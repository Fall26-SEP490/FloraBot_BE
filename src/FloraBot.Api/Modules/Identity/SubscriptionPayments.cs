using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public static class SubscriptionPayments
{
    public static async Task<Guid?> PendingPaymentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid seller, Guid subscription, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT payment_id FROM identity.subscriptions WHERE id=@id AND seller_id=@seller AND status='PENDING_PAYMENT'", connection, transaction);
        command.Parameters.AddWithValue("id", subscription);
        command.Parameters.AddWithValue("seller", seller);
        return await command.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }
}
