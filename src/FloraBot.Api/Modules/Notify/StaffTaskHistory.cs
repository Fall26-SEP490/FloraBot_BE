using Npgsql;

namespace FloraBot.Api.Modules.Notify;

public sealed record StaffTaskEvent(string Id, Guid? ActorId, string Action, string? FromStatus, string? ToStatus, string? Report, DateTime CreatedAt);
public static class StaffTaskHistory
{
    public static async Task<List<StaffTaskEvent>> Read(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid taskId, int page, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id::text,actor_id,action,payload->>'from',payload->>'to',payload->>'report',created_at
            FROM notify.audit_logs WHERE entity_type='staff_tasks' AND entity_id=@id
              AND action IN ('STAFF_TASK_ASSIGNED','STAFF_TASK_UPDATED','STAFF_TASK_STOCKED','STAFF_TASK_REASSIGNED','STAFF_TASK_EVIDENCE_ADDED','STAFF_INCIDENT_RESOLVED')
            ORDER BY notify.audit_logs.id DESC LIMIT 26 OFFSET @offset
            """, connection, transaction);
        command.Parameters.AddWithValue("id", taskId); command.Parameters.AddWithValue("offset", (page - 1) * 25);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var events = new List<StaffTaskEvent>();
        while (await reader.ReadAsync(ct)) events.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetDateTime(6)));
        return events;
    }
}
