using System.Data;
using FloraBot.Api.Modules.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class CheckoutAccessTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task EveryOrderMustBelongToKioskAndCustomerAndEarliestDeadlineWins()
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var checkout = Guid.NewGuid(); var kiosk = Guid.NewGuid(); var customer = Guid.NewGuid();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await using var setup = new NpgsqlCommand("""
            INSERT INTO ordering.orders(id,order_code,checkout_id,customer_id,seller_id,kiosk_id,subtotal,total_amount,tracking_token,created_at)
            SELECT id,id::text,@checkout,@customer,id,@kiosk,25000,25000,upper(substr(md5(id::text),1,8)),CURRENT_TIMESTAMP - age
            FROM (VALUES (@first,interval '2 minutes'),(@second,interval '1 minute')) fixture(id,age);
            SELECT min(created_at + flow.cfg_min('hold_minutes')) FROM ordering.orders WHERE checkout_id=@checkout;
            """, connection, transaction);
        foreach (var (key, value) in new[] { ("checkout", checkout), ("kiosk", kiosk), ("customer", customer), ("first", first), ("second", second) })
            setup.Parameters.AddWithValue(key, value);
        var deadline = (DateTime)(await setup.ExecuteScalarAsync())!;
        var snapshot = await CheckoutAccess.ReadAsync(connection, transaction, checkout, kiosk, customer, default);
        Assert.NotNull(snapshot); Assert.Equal(2, snapshot.Orders.Count); Assert.True(snapshot.AwaitingPayment);
        Assert.Equal(deadline, snapshot.PayBefore); Assert.Equal(50000, snapshot.Orders.Sum(x => x.Amount));
        Assert.Null(await CheckoutAccess.ReadAsync(connection, transaction, Guid.NewGuid(), kiosk, customer, default));
        await using var change = new NpgsqlCommand("UPDATE ordering.orders SET customer_id=NULL WHERE id=@id", connection, transaction);
        change.Parameters.AddWithValue("id", second);
        await change.ExecuteNonQueryAsync();
        Assert.Null(await CheckoutAccess.ReadAsync(connection, transaction, checkout, kiosk, customer, default));
        Assert.NotNull(await CheckoutAccess.ReadAsync(connection, transaction, checkout, kiosk, null, default));
        change.CommandText = "UPDATE ordering.orders SET kiosk_id=@other WHERE id=@id";
        change.Parameters.AddWithValue("other", Guid.NewGuid());
        await change.ExecuteNonQueryAsync();
        Assert.Null(await CheckoutAccess.ReadAsync(connection, transaction, checkout, kiosk, null, default));
        change.CommandText = "UPDATE ordering.orders SET kiosk_id=@kiosk,status='CANCELLED' WHERE id=@id";
        change.Parameters.AddWithValue("kiosk", kiosk);
        await change.ExecuteNonQueryAsync();
        Assert.False((await CheckoutAccess.ReadAsync(connection, transaction, checkout, kiosk, null, default))!.AwaitingPayment);
        await transaction.RollbackAsync();
    }
}
