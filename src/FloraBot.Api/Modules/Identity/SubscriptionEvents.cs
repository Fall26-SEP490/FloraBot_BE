using System.Text.Json.Serialization;
using DotNetCore.CAP;
using FloraBot.Api.Realtime;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public sealed record SubscriptionPeriod(
    [property: JsonPropertyName("from")] DateOnly From,
    [property: JsonPropertyName("to")] DateOnly To);
public sealed record SubscriptionPaid(
    [property: JsonPropertyName("subscription_id")] Guid SubscriptionId,
    [property: JsonPropertyName("seller_id")] Guid SellerId,
    [property: JsonPropertyName("period")] SubscriptionPeriod Period);
public sealed record SellerPastDue(
    [property: JsonPropertyName("seller_id")] Guid SellerId,
    [property: JsonPropertyName("package_expires_at")] DateOnly? PackageExpiresAt);

public sealed class SubscriptionEventSubscriber(PortalNotifier notifier) : ICapSubscribe
{
    [CapSubscribe(SubscriptionEvents.Paid)]
    public Task HandleAsync(SubscriptionPaid message) => notifier.RefreshAsync(message.SellerId);

    [CapSubscribe(SubscriptionEvents.PastDue)]
    public Task PastDueAsync(SellerPastDue message) => notifier.RefreshAsync(message.SellerId);
}

public static class SubscriptionEvents
{
    public const string Paid = "florabot.subscription.paid.v1";
    public const string PastDue = "florabot.seller.past-due.v1";

    public static async Task PublishPastDueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ICapPublisher publisher, Guid sellerId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT package_expires_at FROM identity.sellers WHERE id=@id AND status='PAST_DUE'", connection, transaction);
        command.Parameters.AddWithValue("id", sellerId);
        SellerPastDue? message = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct)) message = new(sellerId, reader.IsDBNull(0) ? null : reader.GetFieldValue<DateOnly>(0));
        }
        if (message is not null) await publisher.PublishAsync(PastDue, message, cancellationToken: ct);
    }

    public static async Task PublishPaidAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ICapPublisher publisher, Guid subscriptionId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT seller_id,period_from,period_to FROM identity.subscriptions WHERE id=@id AND status='ACTIVE'
            """, connection, transaction);
        command.Parameters.AddWithValue("id", subscriptionId);
        SubscriptionPaid? message = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct)) message = new(subscriptionId, reader.GetGuid(0),
                new(reader.GetFieldValue<DateOnly>(1), reader.GetFieldValue<DateOnly>(2)));
        }
        if (message is not null)
        {
            await publisher.PublishAsync(Paid, message, cancellationToken: ct);
            await SellerEmailEvents.PublishReceiptAsync(publisher, message.SellerId, subscriptionId, ct);
        }
    }
}
