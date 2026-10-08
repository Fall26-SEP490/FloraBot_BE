using System.Text.Json.Serialization;
using DotNetCore.CAP;
using FloraBot.Api.Realtime;
using Npgsql;

namespace FloraBot.Api.Modules.Payment;

public sealed record RefundApproved(
    [property: JsonPropertyName("refund_payment_id")] Guid RefundPaymentId,
    [property: JsonPropertyName("order_id")] Guid? OrderId,
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("reason")] string Reason);

public sealed class RefundEventSubscriber(PortalNotifier notifier) : ICapSubscribe
{
    [CapSubscribe(RefundEvents.Approved)]
    public Task HandleAsync(RefundApproved message) => notifier.RefreshAsync(null);
}

public static class RefundEvents
{
    public const string Approved = "florabot.refund.approved.v1";
    public static Task PublishForOrderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ICapPublisher publisher, Guid id, CancellationToken ct)
        => PublishAsync(connection, transaction, publisher, "order_id", id, ct);
    public static Task PublishForChargeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ICapPublisher publisher, Guid id, CancellationToken ct)
        => PublishAsync(connection, transaction, publisher, "parent_payment_id", id, ct);
    public static Task PublishByIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ICapPublisher publisher, Guid id, CancellationToken ct)
        => PublishAsync(connection, transaction, publisher, "id", id, ct);

    private static async Task PublishAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ICapPublisher publisher, string column, Guid id, CancellationToken ct)
    {
        // Only internal column literals are allowed. xmin excludes prior refunds and concurrent transactions.
        await using var command = new NpgsqlCommand($"""
            SELECT id,order_id,amount,reason FROM payment.payments WHERE {column}=@id
              AND kind='REFUND' AND status='PENDING' AND approved_by IS NOT NULL
              AND xmin=pg_current_xact_id()::xid ORDER BY id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        var messages = new List<RefundApproved>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) messages.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3)));
        }
        foreach (var message in messages) await publisher.PublishAsync(Approved, message, cancellationToken: ct);
    }
}
