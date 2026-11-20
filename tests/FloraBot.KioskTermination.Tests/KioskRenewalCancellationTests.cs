using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace FloraBot.KioskTermination.Tests;

public sealed partial class KioskTerminationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenewalCancellation_PreservesCurrentLeaseAndHandlesLatePayment(bool latePayment)
    {
        var f = await SeedDatabaseFixtureAsync();
        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await data.OpenConnectionAsync();
        var renewal = Guid.CreateVersion7();
        var payment = Guid.CreateVersion7();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO identity.subscriptions(id,seller_id,package_id,kiosk_id,period_from,period_to,price,status)
            SELECT @renewal,seller_id,package_id,kiosk_id,period_to,period_to+365,price,'PENDING_PAYMENT'
            FROM identity.subscriptions WHERE id=@current;
            INSERT INTO payment.payments(id,kind,purpose,subscription_id,gateway,idempotency_key,amount,status)
            VALUES(@payment,'CHARGE','SUBSCRIPTION',@renewal,'PAYOS',@key,400000,'PENDING');
            UPDATE identity.subscriptions SET payment_id=@payment WHERE id=@renewal;
            """, conn))
        {
            seed.Parameters.AddWithValue("current", f.SubscriptionId);
            seed.Parameters.AddWithValue("renewal", renewal);
            seed.Parameters.AddWithValue("payment", payment);
            seed.Parameters.AddWithValue("key", "cancel-test-" + renewal);
            await seed.ExecuteNonQueryAsync();
        }
        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);
        var path = $"/api/sellers/{f.SellerId}/subscriptions/{renewal}/cancel";
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync(path, null)).StatusCode);
        if (latePayment)
        {
            await using var paid = new NpgsqlCommand("SELECT flow.subscription_paid(@sub,@txn)", conn);
            paid.Parameters.AddWithValue("sub", renewal);
            paid.Parameters.AddWithValue("txn", "late-cancel-test-" + renewal);
            await paid.ExecuteNonQueryAsync();
            await paid.ExecuteNonQueryAsync();
        }
        await using var check = new NpgsqlCommand("""
            SELECT (SELECT status FROM identity.subscriptions WHERE id=@current),
                (SELECT status FROM identity.subscriptions WHERE id=@renewal),
                (SELECT status FROM payment.payments WHERE id=@payment),
                (SELECT count(*) FROM payment.payments WHERE parent_payment_id=@payment AND kind='REFUND')
            """, conn);
        check.Parameters.AddWithValue("current", f.SubscriptionId);
        check.Parameters.AddWithValue("renewal", renewal);
        check.Parameters.AddWithValue("payment", payment);
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("ACTIVE", reader.GetString(0));
        Assert.Equal("CANCELLED", reader.GetString(1));
        Assert.Equal(latePayment ? "SUCCEEDED" : "CANCELLED", reader.GetString(2));
        Assert.Equal(latePayment ? 1L : 0L, reader.GetInt64(3));
    }

    [Fact]
    public async Task RenewalCancellation_RejectsPaidAndForeignSubscriptions()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync(
            $"/api/sellers/{f.SellerId}/subscriptions/{f.SubscriptionId}/cancel", null)).StatusCode);
        var foreign = await SeedDatabaseFixtureAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync(
            $"/api/sellers/{f.SellerId}/subscriptions/{foreign.SubscriptionId}/cancel", null)).StatusCode);
    }
}
