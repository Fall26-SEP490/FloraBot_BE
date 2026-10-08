using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Notify;

public static class EmailReminderAudit
{
    // Called under the reminder job's database transaction/advisory lock.
    public static async Task<bool> MarkQueuedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid sellerId, DateOnly expiresOn, CancellationToken ct)
    {
        await using var check = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM notify.audit_logs
              WHERE action='SUBSCRIPTION_REMINDER_QUEUED' AND entity_id=@seller AND payload->>'expires_on'=@expiry)
            """, connection, transaction);
        check.Parameters.AddWithValue("seller", sellerId);
        check.Parameters.AddWithValue("expiry", expiresOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        if (await check.ExecuteScalarAsync(ct) is true) return false;
        await using var audit = new NpgsqlCommand("SELECT flow.audit(NULL,'SUBSCRIPTION_REMINDER_QUEUED','sellers',@seller,@payload)", connection, transaction);
        audit.Parameters.AddWithValue("seller", sellerId);
        audit.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new { expires_on = expiresOn }));
        await audit.ExecuteNonQueryAsync(ct);
        return true;
    }
}
