using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Notify;

public static class ReconciliationAudit
{
    public static async Task<bool> HasDailyJobAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM notify.audit_logs WHERE action='DAILY_RECONCILIATION' AND payload->>'date'=(CURRENT_DATE-1)::text)", connection, transaction);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    public static async Task RecordDailyJobAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string payload, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT flow.audit(NULL,'DAILY_RECONCILIATION','payments',NULL,@payload)", connection, transaction);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task RecordDailyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid actor,
        JsonElement date, JsonElement? result, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT flow.audit(@actor,'RECONCILED_DAILY','payments',NULL,@payload)", connection, transaction);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new { date, report = result }));
        await command.ExecuteNonQueryAsync(ct);
    }
}
