using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Security.Cryptography;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Ordering;
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
    [InlineData("CUSTOM", false, false, false, false, true)]
    [InlineData("STOCK", false, false, false, false, true)]
    public async Task WebPaymentWaitsForPhysicalPickupAndSupportsCancellation(string kind, bool cancel, bool expire, bool late, bool fault, bool shopBuyer = false)
    {
        using var app = factory.WithWebHostBuilder(_ => { });
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid(); var user = Guid.NewGuid(); var product = Guid.NewGuid();
        var kiosk = Guid.NewGuid(); var slot = Guid.NewGuid(); var customer = Guid.NewGuid(); var stranger = Guid.NewGuid();
        var key = "web-test-" + kiosk;
        await using var setup = source.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Kiosk payment test','0901112233','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@user,@user::text || '@example.invalid','!unprovisioned','Test shop','SELLER',@seller);
            INSERT INTO identity.users(id,email,full_name,role) VALUES(@customer,@customer::text || '@example.invalid','Customer','CUSTOMER'),(@stranger,@stranger::text || '@example.invalid','Other customer','CUSTOMER');
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status)
            VALUES(@product,@seller,'Test bouquet',350000,48,'ACTIVE');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,api_key_hash,last_heartbeat_at)
            VALUES(@kiosk,@kiosk::text,'Test kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE',@hash,CURRENT_TIMESTAMP);
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel) VALUES(@slot,@kiosk,'A01',0);
            SELECT flow.assign_slot(@slot,@seller,'10000000-0000-0000-0000-000000000001');
            """);
        foreach (var (name, fixtureId) in new[] { ("seller", seller), ("user", user), ("product", product), ("kiosk", kiosk), ("slot", slot), ("customer", customer), ("stranger", stranger) }) setup.Parameters.AddWithValue(name, fixtureId);
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
        if (shopBuyer)
            await Sql("UPDATE identity.users SET role='SELLER',seller_id=@seller,password_hash='!unprovisioned' WHERE id=@customer", ("seller", seller), ("customer", customer));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = customer, Role = shopBuyer ? "SELLER" : "CUSTOMER", SellerId = shopBuyer ? seller : null, PasswordHash = shopBuyer ? "!unprovisioned" : null, FullName = "Member" }));
        using var shop = app.CreateClient();
        shop.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = user, SellerId = seller, Role = "SELLER", FullName = "Shop" }));
        using var foreign = app.CreateClient();
        foreign.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = stranger, Role = "CUSTOMER", FullName = "Other" }));
        var id = Guid.NewGuid(); var pickup = DateTimeOffset.UtcNow.AddHours(3);
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
        if (late) await Sql("UPDATE ordering.orders SET created_at=public.app_now()-interval '1 hour' WHERE id=@id", ("id", row.OrderId!.Value));
        if (fault) await Sql("UPDATE kiosk_ops.slots SET status='FAULT' WHERE id=@id", ("id", slot));
        await Sql("SELECT flow.checkout_paid(@checkout,@txn,true,350000)", ("checkout", row.CheckoutId!.Value), ("txn", "web-payment-" + id));
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
        if (kind == "CUSTOM")
        {
            await Sql("UPDATE ordering.web_requests SET pickup_at=public.app_now()+interval '1 hour',pickup_before=public.app_now()+interval '3 hours' WHERE id=@id", ("id", id));
            (await shop.PostAsJsonAsync(sellerPath + "/fulfill", new { slotId = slot, qrCode = "custom-" + id })).EnsureSuccessStatusCode();
            (await shop.PostAsJsonAsync(sellerPath + "/fulfill", new { slotId = slot, qrCode = "custom-" + id })).EnsureSuccessStatusCode();
        }
        using var device = app.CreateClient(); device.DefaultRequestHeaders.Add("X-Kiosk-Key", key);
        var pickupPath = $"/api/kiosks/{kiosk}/flows/request_pickup";
        if (kind == "CUSTOM") Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(pickupPath, new { p_order = row.OrderId, p_tracking = row.TrackingToken })).StatusCode);
        await Sql("UPDATE ordering.web_requests SET pickup_at=public.app_now()-interval '1 minute' WHERE id=@id", ("id", id));
        (await device.PostAsJsonAsync(pickupPath, new { p_order = row.OrderId, p_tracking = "wrong-code" })).EnsureSuccessStatusCode();
        Assert.Equal(0L, await Sql("SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@id", ("id", row.OrderId.Value)));
        (await device.PostAsJsonAsync(pickupPath, new { p_order = row.OrderId, p_tracking = row.TrackingToken })).EnsureSuccessStatusCode();
        Assert.Equal(1L, await Sql("SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@id", ("id", row.OrderId.Value)));
    }
}

