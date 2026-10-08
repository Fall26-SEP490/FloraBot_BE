using Npgsql;

namespace FloraBot.Api.Modules.Payment;

public sealed record ReceiptRefundSummary(long PendingAmount, long PaidAmount);

public static class ReceiptRefunds
{
    public static async Task<ReceiptRefundSummary> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid orderId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT coalesce(sum(amount) FILTER(WHERE status='PENDING'),0)::bigint,
                   coalesce(sum(amount) FILTER(WHERE status='SUCCEEDED'),0)::bigint
            FROM payment.payments WHERE order_id=@id AND kind='REFUND'
            """, connection, transaction);
        command.Parameters.AddWithValue("id", orderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(reader.GetInt64(0), reader.GetInt64(1));
    }
}
