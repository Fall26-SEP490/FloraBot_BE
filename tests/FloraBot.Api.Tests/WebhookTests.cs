using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Payment;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class WebhookTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedSubscriptionWebhookSurvivesPayloadReplacementAndConcurrentRetries(bool late)
    {
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid(); var user = Guid.NewGuid();
        await using var setup = source.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id)
            VALUES(@seller,'Webhook test','0901112233','APPROVED','20000000-0000-0000-0000-00000000000a');
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@user,@user::text || '@example.invalid','!unprovisioned','Webhook test','SELLER',@seller);
            """);
        setup.Parameters.AddWithValue("seller", seller); setup.Parameters.AddWithValue("user", user);
        await setup.ExecuteNonQueryAsync();
        await using var subscribe = source.CreateCommand("SELECT flow.subscribe(@seller,@user,1)");
        subscribe.Parameters.AddWithValue("seller", seller); subscribe.Parameters.AddWithValue("user", user);
        var subscription = (Guid)(await subscribe.ExecuteScalarAsync())!;
        await using var reserve = source.CreateCommand("SELECT flow.reserve_gateway_order(payment_id) FROM identity.subscriptions WHERE id=@id");
        reserve.Parameters.AddWithValue("id", subscription);
        var orderCode = (long)(await reserve.ExecuteScalarAsync())!;
        Assert.Equal(orderCode, await reserve.ExecuteScalarAsync());
        if (late)
        {
            await using var cancel = source.CreateCommand("UPDATE identity.subscriptions SET status='CANCELLED' WHERE id=@id");
            cancel.Parameters.AddWithValue("id", subscription);
            await cancel.ExecuteNonQueryAsync();
        }
        var reference = "TEST-" + Guid.NewGuid();
        object Envelope(long amount, long gatewayCode)
        {
            var data = JsonSerializer.SerializeToElement(new { orderCode = gatewayCode, amount, reference, code = "00" });
            return new { data, signature = PayOsChecksum.Sign(data, "test-only-checksum-key"), code = "00", desc = "success", success = true };
        }
        var mismatch = await client.PostAsJsonAsync("/api/payments/webhook", Envelope(1, orderCode));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var unknown = await client.PostAsJsonAsync("/api/payments/webhook", Envelope(400000, 0));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        await using var pending = source.CreateCommand("SELECT status FROM identity.subscriptions WHERE id=@id");
        pending.Parameters.AddWithValue("id", subscription);
        Assert.Equal(late ? "CANCELLED" : "PENDING_PAYMENT", await pending.ExecuteScalarAsync());
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.PostAsJsonAsync("/api/payments/webhook", Envelope(400000, orderCode))));
        foreach (var response in results) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var replay = await client.PostAsJsonAsync("/api/payments/webhook", Envelope(400000, orderCode));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(late ? "CANCELLED" : "ACTIVE", await pending.ExecuteScalarAsync());
        await using var check = source.CreateCommand("""
            SELECT p.status,p.gateway_txn_id,p.raw_payload->>'orderCode',g.order_code,
              (SELECT count(*) FROM payment.ledger_entries WHERE ref_id=CASE WHEN @late THEN p.id ELSE @id END),
              (SELECT coalesce(sum(amount),0)::bigint FROM payment.ledger_entries WHERE ref_id=CASE WHEN @late THEN p.id ELSE @id END),
              (SELECT count(*) FROM notify.audit_logs WHERE entity_id=@id AND action=@action)
            FROM payment.payments p JOIN payment.gateway_orders g ON g.payment_id=p.id
            WHERE p.subscription_id=@id AND p.kind='CHARGE'
            """);
        check.Parameters.AddWithValue("id", subscription);
        check.Parameters.AddWithValue("late", late);
        check.Parameters.AddWithValue("action", late ? "LATE_SUBSCRIPTION_PAYMENT" : "SUBSCRIPTION_PAID");
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("SUCCEEDED", reader.GetString(0));
        Assert.Equal(reference, reader.GetString(1));
        Assert.Equal(reference, reader.GetString(2));
        Assert.Equal(orderCode, reader.GetInt64(3));
        Assert.Equal(2, reader.GetInt64(4));
        Assert.Equal(0, reader.GetInt64(5));
        Assert.Equal(1, reader.GetInt64(6));
        await reader.DisposeAsync();
        await using var events = source.CreateCommand("""
            SELECT "Content"::jsonb->'Value' FROM cap.published
            WHERE "Name"=@name AND "Content"::jsonb->'Value'->>'subscription_id'=@id
            """);
        events.Parameters.AddWithValue("name", PaymentEvents.Succeeded);
        events.Parameters.AddWithValue("id", subscription.ToString());
        await using var eventReader = await events.ExecuteReaderAsync();
        Assert.True(await eventReader.ReadAsync());
        var payload = JsonSerializer.Deserialize<JsonElement>(eventReader.GetString(0));
        Assert.Equal(reference, payload.GetProperty("gateway_txn_id").GetString());
        Assert.Equal(400000, payload.GetProperty("amount").GetInt64());
        Assert.False(await eventReader.ReadAsync());
        await eventReader.DisposeAsync();
        events.Parameters["name"].Value = FloraBot.Api.Modules.Identity.SubscriptionEvents.Paid;
        await using var subscriptionEvents = await events.ExecuteReaderAsync();
        Assert.Equal(!late, await subscriptionEvents.ReadAsync());
        if (!late)
        {
            var activation = JsonSerializer.Deserialize<JsonElement>(subscriptionEvents.GetString(0));
            Assert.Equal(seller, activation.GetProperty("seller_id").GetGuid());
            Assert.True(DateOnly.Parse(activation.GetProperty("period").GetProperty("to").GetString()!) >
                DateOnly.Parse(activation.GetProperty("period").GetProperty("from").GetString()!));
            Assert.False(await subscriptionEvents.ReadAsync());
        }
        await subscriptionEvents.DisposeAsync();
        events.Parameters["name"].Value = FloraBot.Api.Modules.Identity.SellerEmailEvents.Requested;
        await using var emails = await events.ExecuteReaderAsync();
        Assert.Equal(!late, await emails.ReadAsync());
        if (!late)
        {
            var email = JsonSerializer.Deserialize<JsonElement>(emails.GetString(0));
            Assert.Equal(seller, email.GetProperty("seller_id").GetGuid());
            Assert.Equal("subscription-receipt", email.GetProperty("kind").GetString());
            Assert.DoesNotContain("@", email.GetRawText());
            Assert.False(await emails.ReadAsync());
        }
        // Keep the fixture in the isolated test DB: ledger and audit are append-only.
    }

    [Fact]
    public async Task SignedMalformedNumbersReturn400()
    {
        using var client = factory.CreateClient();
        foreach (var json in new[] {
            "{\"orderCode\":\"123\",\"amount\":400000,\"reference\":\"test\",\"code\":\"00\"}",
            "{\"orderCode\":123,\"amount\":null,\"reference\":\"test\",\"code\":\"00\"}" })
        {
            var data = JsonSerializer.Deserialize<JsonElement>(json);
            var response = await client.PostAsJsonAsync("/api/payments/webhook",
                new { data, signature = PayOsChecksum.Sign(data, "test-only-checksum-key") });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task InvalidChecksumReturns400AndAuditsWithoutPayload()
    {
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var before = data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE action='WEBHOOK_INVALID_CHECKSUM'");
        var count = (long)(await before.ExecuteScalarAsync())!;
        var response = await client.PostAsJsonAsync("/api/payments/webhook", new { data = new { orderCode = 12345, amount = 400000, reference = "fake" }, signature = new string('0', 64) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var after = data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE action='WEBHOOK_INVALID_CHECKSUM' AND payload IS NULL");
        Assert.True((long)(await after.ExecuteScalarAsync())! > count);
    }
    [Fact]
    public void ChecksumDetectsTamperingAndSortsKeys()
    {
        var original = JsonSerializer.SerializeToElement(new { orderCode = 123, amount = 400000 });
        var reordered = JsonSerializer.SerializeToElement(new { amount = 400000, orderCode = 123 });
        var signature = PayOsChecksum.Sign(original, "test-key");
        Assert.True(PayOsChecksum.Verify(reordered, signature, "test-key"));
        Assert.False(PayOsChecksum.Verify(JsonSerializer.SerializeToElement(new { orderCode = 123, amount = 1 }), signature, "test-key"));
        Assert.False(PayOsChecksum.Verify(original, "not-hex", "test-key"));
    }
}
