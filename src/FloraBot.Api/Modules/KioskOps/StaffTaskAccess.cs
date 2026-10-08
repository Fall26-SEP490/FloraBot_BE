using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record StaffTaskScope(string Status, int Version);
public static class StaffTaskAccess
{
    public static async Task<StaffTaskScope?> Lock(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid taskId, Guid actor, bool admin, CancellationToken ct, bool readOnly = false)
    {
        var sql = "SELECT status,version FROM kiosk_ops.staff_tasks WHERE id=@id AND (@admin OR assignee_id=@actor)";
        await using var command = new NpgsqlCommand(sql + (readOnly ? " FOR SHARE" : " FOR UPDATE"), connection, transaction);
        command.Parameters.AddWithValue("id", taskId); command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("admin", admin);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetInt32(1)) : null;
    }
}
