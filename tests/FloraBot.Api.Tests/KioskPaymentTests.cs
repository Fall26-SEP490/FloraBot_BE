using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Payment;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class KioskPaymentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, true, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, false, false, true, true)]
    public async Task CheckoutPaymentDispensesOnceAndProtectsKioskAndCustomer(bool signedIn, bool failDoor, bool reportFault, bool viaInbox, bool recoverAfterOffline)
    {
        var gateway = new Gateway();
        using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("PAYOS_CLIENT_ID", "test-client"); builder.UseSetting("PAYOS_API_KEY", "test-api");
            builder.UseSetting("PAYOS_RETURN_URL", "https://example.invalid/return"); builder.UseSetting("PAYOS_CANCEL_URL", "https://example.invalid/cancel");
            builder.ConfigureServices(services => services.AddHttpClient<PayOsClient>().ConfigurePrimaryHttpMessageHandler(() => gateway));
        });
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid(); var user = Guid.NewGuid(); var product = Guid.NewGuid();
        var kiosk = Guid.NewGuid(); var slot = Guid.NewGuid(); var customer = Guid.NewGuid(); var stranger = Guid.NewGuid();
        var key = "kiosk-test-" + kiosk;
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
        foreach (var (name, id) in new[] { ("seller", seller), ("user", user), ("product", product), ("kiosk", kiosk), ("slot", slot), ("customer", customer), ("stranger", stranger) }) setup.Parameters.AddWithValue(name, id);
        setup.Parameters.AddWithValue("hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant());
        await setup.ExecuteNonQueryAsync();
        await using var stock = source.CreateCommand("SELECT flow.stock_bouquet(flow.new_batch(),@product,@slot,@user,@qr)");
        stock.Parameters.AddWithValue("product", product); stock.Parameters.AddWithValue("slot", slot); stock.Parameters.AddWithValue("user", user); stock.Parameters.AddWithValue("qr", "test-" + product);
        var bouquet = (Guid)(await stock.ExecuteScalarAsync())!;
        using var device = app.CreateClient(); device.DefaultRequestHeaders.Add("X-Kiosk-Key", key);
        using var client = app.CreateClient();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        if (signedIn) client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = customer, Role = "CUSTOMER", FullName = "Test" }, kioskId: kiosk.ToString()));
        else client.DefaultRequestHeaders.Add("X-Kiosk-Key", key);
        var catalog = await client.GetFromJsonAsync<List<FloraBot.Api.Infrastructure.KioskCatalogResponse>>($"/api/kiosks/{kiosk}/catalog/items");
        Assert.Single(catalog!); Assert.Equal(bouquet, catalog![0].BouquetId); Assert.Equal(350000, catalog[0].Price);
        var checkoutResponse = await client.PostAsJsonAsync($"/api/kiosks/{kiosk}/flows/kiosk_checkout", new { p_bouquets = new[] { bouquet } });
        checkoutResponse.EnsureSuccessStatusCode();
        var checkout = (await checkoutResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        var path = $"/api/kiosks/{kiosk}/checkouts/{checkout}";
        var pending = await client.GetFromJsonAsync<CheckoutResponse>(path);
        Assert.Equal("PENDING", pending!.PaymentStatus); Assert.Equal(350000, pending.Amount);
        Assert.Single(pending.Orders); Assert.Equal("AWAITING_PAYMENT", pending.Orders[0].Status);
        using var foreign = app.CreateClient(); foreign.DefaultRequestHeaders.Add("X-Kiosk-Key", "demo-key-q1");
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.PostAsync(path + "/payment-link", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/kiosks/40000000-0000-0000-0000-000000000001/checkouts/{checkout}")).StatusCode);
        using var otherCustomer = app.CreateClient();
        otherCustomer.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = stranger, Role = "CUSTOMER", FullName = "Other" }, kioskId: kiosk.ToString()));
        Assert.Equal(HttpStatusCode.NotFound, (await otherCustomer.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherCustomer.PostAsync(path + "/payment-link", null)).StatusCode);
        var linkResponse = await client.PostAsync(path + "/payment-link", null);
        linkResponse.EnsureSuccessStatusCode();
        var link = await linkResponse.Content.ReadFromJsonAsync<PaymentLinkResponse>();
        Assert.Equal(350000, link!.Amount);
        Assert.Equal(new DateTimeOffset(pending.PayBefore).ToUnixTimeSeconds(), gateway.Deadline);
        var data = JsonSerializer.SerializeToElement(new { orderCode = link.OrderCode, amount = 350000, reference = "test-" + checkout, code = "00" });
        var envelope = new { data, signature = PayOsChecksum.Sign(data, "test-only-checksum-key"), code = "00", desc = "success", success = true };
        if (recoverAfterOffline)
        {
            await using var offline = source.CreateCommand("UPDATE kiosk_ops.kiosks SET status='OFFLINE' WHERE id=@id");
            offline.Parameters.AddWithValue("id", kiosk); await offline.ExecuteNonQueryAsync();
        }
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostAsJsonAsync("/api/payments/webhook", envelope)));
        foreach (var response in responses) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paid = await client.GetFromJsonAsync<CheckoutResponse>(path);
        if (recoverAfterOffline)
        {
            Assert.Equal("SUCCEEDED", paid!.PaymentStatus); Assert.Equal("PAID", paid.Orders[0].Status);
            var pickupPath = $"/api/kiosks/{kiosk}/flows/request_pickup";
            var pickup = new { p_order = paid.Orders[0].Id, p_tracking = paid.Orders[0].TrackingToken };
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(pickupPath, pickup)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await foreign.PostAsJsonAsync(pickupPath, pickup)).StatusCode);
            await using var commands = source.CreateCommand("SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@id");
            commands.Parameters.AddWithValue("id", pickup.p_order); Assert.Equal(0L, await commands.ExecuteScalarAsync());
            await using var online = source.CreateCommand("UPDATE kiosk_ops.kiosks SET status='ONLINE',last_heartbeat_at=CURRENT_TIMESTAMP WHERE id=@id");
            online.Parameters.AddWithValue("id", kiosk); await online.ExecuteNonQueryAsync();
            var rejected = await client.PostAsJsonAsync(pickupPath, new { pickup.p_order, p_tracking = "!!!!!!!!" });
            rejected.EnsureSuccessStatusCode(); Assert.True(rejected.Headers.CacheControl?.NoStore);
            Assert.Equal(JsonValueKind.Null, (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").ValueKind);
            Assert.Equal(0L, await commands.ExecuteScalarAsync());
            await using var audit = source.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE action='PICKUP_CODE_REJECTED' AND entity_id=@id AND actor_id=@kiosk");
            audit.Parameters.AddWithValue("id", pickup.p_order); audit.Parameters.AddWithValue("kiosk", kiosk);
            Assert.Equal(1L, await audit.ExecuteScalarAsync());
            var accepted = await client.PostAsJsonAsync(pickupPath, pickup); accepted.EnsureSuccessStatusCode();
            var firstCommand = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
            Assert.Equal(1L, await commands.ExecuteScalarAsync());
            // Emulate the terminal expiry state for only this fixture, not a global job sweep.
            await using var expireCommand = source.CreateCommand("UPDATE kiosk_ops.unlock_tokens SET status='EXPIRED' WHERE cmd_id=@cmd");
            expireCommand.Parameters.AddWithValue("cmd", firstCommand); await expireCommand.ExecuteNonQueryAsync();
            var replacement = await client.PostAsJsonAsync(pickupPath, pickup); replacement.EnsureSuccessStatusCode();
            var replacementCommand = (await replacement.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
            Assert.NotEqual(firstCommand, replacementCommand); Assert.Equal(2L, await commands.ExecuteScalarAsync());
            paid = await client.GetFromJsonAsync<CheckoutResponse>(path);
        }
        Assert.Equal("SUCCEEDED", paid!.PaymentStatus); Assert.Equal("DISPENSING", paid.Orders[0].Status);
        using var receiptClient = app.CreateClient();
        var receiptInput = new { orderId = paid.Orders[0].Id, trackingToken = paid.Orders[0].TrackingToken };
        var receiptResponse = await receiptClient.PostAsJsonAsync("/api/receipts/lookup", receiptInput);
        receiptResponse.EnsureSuccessStatusCode();
        Assert.True(receiptResponse.Headers.CacheControl?.NoStore);
        var receiptJson = await receiptResponse.Content.ReadAsStringAsync();
        foreach (var privateField in new[] { "trackingToken", "customerId", "sellerId", "refundBank", "ecardContent", "accountEnc" })
            Assert.DoesNotContain(privateField, receiptJson, StringComparison.OrdinalIgnoreCase);
        var receipt = (await receiptResponse.Content.ReadFromJsonAsync<FloraBot.Api.Modules.Ordering.ReceiptResponse>())!;
        Assert.Equal("DISPENSING", receipt.Status);
        Assert.False(receipt.CanComplain);
        Assert.Null(receipt.DisputeDeadline);
        Assert.Equal(350000, receipt.Total);
        Assert.Equal("Test bouquet", Assert.Single(receipt.Items).Name);
        Assert.Equal(0, receipt.Refunds.PendingAmount);
        foreach (var invalid in new[] {
            new { orderId = paid.Orders[0].Id, trackingToken = "!!!!!!!!" },
            new { orderId = Guid.NewGuid(), trackingToken = paid.Orders[0].TrackingToken },
            new { orderId = paid.Orders[0].Id, trackingToken = new string(paid.Orders[0].TrackingToken[0] == 'A' ? 'B' : 'A', 8) }
        })
        {
            var denied = await receiptClient.PostAsJsonAsync("/api/receipts/lookup", invalid);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.True(denied.Headers.CacheControl?.NoStore);
        }
        await using var events = source.CreateCommand("""
            SELECT "Content"::jsonb->'Value' FROM cap.published
            WHERE "Name"=@name AND "Content"::jsonb->'Value'->>'checkout_id'=@id
            """);
        events.Parameters.AddWithValue("name", FloraBot.Api.Modules.Ordering.OrderEvents.Paid);
        events.Parameters.AddWithValue("id", checkout.ToString());
        await using (var eventReader = await events.ExecuteReaderAsync())
        {
            Assert.True(await eventReader.ReadAsync());
            var payload = JsonSerializer.Deserialize<JsonElement>(eventReader.GetString(0));
            Assert.Equal(paid.Orders[0].Id, payload.GetProperty("order_id").GetGuid());
            Assert.Equal(seller, payload.GetProperty("seller_id").GetGuid());
            Assert.Equal(kiosk, payload.GetProperty("kiosk_id").GetGuid());
            var item = Assert.Single(payload.GetProperty("items").EnumerateArray());
            Assert.Equal(slot, item.GetProperty("slot_id").GetGuid());
            Assert.Equal(bouquet, item.GetProperty("bouquet_id").GetGuid());
            Assert.Equal(1, item.GetProperty("quantity").GetInt32());
            Assert.False(await eventReader.ReadAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(path + "/payment-link", null)).StatusCode);
        await using var command = source.CreateCommand("SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@id AND status='ISSUED'");
        command.Parameters.AddWithValue("id", paid.Orders[0].Id);
        var cmd = (Guid)(await command.ExecuteScalarAsync())!;
        var deviceKey = RandomNumberGenerator.GetBytes(32);
        var keyRing = new FloraBot.Api.Modules.KioskOps.DeviceKeyRing(new Dictionary<string, byte[]> { [kiosk.ToString()] = deviceKey });
        var dispatcher = new FloraBot.Api.Infrastructure.DeviceCommandDispatcher(source,
            scope.ServiceProvider.GetRequiredService<DotNetCore.CAP.ICapPublisher>(), keyRing);
        if (failDoor)
        {
            if (viaInbox)
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var prepared = await dispatcher.PrepareAsync(cmd, CancellationToken.None);
                    Assert.NotNull(prepared);
                    Assert.True(await dispatcher.DeliverAsync(prepared, (_, _) => Task.CompletedTask, CancellationToken.None));
                    await using var retry = source.CreateCommand("UPDATE kiosk_ops.mqtt_dispatches SET attempted_at=CURRENT_TIMESTAMP-interval '20 seconds',next_attempt_at=CURRENT_TIMESTAMP-interval '10 seconds' WHERE cmd_id=@id");
                    retry.Parameters.AddWithValue("id", cmd);
                    await retry.ExecuteNonQueryAsync();
                }
                Assert.Null(await dispatcher.PrepareAsync(cmd, CancellationToken.None));
                Assert.Null(await dispatcher.PrepareAsync(cmd, CancellationToken.None));
            }
            else if (reportFault)
            {
                using var merchant = app.CreateClient();
                merchant.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = user, Role = "SELLER", SellerId = seller, FullName = "Test shop" }, "ACTIVE"));
                for (var attempt = 0; attempt < 2; attempt++)
                    (await merchant.PostAsJsonAsync($"/api/sellers/{seller}/flows/report_device_fault", new
                    {
                        p_kiosk = kiosk,
                        p_slot = slot,
                        p_desc = "Door fault fixture",
                        p_photo = EvidenceFixture.Reference(app.Services, "report_device_fault", PrivateEvidence.SellerBinding(user.ToString(), seller, slot))
                    })).EnsureSuccessStatusCode();
            }
            else
            {
                for (var attempt = 0; attempt < 2; attempt++)
                    (await device.PostAsJsonAsync($"/api/kiosks/{kiosk}/flows/device_event", new { p_cmd = cmd, p_event = "SENT" })).EnsureSuccessStatusCode();
                var failures = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => device.PostAsJsonAsync($"/api/kiosks/{kiosk}/flows/device_event", new { p_cmd = cmd, p_event = "FAILED" })));
                foreach (var failure in failures) failure.EnsureSuccessStatusCode();
            }
            var failedOrder = await client.GetFromJsonAsync<CheckoutResponse>(path);
            Assert.Equal("DISPENSE_FAILED", failedOrder!.Orders[0].Status);
            var failedReceipt = (await (await receiptClient.PostAsJsonAsync("/api/receipts/lookup", receiptInput)).Content.ReadFromJsonAsync<FloraBot.Api.Modules.Ordering.ReceiptResponse>())!;
            Assert.Equal("DISPENSE_FAILED", failedReceipt.Status);
            Assert.Equal(350000, failedReceipt.Refunds.PendingAmount);
            Assert.Equal(0, failedReceipt.Refunds.PaidAmount);
            Assert.False(failedReceipt.RefundInformationProvided);
            Assert.False(failedReceipt.CanComplain);
            const string accountNumber = "001234567890";
            var refundPath = "/api/receipts/flows/submit_refund_info";
            foreach (var wrong in new[] { (paid.Orders[0].Id, "!!!!!!!!"), (Guid.NewGuid(), receiptInput.trackingToken) })
            {
                var rejected = await receiptClient.PostAsJsonAsync(refundPath, new { p_order = wrong.Item1, p_tracking = wrong.Item2, p_bank = "ACB", p_account_enc = accountNumber, p_holder = "Nguyen Van A" });
                Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
                Assert.True(rejected.Headers.CacheControl?.NoStore);
            }
            foreach (var invalidAccount in new string?[] { null, "", "123 456", "x\nabc" })
            {
                var rejected = await receiptClient.PostAsJsonAsync(refundPath, new { p_order = receiptInput.orderId, p_tracking = receiptInput.trackingToken, p_bank = "ACB", p_account_enc = invalidAccount, p_holder = "Nguyen Van A" });
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
                Assert.True(rejected.Headers.CacheControl?.NoStore);
            }
            var submitted = await receiptClient.PostAsJsonAsync(refundPath, new { p_order = receiptInput.orderId, p_tracking = " " + receiptInput.trackingToken.ToLowerInvariant() + " ", p_bank = " ACB ", p_account_enc = " " + accountNumber + " ", p_holder = " Nguyen Van A " });
            submitted.EnsureSuccessStatusCode();
            Assert.True(submitted.Headers.CacheControl?.NoStore);
            Assert.DoesNotContain(accountNumber, await submitted.Content.ReadAsStringAsync());
            await using (var bank = source.CreateCommand("SELECT refund_bank_name,refund_bank_account_enc,refund_bank_holder FROM ordering.orders WHERE id=@id"))
            {
                bank.Parameters.AddWithValue("id", receiptInput.orderId);
                await using var bankReader = await bank.ExecuteReaderAsync();
                Assert.True(await bankReader.ReadAsync());
                Assert.Equal("ACB", bankReader.GetString(0));
                Assert.NotEqual(accountNumber, bankReader.GetString(1));
                Assert.Equal(accountNumber, scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("bank-account-v1").Unprotect(bankReader.GetString(1)));
                Assert.Equal("NGUYEN VAN A", bankReader.GetString(2));
            }
            var savedReceipt = (await (await receiptClient.PostAsJsonAsync("/api/receipts/lookup", receiptInput)).Content.ReadFromJsonAsync<FloraBot.Api.Modules.Ordering.ReceiptResponse>())!;
            Assert.True(savedReceipt.RefundInformationProvided);
            Assert.Equal(350000, savedReceipt.Refunds.PendingAmount);
            var failureEvent = Assert.Single(await OrderEvents(source, FloraBot.Api.Modules.Ordering.OrderEvents.Failed, paid.Orders[0].Id));
            Assert.Equal(slot, failureEvent.GetProperty("slot_id").GetGuid());
            var refundEvent = Assert.Single(await OrderEvents(source, RefundEvents.Approved, paid.Orders[0].Id));
            Assert.Equal(350000, refundEvent.GetProperty("amount").GetInt64());
            Assert.Empty(await OrderEvents(source, FloraBot.Api.Modules.KioskOps.DeviceEvents.PickedUp, paid.Orders[0].Id));
            using var incidentAdmin = app.CreateClient();
            incidentAdmin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
            var incidentPage = (await incidentAdmin.GetFromJsonAsync<FloraBot.Api.Modules.Ordering.AdminIncidentPage>($"/api/admin/incidents?kioskId={kiosk}"))!;
            var incident = Assert.Single(incidentPage.Items, x => x.Kind == "DISPENSE_FAILED");
            var prematureClose = await incidentAdmin.PostAsJsonAsync("/api/admin/flows/resolve_device_fault", new { p_dispute = incident.Id, p_note = "Refund is still pending" });
            Assert.Equal(HttpStatusCode.Conflict, prematureClose.StatusCode);
            var stillOpen = (await incidentAdmin.GetFromJsonAsync<FloraBot.Api.Modules.Ordering.AdminIncidentDetail>($"/api/admin/incidents/{incident.Id}"))!;
            Assert.Equal("OPEN", stillOpen.Incident.Status);
            return;
        }
        foreach (var evt in new[] { "SENT", "ACK", "OPENED", "CLOSED" })
        {
            if (viaInbox && evt == "SENT")
            {
                var prepared = await dispatcher.PrepareAsync(cmd, CancellationToken.None);
                Assert.NotNull(prepared);
                Assert.Equal(slot, prepared.Command.SlotId);
                Assert.True(await dispatcher.DeliverAsync(prepared, (_, _) => Task.CompletedTask, CancellationToken.None));
            }
            else if (!viaInbox)
                (await device.PostAsJsonAsync($"/api/kiosks/{kiosk}/flows/device_event", new { p_cmd = cmd, p_event = evt })).EnsureSuccessStatusCode();
            else
            {
                using var ingressScope = app.Services.CreateScope();
                var inbox = new FloraBot.Api.Infrastructure.DeviceEventInbox(source,
                    ingressScope.ServiceProvider.GetRequiredService<DotNetCore.CAP.ICapPublisher>(), keyRing,
                    ingressScope.ServiceProvider.GetRequiredService<FloraBot.Api.Realtime.PortalNotifier>());
                var message = FloraBot.Api.Modules.KioskOps.DeviceProtocol.Sign(new FloraBot.Api.Modules.KioskOps.SignedDeviceEvent(
                    1, kiosk.ToString(), Guid.NewGuid(), cmd, evt, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ""), deviceKey);
                Assert.Equal(FloraBot.Api.Infrastructure.DeviceReceipt.Stored, await inbox.ReceiveAsync(kiosk.ToString(), message, CancellationToken.None));
                Assert.True(await inbox.ProcessAsync(message.EventId, CancellationToken.None));
                Assert.Equal(FloraBot.Api.Infrastructure.DeviceReceipt.Duplicate, await inbox.ReceiveAsync(kiosk.ToString(), message, CancellationToken.None));
                Assert.False(await inbox.ProcessAsync(message.EventId, CancellationToken.None));
            }
            if (recoverAfterOffline && evt == "OPENED")
            {
                var whileOpen = await client.PostAsJsonAsync($"/api/kiosks/{kiosk}/flows/request_pickup", new { p_order = paid.Orders[0].Id, p_tracking = paid.Orders[0].TrackingToken });
                Assert.Equal(HttpStatusCode.Conflict, whileOpen.StatusCode);
                await using var openState = source.CreateCommand("SELECT status FROM kiosk_ops.unlock_tokens WHERE cmd_id=@cmd");
                openState.Parameters.AddWithValue("cmd", cmd); Assert.Equal("OPENED", await openState.ExecuteScalarAsync());
            }
        }
        var completed = await client.GetFromJsonAsync<CheckoutResponse>(path);
        Assert.Equal("COMPLETED", completed!.Orders[0].Status);
        if (recoverAfterOffline)
        {
            var denied = await client.PostAsJsonAsync($"/api/kiosks/{kiosk}/flows/request_pickup", new { p_order = completed.Orders[0].Id, p_tracking = completed.Orders[0].TrackingToken });
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            await using var count = source.CreateCommand("SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@id");
            count.Parameters.AddWithValue("id", completed.Orders[0].Id); Assert.Equal(2L, await count.ExecuteScalarAsync());
        }
        var completedReceipt = (await (await receiptClient.PostAsJsonAsync("/api/receipts/lookup", new { receiptInput.orderId, trackingToken = " " + receiptInput.trackingToken.ToLowerInvariant() + " " })).Content.ReadFromJsonAsync<FloraBot.Api.Modules.Ordering.ReceiptResponse>())!;
        Assert.Equal("COMPLETED", completedReceipt.Status);
        Assert.True(completedReceipt.CanComplain);
        Assert.False(completedReceipt.HasComplaint);
        Assert.NotNull(completedReceipt.CompletedAt);
        Assert.True(completedReceipt.DisputeDeadline > completedReceipt.CompletedAt);
        var noRefund = await receiptClient.PostAsJsonAsync("/api/receipts/flows/submit_refund_info", new { p_order = receiptInput.orderId, p_tracking = receiptInput.trackingToken, p_bank = "ACB", p_account_enc = "001234567890", p_holder = "Nguyen Van A" });
        Assert.Equal(HttpStatusCode.Conflict, noRefund.StatusCode);
        var complaintPath = "/api/receipts/flows/open_dispute";
        await using (var expiredComplaint = source.CreateCommand("UPDATE ordering.orders SET completed_at=public.app_now()-interval '25 hours' WHERE id=@id"))
        {
            expiredComplaint.Parameters.AddWithValue("id", receiptInput.orderId);
            await expiredComplaint.ExecuteNonQueryAsync();
            var expiredReceipt = (await (await receiptClient.PostAsJsonAsync("/api/receipts/lookup", receiptInput)).Content.ReadFromJsonAsync<FloraBot.Api.Modules.Ordering.ReceiptResponse>())!;
            Assert.False(expiredReceipt.CanComplain);
            var expiredAttempt = await receiptClient.PostAsJsonAsync(complaintPath, new { p_order = receiptInput.orderId, p_tracking = receiptInput.trackingToken, p_reason = "Hoa bị dập khi nhận.", p_photo = EvidenceFixture.Reference(app.Services, "open_dispute", PrivateEvidence.ReceiptBinding(receiptInput.orderId, receiptInput.trackingToken)) });
            Assert.Equal(HttpStatusCode.Conflict, expiredAttempt.StatusCode);
            expiredComplaint.CommandText = "UPDATE ordering.orders SET completed_at=@completed WHERE id=@id";
            expiredComplaint.Parameters.AddWithValue("completed", completedReceipt.CompletedAt.Value);
            await expiredComplaint.ExecuteNonQueryAsync();
        }
        foreach (var invalidPhoto in new string?[] { null, "", "http://example.invalid/photo.jpg", "https://user:password@example.invalid/photo.jpg", "javascript:alert(1)" })
        {
            var invalid = await receiptClient.PostAsJsonAsync(complaintPath, new { p_order = receiptInput.orderId, p_tracking = receiptInput.trackingToken, p_reason = "Hoa bị dập khi nhận.", p_photo = invalidPhoto });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.True(invalid.Headers.CacheControl?.NoStore);
        }
        var complaintInput = new { p_order = receiptInput.orderId, p_tracking = receiptInput.trackingToken, p_reason = " Hoa bị dập khi nhận. ", p_photo = EvidenceFixture.Reference(app.Services, "open_dispute", PrivateEvidence.ReceiptBinding(receiptInput.orderId, receiptInput.trackingToken)) };
        var complaints = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => receiptClient.PostAsJsonAsync(complaintPath, complaintInput)));
        Assert.Single(complaints, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(complaints, response => response.StatusCode == HttpStatusCode.Conflict);
        var disputedReceipt = (await (await receiptClient.PostAsJsonAsync("/api/receipts/lookup", receiptInput)).Content.ReadFromJsonAsync<FloraBot.Api.Modules.Ordering.ReceiptResponse>())!;
        Assert.Equal("DISPUTED", disputedReceipt.Status);
        Assert.True(disputedReceipt.HasComplaint);
        Assert.False(disputedReceipt.CanComplain);
        await using (var complaintCheck = source.CreateCommand("SELECT count(*) FROM ordering.disputes WHERE order_id=@id AND kind='COMPLAINT' AND reason='Hoa bị dập khi nhận.'"))
        {
            complaintCheck.Parameters.AddWithValue("id", receiptInput.orderId);
            Assert.Equal(1L, await complaintCheck.ExecuteScalarAsync());
        }
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync($"/api/kiosks/{kiosk}/flows/device_event", new { p_cmd = cmd, p_event = "CLOSED" })).StatusCode);
        var pickupEvent = Assert.Single(await OrderEvents(source, FloraBot.Api.Modules.KioskOps.DeviceEvents.PickedUp, paid.Orders[0].Id));
        Assert.Equal(slot, pickupEvent.GetProperty("slot_id").GetGuid());
        Assert.Equal(bouquet, pickupEvent.GetProperty("bouquet_id").GetGuid());
        Assert.Equal(DateTimeKind.Utc, pickupEvent.GetProperty("picked_up_at").GetDateTime().Kind);
        await using var verify = source.CreateCommand("SELECT count(*),sum(amount)::bigint FROM payment.ledger_entries WHERE ref_type='ORDER' AND ref_id=@id");
        verify.Parameters.AddWithValue("id", paid.Orders[0].Id);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync()); Assert.Equal(2, reader.GetInt64(0)); Assert.Equal(0, reader.GetInt64(1));
        await reader.DisposeAsync();
        stock.Parameters["qr"].Value = "expired-" + product;
        var nextBouquet = (Guid)(await stock.ExecuteScalarAsync())!;
        var nextResponse = await client.PostAsJsonAsync($"/api/kiosks/{kiosk}/flows/kiosk_checkout", new { p_bouquets = new[] { nextBouquet } });
        nextResponse.EnsureSuccessStatusCode();
        var expiredCheckout = (await nextResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        await using var age = source.CreateCommand("UPDATE ordering.orders SET created_at=now()-interval '8 minutes' WHERE checkout_id=@id");
        age.Parameters.AddWithValue("id", expiredCheckout); await age.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/kiosks/{kiosk}/checkouts/{expiredCheckout}/payment-link", null)).StatusCode);
        Assert.Equal(1, gateway.Calls);
        await using var reserveLate = source.CreateCommand("SELECT flow.reserve_gateway_order(id) FROM payment.payments WHERE checkout_id=@id AND kind='CHARGE'");
        reserveLate.Parameters.AddWithValue("id", expiredCheckout);
        var lateCode = (long)(await reserveLate.ExecuteScalarAsync())!;
        await using var expire = source.CreateCommand("""
            UPDATE payment.payments SET status='EXPIRED' WHERE checkout_id=@id AND kind='CHARGE';
            SELECT flow.release_checkout(@id,'EXPIRED');
            """);
        expire.Parameters.AddWithValue("id", expiredCheckout);
        await expire.ExecuteNonQueryAsync();
        var lateData = JsonSerializer.SerializeToElement(new { orderCode = lateCode, amount = 350000, reference = "late-" + expiredCheckout, code = "00" });
        (await client.PostAsJsonAsync("/api/payments/webhook", new { data = lateData, signature = PayOsChecksum.Sign(lateData, "test-only-checksum-key") })).EnsureSuccessStatusCode();
        events.Parameters["id"].Value = expiredCheckout.ToString();
        Assert.Null(await events.ExecuteScalarAsync());
        events.Parameters["name"].Value = PaymentEvents.Succeeded;
        Assert.NotNull(await events.ExecuteScalarAsync());
        var expiredOrder = (await client.GetFromJsonAsync<CheckoutResponse>($"/api/kiosks/{kiosk}/checkouts/{expiredCheckout}"))!.Orders[0].Id;
        Assert.Single(await OrderEvents(source, RefundEvents.Approved, expiredOrder));
        await using var pendingReturn = source.CreateCommand("UPDATE kiosk_ops.slots SET status='PENDING_REMOVAL' WHERE id=@id");
        pendingReturn.Parameters.AddWithValue("id", slot);
        await pendingReturn.ExecuteNonQueryAsync();
        using var returningSeller = app.CreateClient();
        returningSeller.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = user, Role = "SELLER", SellerId = seller, FullName = "Test shop" }, "ACTIVE"));
        var returnReference = EvidenceFixture.Reference(app.Services, "return_to_seller", PrivateEvidence.SellerBinding(user.ToString(), seller, slot));
        var returnResponse = await returningSeller.PostAsJsonAsync($"/api/sellers/{seller}/flows/return_to_seller", new { p_slot = slot, p_reason = "Nhận lại hoa hết hạn", p_photo_url = returnReference, p_damaged = false });
        Assert.True(returnResponse.IsSuccessStatusCode, await returnResponse.Content.ReadAsStringAsync());
        await using var returned = source.CreateCommand("SELECT count(*) FROM notify.attachments WHERE file_url=@url AND phase='RETURN' AND sha256='fixture' AND size_bytes=1");
        returned.Parameters.AddWithValue("url", scope.ServiceProvider.GetRequiredService<PrivateEvidence>().Resolve(returnReference, "return_to_seller", PrivateEvidence.SellerBinding(user.ToString(), seller, slot)).Url);
        Assert.Equal(1L, await returned.ExecuteScalarAsync());
        // Keep append-only ledger/audit fixture evidence in the isolated test database.
    }

    private static async Task<List<JsonElement>> OrderEvents(NpgsqlDataSource source, string name, Guid order)
    {
        await using var command = source.CreateCommand("""
            SELECT "Content"::jsonb->'Value' FROM cap.published
            WHERE "Name"=@name AND "Content"::jsonb->'Value'->>'order_id'=@id
            """);
        command.Parameters.AddWithValue("name", name); command.Parameters.AddWithValue("id", order.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        var events = new List<JsonElement>();
        while (await reader.ReadAsync()) events.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        return events;
    }

    private sealed class Gateway : HttpMessageHandler
    {
        public long Deadline { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(350000, body.GetProperty("amount").GetInt64());
            Deadline = body.GetProperty("expiredAt").GetInt64();
            Assert.InRange(Deadline, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(8).ToUnixTimeSeconds());
            var data = JsonSerializer.SerializeToElement(new { orderCode = body.GetProperty("orderCode").GetInt64(), amount = 350000, status = "PENDING", checkoutUrl = "https://pay.payos.vn/web/124c33293c934a85be5b7f8761a27a07" });
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { code = "00", data, signature = PayOsChecksum.Sign(data, "test-only-checksum-key") }) };
        }
    }
}
