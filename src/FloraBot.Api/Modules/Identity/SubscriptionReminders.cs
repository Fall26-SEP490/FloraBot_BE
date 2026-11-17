using DotNetCore.CAP;
using FloraBot.Api.Modules.Notify;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public static class SubscriptionReminders
{
    internal const string EligibleLeases = """
        SELECT sub.id,sub.seller_id,k.name,k.code,lease.expires_at
        FROM identity.subscriptions sub
        JOIN identity.sellers s ON s.id=sub.seller_id AND s.status='ACTIVE'
        JOIN kiosk_ops.kiosks k ON k.id=sub.kiosk_id AND k.seller_id=sub.seller_id AND k.status<>'DISABLED'
        CROSS JOIN LATERAL (
            SELECT coalesce(sub.period_from_ts,sub.period_from::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh') AS starts_at,
                   coalesce(sub.period_to_ts,sub.period_to::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh') AS expires_at
        ) lease
        WHERE sub.status='ACTIVE' AND lease.starts_at<=CURRENT_TIMESTAMP
          AND lease.expires_at>CURRENT_TIMESTAMP
          AND lease.expires_at<=CURRENT_TIMESTAMP+interval '7 days'
          AND NOT EXISTS (
              SELECT 1 FROM identity.subscriptions renewal
              WHERE renewal.kiosk_id=sub.kiosk_id AND renewal.seller_id=sub.seller_id
                AND renewal.id<>sub.id AND renewal.status='ACTIVE'
                AND coalesce(renewal.period_from_ts,renewal.period_from::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh')=lease.expires_at
          )
        """;

    public static async Task<int> EnqueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ICapPublisher publisher, CancellationToken ct)
    {
        await using var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('florabot:subscription-reminders',0))", connection, transaction);
        await guard.ExecuteNonQueryAsync(ct);
        await using var query = new NpgsqlCommand(EligibleLeases + " ORDER BY sub.id", connection, transaction);
        var candidates = new List<(Guid Id, Guid Seller)>();
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) candidates.Add((reader.GetGuid(0), reader.GetGuid(1)));
        var count = 0;
        foreach (var candidate in candidates)
        {
            if (!await EmailReminderAudit.MarkQueuedAsync(connection, transaction, candidate.Id, ct)) continue;
            await publisher.PublishAsync(SellerEmailEvents.Requested,
                new SellerEmailRequested(candidate.Seller, SellerEmailEvents.Reminder, candidate.Id), cancellationToken: ct);
            count++;
        }
        return count;
    }
}
