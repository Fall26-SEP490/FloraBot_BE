using DotNetCore.CAP;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.Ordering;
using FloraBot.Api.Modules.Payment;
using Npgsql;

namespace FloraBot.Api.Infrastructure;

public static class DomainChanges
{
    public static async Task PublishAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ICapPublisher publisher, long watermark, CancellationToken ct)
    {
        foreach (var change in await TransactionChanges.ReadAsync(connection, transaction, watermark, ct))
        {
            if (change.Action == "SELLER_APPROVED")
                await SellerEmailEvents.PublishApprovalAsync(publisher, change.Id, ct);
            else if (change.Action == "SELLER_PAST_DUE")
                await SubscriptionEvents.PublishPastDueAsync(connection, transaction, publisher, change.Id, ct);
            else if (change.Action == "DISPENSE_FAILED")
            {
                await OrderEvents.PublishFailuresAsync(connection, transaction, publisher, change.Id, ct);
                await RefundEvents.PublishForOrderAsync(connection, transaction, publisher, change.Id, ct);
            }
        }
    }
}
