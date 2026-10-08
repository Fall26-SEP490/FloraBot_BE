using Npgsql;

namespace FloraBot.Api.Modules.Notify;

public sealed record ChangedEntity(string Action, Guid Id);

public static class TransactionChanges
{
    public static async Task<long> WatermarkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT coalesce(max(id),0) FROM notify.audit_logs", connection, transaction);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public static async Task<List<ChangedEntity>> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long watermark, CancellationToken ct)
    {
        // The primary-key range is bounded; xmin excludes concurrent transactions within that range.
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT action,entity_id FROM notify.audit_logs
            WHERE id>@watermark AND xmin=pg_current_xact_id()::xid
              AND action IN ('SELLER_PAST_DUE','SELLER_APPROVED','DISPENSE_FAILED') AND entity_id IS NOT NULL
            """, connection, transaction);
        command.Parameters.AddWithValue("watermark", watermark);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var changes = new List<ChangedEntity>();
        while (await reader.ReadAsync(ct)) changes.Add(new(reader.GetString(0), reader.GetGuid(1)));
        return changes;
    }
}
