using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Ordering;
using FloraBot.Api.Modules.Payment;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class WebPreorderTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("CUSTOM", false, false, false, false)]
    [InlineData("STOCK", false, false, false, false)]
    [InlineData("CUSTOM", true, false, false, false)]
    [InlineData("STOCK", true, false, false, false)]
    [InlineData("CUSTOM", false, true, false, false)]
    [InlineData("CUSTOM", false, false, true, false)]
    [InlineData("STOCK", false, false, true, false)]
    [InlineData("STOCK", false, false, false, true)]
    public async Task WebPaymentWaitsForPhysicalPickupAndSupportsCancellation(string kind, bool cancel, bool expire, bool late, bool fault)
    {
        var seller = Guid.NewGuid();
        var secretRef = "channel-" + seller.ToString("N");
        using var gateway = new KioskPaymentTests.SellerGateway();
        using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("PAYOS_CLIENT_ID", "test-client");
            builder.UseSetting("PAYOS_API_KEY", "test-api");
            builder.UseSetting("PAYOS_CHECKSUM_KEY", "test-only-checksum-key");
            builder.UseSetting($"PAYOS_CHANNELS:{secretRef}:SellerId", seller.ToString());
            builder.UseSetting("PAYOS_RETURN_URL", "https://example.invalid/return");
            builder.UseSetting("PAYOS_CANCEL_URL", "https://example.invalid/cancel");
            builder.UseSetting($"PAYOS_CHANNELS:{secretRef}:ClientId", KioskPaymentTests.SellerGateway.ClientId);
            builder.UseSetting($"PAYOS_CHANNELS:{secretRef}:ApiKey", "test-api");
            builder.UseSetting($"PAYOS_CHANNELS:{secretRef}:ChecksumKey", KioskPaymentTests.SellerGateway.ChecksumKey);
            builder.UseSetting($"PAYOS_CHANNELS:{secretRef}:ExpectedBin", "970415");
            builder.UseSetting($"PAYOS_CHANNELS:{secretRef}:ExpectedAccount", "1010099887766");
            builder.UseSetting($"PAYOS_CHANNELS:{secretRef}:ExpectedName", "TIEM HOA TEST");
            builder.ConfigureServices(services => services.AddSingleton<IPayOsClientResolver>(sp =>
                PayOsClientResolver.ForTest(new HttpClient(gateway, disposeHandler: false), sp.GetRequiredService<IConfiguration>())));
        });
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var user = Guid.NewGuid(); var product = Guid.NewGuid();
        var kiosk = Guid.NewGuid();
        var walkSlotId = Guid.NewGuid();
        var preorderSlotId = Guid.NewGuid();
        var slot = kind == "CUSTOM" ? preorderSlotId : walkSlotId;
        var customer = Guid.NewGuid(); var stranger = Guid.NewGuid();
        var key = "web-test-" + kiosk;
        await using var setup = source.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Kiosk payment test','0901112233','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@user,@user::text || '@example.invalid','!unprovisioned','Test shop','SELLER',@seller);
            INSERT INTO identity.users(id,email,full_name,role) VALUES(@customer,@customer::text || '@example.invalid','Customer','CUSTOMER'),(@stranger,@stranger::text || '@example.invalid','Other customer','CUSTOMER');
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status)
            VALUES(@product,@seller,'Test bouquet',350000,48,'ACTIVE');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,api_key_hash,last_heartbeat_at,latitude,longitude)
            VALUES(@kiosk,@kiosk::text,'Test kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE',@hash,CURRENT_TIMESTAMP,10.771918,106.698347);
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,zone)
            VALUES(@walkSlot,@kiosk,'A01',0,'WALK_IN'),
                  (@preorderSlot,@kiosk,'A02',1,'PREORDER');
            INSERT INTO payment.payment_channel_versions (
                id, seller_id, version, secret_ref, expected_bank_bin, expected_bank_account,
                expected_bank_name, state, verified_at
            ) VALUES (
                public.uuid_v7(), @seller, 1, @secretRef, '970415', '1010099887766',
                'TIEM HOA TEST', 'ACTIVE', public.app_now()
            );
            """ + "\n" + CurrentMainKioskFixture.AssignKioskWithLeaseSql);
        foreach (var (name, fixtureId) in new[] { ("seller", seller), ("user", user), ("product", product), ("kiosk", kiosk), ("walkSlot", walkSlotId), ("preorderSlot", preorderSlotId), ("customer", customer), ("stranger", stranger) }) setup.Parameters.AddWithValue(name, fixtureId);
        setup.Parameters.AddWithValue("secretRef", secretRef);
        setup.Parameters.AddWithValue("hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant());
        await setup.ExecuteNonQueryAsync();

        async Task<object?> Sql(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = source.CreateCommand(sql);
            foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
            return await command.ExecuteScalarAsync();
        }
        Guid? bouquet = kind == "STOCK" ? (Guid)(await Sql("SELECT flow.stock_bouquet(flow.new_batch(),@product,@slot,@user,@qr)", ("product", product), ("slot", slot), ("user", user), ("qr", "web-" + product)))! : null;
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = customer, Role = "CUSTOMER", FullName = "Member" }));
        using var shop = app.CreateClient();
        shop.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = user, SellerId = seller, Role = "SELLER", FullName = "Shop" }));
        using var foreign = app.CreateClient();
        foreign.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = stranger, Role = "CUSTOMER", FullName = "Other" }));
        var id = Guid.NewGuid(); var pickup = DateTimeOffset.UtcNow.AddHours(3);
        if (kind == "STOCK")
        {
            var checkoutId = (Guid)(await Sql("SELECT flow.kiosk_checkout(@kiosk, ARRAY[@bouquet]::uuid[], @customer)",
                ("kiosk", kiosk), ("bouquet", bouquet!), ("customer", customer)))!;
            await Sql("""
                INSERT INTO ordering.web_requests(
                    id, customer_id, seller_id, product_id, kiosk_id, kind, instructions,
                    pickup_at, pickup_before, quoted_price, state, order_id, request_payload, display_snapshot
                )
                VALUES (
                    @id, @customer, @seller, @product, @kiosk, 'STOCK', @instructions,
                    @pickup, @pickup + interval '2 hours', 350000, 'ORDERED',
                    (SELECT id FROM ordering.orders WHERE checkout_id = @checkout),
                    jsonb_build_object('bouquet', @bouquet, 'pickup', @pickup),
                    jsonb_build_object('product', 'Test bouquet', 'shop', 'Kiosk payment test', 'kiosk', 'Test kiosk', 'address', 'Test address')
                );
                """,
                ("id", id), ("customer", customer), ("seller", seller), ("product", product),
                ("kiosk", kiosk), ("instructions", "Pink flowers for a birthday"),
                ("pickup", pickup.UtcDateTime), ("bouquet", bouquet!), ("checkout", checkoutId));
        }
        var input = new { id, kind, productId = product, kioskId = kiosk, bouquetId = bouquet, pickupAt = pickup, instructions = "Pink flowers for a birthday" };
        (await client.PostAsJsonAsync("/api/member/preorders", input)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/member/preorders", input)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/member/preorders", new { id, kind, productId = product, kioskId = kiosk, bouquetId = bouquet, pickupAt = pickup.AddHours(1), instructions = "Pink flowers for a birthday" })).StatusCode);
        var path = "/api/member/preorders/" + id;
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync(path + "/accept", new { price = 350000, pickupAt = pickup, note = "Pink bouquet confirmed" })).StatusCode);
        var sellerPath = $"/api/sellers/{seller}/preorders/{id}";
        if (kind == "CUSTOM")
        {
            (await shop.PostAsJsonAsync(sellerPath + "/quote", new { price = 350000, pickupAt = pickup, note = "Pink bouquet confirmed", reject = false })).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/accept", new { price = 340000, pickupAt = pickup, note = "Pink bouquet confirmed" })).StatusCode);
            (await client.PostAsJsonAsync(path + "/accept", new { price = 350000, pickupAt = pickup, note = "Pink bouquet confirmed" })).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync(path + "/accept", new { price = 350000, pickupAt = pickup, note = "Pink bouquet confirmed" })).EnsureSuccessStatusCode();
        }
        var list = await client.GetFromJsonAsync<WebRequestPage>("/api/member/preorders");
        var row = Assert.Single(list!.Items);
        Assert.NotNull(row.OrderId); Assert.NotNull(row.TrackingToken);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsync(path + "/payment-link", null)).StatusCode);
        using var linkResponse = await client.PostAsync(path + "/payment-link", null);
        linkResponse.EnsureSuccessStatusCode();
        var link = await linkResponse.Content.ReadFromJsonAsync<PaymentLinkResponse>();
        Assert.Equal("TIEM HOA TEST", link!.RecipientName);
        Assert.Equal(350000, link.Amount);
        Assert.Equal("SELLER_DIRECT", await Sql("SELECT funding_provenance::text FROM payment.payments WHERE checkout_id=@checkout AND kind='CHARGE' AND seller_id=@seller",
            ("checkout", row.CheckoutId!.Value), ("seller", seller)));
        if (late) await Sql("UPDATE ordering.orders SET created_at=public.app_now()-interval '1 hour' WHERE id=@id", ("id", row.OrderId!.Value));
        if (fault) await Sql("UPDATE kiosk_ops.slots SET status='FAULT' WHERE id=@id", ("id", slot));
        var callback = JsonSerializer.SerializeToElement(new
        {
            orderCode = link.OrderCode,
            amount = link.Amount,
            reference = "web-payment-" + id,
            code = "00",
            accountNumber = "1010099887766"
        });
        using var confirmed = await client.PostAsJsonAsync("/api/payments/webhook",
            new { data = callback, signature = PayOsChecksum.Sign(callback, KioskPaymentTests.SellerGateway.ChecksumKey) });
        confirmed.EnsureSuccessStatusCode();
        Assert.Equal(0L, await Sql("SELECT count(*) FROM payment.ledger_entries WHERE ref_type='ORDER' AND ref_id=@order", ("order", row.OrderId!.Value)));
        if (late || fault)
        {
            Assert.Equal(late ? "EXPIRED" : "DISPENSE_FAILED", await Sql("SELECT status FROM ordering.orders WHERE id=@id", ("id", row.OrderId!.Value)));
            Assert.Equal(1L, await Sql("SELECT count(*) FROM payment.payments WHERE order_id=@id AND kind='REFUND'", ("id", row.OrderId.Value)));
            Assert.Equal(0L, await Sql("SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@id", ("id", row.OrderId.Value))); return;
        }
        if (expire)
        {
            await Sql("UPDATE ordering.web_requests SET pickup_at=public.app_now()-interval '3 hours',pickup_before=public.app_now()-interval '1 hour' WHERE id=@id", ("id", id));
            await Sql("SELECT flow.expire_web_orders()");
            Assert.Equal("DISPENSE_FAILED", await Sql("SELECT status FROM ordering.orders WHERE id=@id", ("id", row.OrderId!.Value))); return;
        }
        Assert.Equal("PAID", await Sql("SELECT status FROM ordering.orders WHERE id=@id", ("id", row.OrderId!.Value)));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@id", ("id", row.OrderId.Value)));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/cancel", new { reason = "Member requests cancellation" })).StatusCode);
        if (cancel)
        {
            (await shop.PostAsJsonAsync(sellerPath + "/cancel", new { reason = "Shop cannot prepare the flowers" })).EnsureSuccessStatusCode();
            Assert.Equal("DISPENSE_FAILED", await Sql("SELECT status FROM ordering.orders WHERE id=@id", ("id", row.OrderId.Value)));
            Assert.Equal(1L, await Sql("SELECT count(*) FROM payment.payments WHERE order_id=@id AND kind='REFUND'", ("id", row.OrderId.Value)));
            return;
        }
        using var device = app.CreateClient(); device.DefaultRequestHeaders.Add("X-Kiosk-Key", key);
        var pickupPath = $"/api/kiosks/{kiosk}/flows/request_pickup";
        if (kind == "CUSTOM")
        {
            await Sql("UPDATE ordering.web_requests SET pickup_at=public.app_now()+interval '1 hour',pickup_before=public.app_now()+interval '3 hours' WHERE id=@id", ("id", id));
            (await shop.PostAsJsonAsync(sellerPath + "/fulfill", new { slotId = slot, qrCode = "custom-" + id })).EnsureSuccessStatusCode();
            (await shop.PostAsJsonAsync(sellerPath + "/fulfill", new { slotId = slot, qrCode = "custom-" + id })).EnsureSuccessStatusCode();

            // Receipt tracking token lookup must not bypass email OTP for preorders
            Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync(pickupPath, new { p_order = row.OrderId, p_tracking = row.TrackingToken })).StatusCode);
            Assert.Equal(0L, await Sql("SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@id", ("id", row.OrderId!.Value)));

            // Request 8-digit pickup OTP via seller API
            (await shop.PostAsync($"/api/me/preorders/{id}/pickup-otp", null)).EnsureSuccessStatusCode();
            var customerEmail = customer.ToString() + "@example.invalid";
            var sentMail = factory.EmailSender.SentEmails.Last(e => string.Equals(e.Recipient, customerEmail, StringComparison.OrdinalIgnoreCase));
            var otpCode = Regex.Match(sentMail.Text, @"\b\d{8}\b").Value;
            Assert.Equal(8, otpCode.Length);

            // Wrong OTP rejected, no unlock token issued
            var wrongAttempt = Guid.NewGuid();
            var wrongCode = otpCode == "00000000" ? "11111111" : "00000000";
            var wrongOtpResp = await device.PostAsJsonAsync($"/api/kiosks/{kiosk}/preorders/pickup-otp/open", new { code = wrongCode, attemptId = wrongAttempt });
            Assert.Equal(HttpStatusCode.Unauthorized, wrongOtpResp.StatusCode);
            Assert.Equal(0L, await Sql("SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@id", ("id", row.OrderId!.Value)));

            // Valid OTP opens slot even before scheduled pickup window (early pickup allowed when ready)
            var openAttempt = Guid.NewGuid();
            var openResp = await device.PostAsJsonAsync($"/api/kiosks/{kiosk}/preorders/pickup-otp/open", new { code = otpCode, attemptId = openAttempt });
            openResp.EnsureSuccessStatusCode();
            var opened = await openResp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.True(opened.TryGetProperty("watchToken", out _));

            // Idempotent retry returns same command, single token issued
            var replayResp = await device.PostAsJsonAsync($"/api/kiosks/{kiosk}/preorders/pickup-otp/open", new { code = otpCode, attemptId = openAttempt });
            replayResp.EnsureSuccessStatusCode();
            Assert.Equal(1L, await Sql("SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@id", ("id", row.OrderId!.Value)));

            // Order is still DISPENSING, not COMPLETED without physical door events
            Assert.Equal("DISPENSING", await Sql("SELECT status FROM ordering.orders WHERE id=@id", ("id", row.OrderId!.Value)));
        }
        else
        {
            // Historical legacy STOCK preorder compatibility:
            // Current main supports historical retry, read, cancellation, and refund only.
            // Physical delivery/pickup is not supported for legacy STOCK preorders because
            // receipt tracking lookup rejects web orders and OTP requires CUSTOM + PREORDER zone.
            Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync(pickupPath, new { p_order = row.OrderId, p_tracking = row.TrackingToken })).StatusCode);
            Assert.Equal(0L, await Sql("SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@id", ("id", row.OrderId!.Value)));
        }
    }
}

