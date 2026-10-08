using DotNetCore.CAP;
using FloraBot.Api.Modules.Notify;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public static class SubscriptionReminders
{
    public static async Task<int> EnqueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ICapPublisher publisher, CancellationToken ct)
    {
        await using var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('florabot:subscription-reminders',0))", connection, transaction);
        await guard.ExecuteNonQueryAsync(ct);
        await using var query = new NpgsqlCommand("""
            SELECT id,package_expires_at FROM identity.sellers
            WHERE status='ACTIVE'
              AND package_expires_at>(CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
              AND package_expires_at<=(CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Ho_Chi_Minh')::date+3
            ORDER BY id
            """, connection, transaction);
        var candidates = new List<(Guid Seller, DateOnly Expiry)>();
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) candidates.Add((reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1)));
        var count = 0;
        foreach (var candidate in candidates)
        {
            if (!await EmailReminderAudit.MarkQueuedAsync(connection, transaction, candidate.Seller, candidate.Expiry, ct)) continue;
            await publisher.PublishAsync(SellerEmailEvents.Requested,
                new SellerEmailRequested(candidate.Seller, SellerEmailEvents.Reminder, ExpiresOn: candidate.Expiry), cancellationToken: ct);
            count++;
        }
        return count;
    }
}
