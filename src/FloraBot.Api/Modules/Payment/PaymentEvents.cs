using System.Text.Json.Serialization;
using DotNetCore.CAP;
using Npgsql;

namespace FloraBot.Api.Modules.Payment;

public sealed class PaymentEventSubscriber(FloraBot.Api.Realtime.PortalNotifier notifier) : ICapSubscribe
{
    [CapSubscribe(PaymentEvents.Succeeded)]
    public Task HandleAsync(PaymentSucceeded message) => notifier.RefreshAsync(null);
}

public sealed record PaymentSucceeded(
    [property: JsonPropertyName("payment_id")] Guid PaymentId,
    [property: JsonPropertyName("checkout_id")] Guid? CheckoutId,
    [property: JsonPropertyName("subscription_id")] Guid? SubscriptionId,
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("gateway_txn_id")] string GatewayTransactionId,
    [property: JsonPropertyName("paid_at")] DateTime PaidAt);

public static class PaymentEvents
{
    public const string Succeeded = "florabot.payment.succeeded.v1";

    public static async Task PublishSucceededAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ICapPublisher publisher, Guid paymentId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT checkout_id,subscription_id,amount,gateway_txn_id,paid_at
            FROM payment.payments WHERE id=@id AND status='SUCCEEDED'
            """, connection, transaction);
        command.Parameters.AddWithValue("id", paymentId);
        PaymentSucceeded? message = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                message = new(paymentId, reader.IsDBNull(0) ? null : reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3), reader.GetDateTime(4));
        }
        if (message is null) return;
        await publisher.PublishAsync(Succeeded, message, cancellationToken: ct);
        if (message.SubscriptionId is { } subscription)
            await Identity.SubscriptionEvents.PublishPaidAsync(connection, transaction, publisher, subscription, ct);
        if (message.CheckoutId is { } checkout)
            await Ordering.OrderEvents.PublishPaidAsync(connection, transaction, publisher, checkout, ct);
        await RefundEvents.PublishForChargeAsync(connection, transaction, publisher, paymentId, ct);
    }
}
