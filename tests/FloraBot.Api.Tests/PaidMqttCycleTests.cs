using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Jobs;
using FloraBot.Api.Modules.Payment;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class PaidMqttTheoryAttribute : TheoryAttribute
{
    public PaidMqttTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLORABOT_MQTT_E2E") != "1")
            Skip = "Run scripts/test-paid-mqtt-cycle.ps1 with the isolated TLS broker and simulator.";
    }
}

public sealed class PaidMqttCycleTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Checksum = "test-only-checksum-key";
    private static readonly Guid Kiosk = Guid.Parse("40000000-0000-0000-0000-000000000001");

    [PaidMqttTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SubscriptionThroughActualMqttPickupAndSettlement(int bouquetCount)
    {
        var database = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DATABASE_URL"));
        Assert.Equal("florabot_mqtt_tests", database.Database);
        using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MQTT_ENABLED", "true");
            builder.UseSetting("MQTT_HOST", "localhost");
            builder.UseSetting("MQTT_PORT", "58884");
            builder.UseSetting("MQTT_CLIENT_ID", "florabot-paid-test-api");
            builder.UseSetting("MQTT_CREDENTIALS_FILE", Environment.GetEnvironmentVariable("TEST_MQTT_CREDENTIALS"));
            builder.UseSetting("MQTT_CA_FILE", Environment.GetEnvironmentVariable("TEST_MQTT_CA"));
            builder.UseSetting("PAYOS_CLIENT_ID", "test-client");
            builder.UseSetting("PAYOS_API_KEY", "test-api");
            builder.UseSetting("PAYOS_RETURN_URL", "https://example.invalid/return");
            builder.UseSetting("PAYOS_CANCEL_URL", "https://example.invalid/cancel");
            builder.ConfigureServices(services => services.AddHttpClient<PayOsClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new Gateway()));
        });
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid(); var user = Guid.NewGuid(); var product = Guid.NewGuid();
        await using var fixture = source.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status)
            VALUES(@seller,'Paid MQTT test','0901112233','PENDING');
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@user,@user::text || '@example.invalid','!unprovisioned','MQTT test','SELLER',@seller);
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status)
            VALUES(@product,@seller,'MQTT test bouquet',350000,48,'ACTIVE');
            """);
        fixture.Parameters.AddWithValue("seller", seller); fixture.Parameters.AddWithValue("user", user);
        fixture.Parameters.AddWithValue("product", product);
        await fixture.ExecuteNonQueryAsync();
        var slots = new List<Guid>();
        for (var index = 0; index < bouquetCount; index++)
        {
            await using var slotCommand = source.CreateCommand("""
            INSERT INTO kiosk_ops.slots(kiosk_id,slot_code,relay_channel)
            SELECT @kiosk,@code,min(channel) FROM generate_series(0,63) channel
            WHERE NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id=@kiosk AND relay_channel=channel)
              AND NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id=@kiosk AND slot_code=@code)
            HAVING min(channel) IS NOT NULL;
            SELECT id FROM kiosk_ops.slots WHERE kiosk_id=@kiosk AND slot_code=@code
              AND status='FREE' AND current_seller_id IS NULL AND bouquet_id IS NULL;
            """);
            slotCommand.Parameters.AddWithValue("kiosk", Kiosk);
            slotCommand.Parameters.AddWithValue("code", index == 0 ? "MQTT-PAID-TEST" : $"MQTT-PAID-TEST-{index + 1}");
            slots.Add(Assert.IsType<Guid>(await slotCommand.ExecuteScalarAsync()));
        }
        await Post(admin, "/api/admin/flows/approve_seller", new { p_seller = seller, p_package = "20000000-0000-0000-0000-00000000000a" });
        using var merchant = app.CreateClient();
        merchant.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: user, sellerId: seller));
        var subscription = (await Post(merchant, $"/api/sellers/{seller}/flows/subscribe", new { p_months = 1 })).GetProperty("result").GetGuid();
        var subLink = await Link(merchant, $"/api/sellers/{seller}/subscriptions/{subscription}/payment-link");
        Assert.Equal(400000, subLink.Amount);
        await Paid(merchant, subLink);
        await using var batchCommand = source.CreateCommand("SELECT flow.new_batch()");
        var batch = (Guid)(await batchCommand.ExecuteScalarAsync())!;
        var bouquets = new List<Guid>();
        foreach (var slot in slots)
        {
            await Post(admin, "/api/admin/flows/assign_slot", new { p_slot = slot, p_seller = seller });
            bouquets.Add((await Post(merchant, $"/api/sellers/{seller}/flows/stock_bouquet", new
            {
                p_batch = batch,
                p_product = product,
                p_slot = slot,
                p_qr = $"mqtt-{product}-{slot}"
            })).GetProperty("result").GetGuid());
        }

        // Wait for an authenticated heartbeat received by this worker, not seeded ONLINE status.
        await Eventually(async () =>
        {
            await using var heartbeat = source.CreateCommand("SELECT EXISTS(SELECT 1 FROM kiosk_ops.mqtt_inbox WHERE kiosk_id=@kiosk AND event='HEARTBEAT' AND disposition='APPLIED' AND received_at>CURRENT_TIMESTAMP-interval '10 seconds')");
            heartbeat.Parameters.AddWithValue("kiosk", Kiosk);
            return await heartbeat.ExecuteScalarAsync() is true;
        }, TimeSpan.FromSeconds(45));
        using var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Kiosk-Key", "demo-key-q1");
        var checkout = (await Post(device, $"/api/kiosks/{Kiosk}/flows/kiosk_checkout", new { p_bouquets = bouquets })).GetProperty("result").GetGuid();
        var checkoutPath = $"/api/kiosks/{Kiosk}/checkouts/{checkout}";
        var purchaseLink = await Link(device, checkoutPath + "/payment-link");
        Assert.Equal(350000L * bouquetCount, purchaseLink.Amount);
        await Paid(device, purchaseLink);
        await Eventually(async () => (await device.GetFromJsonAsync<CheckoutResponse>(checkoutPath))!.Orders.Single().Status == "COMPLETED", TimeSpan.FromSeconds(40));
        var completed = (await device.GetFromJsonAsync<CheckoutResponse>(checkoutPath))!;
        var order = completed.Orders.Single().Id;
        await using var evidence = source.CreateCommand("""
            SELECT t.status,t.attempts,d.pubacked_at IS NOT NULL,
              (SELECT count(*) FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id=t.cmd_id AND i.disposition='APPLIED'),
              (SELECT count(*) FROM cap.published p WHERE p."Name"=@event AND p."Content"::jsonb->'Value'->>'order_id'=@order::text
                AND p."Content"::jsonb->'Value'->>'slot_id'=t.slot_id::text), t.slot_id
            FROM kiosk_ops.unlock_tokens t JOIN kiosk_ops.mqtt_dispatches d USING(cmd_id) WHERE t.order_id=@order
            """);
        evidence.Parameters.AddWithValue("order", order);
        evidence.Parameters.AddWithValue("event", FloraBot.Api.Modules.KioskOps.DeviceEvents.PickedUp);
        await using (var reader = await evidence.ExecuteReaderAsync())
        {
            var receivedSlots = new HashSet<Guid>();
            while (await reader.ReadAsync())
            {
                Assert.Equal("CLOSED", reader.GetString(0));
                Assert.Equal(1, reader.GetInt32(1)); Assert.True(reader.GetBoolean(2));
                Assert.Equal(3, reader.GetInt64(3)); Assert.Equal(1, reader.GetInt64(4));
                Assert.True(receivedSlots.Add(reader.GetGuid(5)));
            }
            Assert.True(receivedSlots.SetEquals(slots));
        }
        var jobs = scope.ServiceProvider.GetRequiredService<JobRunner>();
        await jobs.RunAsync("settle_due_orders", CancellationToken.None);
        Assert.Equal(0L, await Available(source, seller));
        // Age only this isolated fixture; application endpoints still use the real server clock.
        await using var age = source.CreateCommand("UPDATE ordering.orders SET completed_at=CURRENT_TIMESTAMP-flow.cfg_hour('dispute_window_hours')-interval '1 minute' WHERE id=@order");
        age.Parameters.AddWithValue("order", order); await age.ExecuteNonQueryAsync();
        Assert.True((await jobs.RunAsync("settle_due_orders", CancellationToken.None)).Affected >= 1);
        Assert.Equal(350000L * bouquetCount, await Available(source, seller));
        await jobs.RunAsync("settle_due_orders", CancellationToken.None);
        Assert.Equal(350000L * bouquetCount, await Available(source, seller));
        await using var ledger = source.CreateCommand("SELECT count(*),sum(amount)::bigint FROM payment.ledger_entries WHERE ref_id=@order AND ref_type='SETTLEMENT'");
        ledger.Parameters.AddWithValue("order", order);
        await using (var reader = await ledger.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync()); Assert.Equal(2, reader.GetInt64(0)); Assert.Equal(0, reader.GetInt64(1));
        }
        foreach (var slot in slots)
            await Post(merchant, $"/api/sellers/{seller}/flows/release_slot", new { p_slot = slot, p_reason = "Completed isolated MQTT test" });
    }

    private static async Task<JsonElement> Post(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<PaymentLinkResponse> Link(HttpClient client, string path)
    {
        using var response = await client.PostAsync(path, null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PaymentLinkResponse>())!;
    }

    private static async Task Paid(HttpClient client, PaymentLinkResponse link)
    {
        var data = JsonSerializer.SerializeToElement(new { orderCode = link.OrderCode, amount = link.Amount, reference = "mqtt-test-" + link.OrderCode, code = "00" });
        var envelope = new { data, signature = PayOsChecksum.Sign(data, Checksum), code = "00", desc = "success", success = true };
        foreach (var response in await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostAsJsonAsync("/api/payments/webhook", envelope))))
        {
            using (response) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static async Task<long> Available(NpgsqlDataSource source, Guid seller)
    {
        await using var query = source.CreateCommand("SELECT COALESCE(-sum(amount),0)::bigint FROM payment.ledger_entries WHERE seller_id=@seller AND account='SELLER_AVAILABLE'");
        query.Parameters.AddWithValue("seller", seller);
        return (long)(await query.ExecuteScalarAsync())!;
    }

    private static async Task Eventually(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }
        Assert.Fail("Timed out waiting for authenticated MQTT processing.");
    }

    private sealed class Gateway : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api-merchant.payos.vn", request.RequestUri!.Host);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var data = JsonSerializer.SerializeToElement(new
            {
                orderCode = body.GetProperty("orderCode").GetInt64(),
                amount = body.GetProperty("amount").GetInt64(),
                status = "PENDING",
                checkoutUrl = "https://pay.payos.vn/web/124c33293c934a85be5b7f8761a27a07"
            });
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { code = "00", data, signature = PayOsChecksum.Sign(data, Checksum) }) };
        }
    }
}
