using System.Data;
using System.Security.Claims;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.Ordering;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record StaffTaskHistoryItem(string Id, string ActorName, string Action, string? FromStatus, string? ToStatus, string? Report, DateTime CreatedAt);
public sealed record StaffTaskDetail(string KioskStatus, DateTime? LastHeartbeatAt, StaffIncidentInfo? Incident, string? SlotCode, string? SlotStatus, List<StaffTaskHistoryItem> History, int Page, bool HasMore);
public static class StaffTaskDetails
{
    public static void MapStaffTaskDetails(this WebApplication app)
    {
        app.MapGet("/api/staff/tasks/{taskId:guid}", Read).RequireAuthorization("Staff").Produces<StaffTaskDetail>();
        app.MapGet("/api/admin/staff-tasks/{taskId:guid}", Read).RequireAuthorization("Admin").Produces<StaffTaskDetail>();
    }
    private static async Task<IResult> Read(Guid taskId, int? page, NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var number = page ?? 1;
        if (number is < 1 or > 100000) return Results.BadRequest();
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        Guid kiosk; Guid? incidentId; string status; DateTime? heartbeat;
        await using (var command = new NpgsqlCommand("""
            SELECT t.kiosk_id,t.incident_id,k.status,k.last_heartbeat_at FROM kiosk_ops.staff_tasks t
            JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id
            WHERE t.id=@id AND (@admin OR t.assignee_id=@actor) FOR SHARE OF t
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", taskId); command.Parameters.AddWithValue("admin", http.User.IsInRole("ADMIN"));
            command.Parameters.AddWithValue("actor", Guid.Parse(http.User.FindFirstValue("sub")!));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            kiosk = reader.GetGuid(0); incidentId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            status = reader.GetString(2); heartbeat = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
        }
        var incident = incidentId is Guid id ? await StaffIncidentContext.Read(connection, transaction, id, kiosk, ct) : null;
        string? slotCode = null, slotStatus = null;
        if (incident?.SlotId is Guid slot)
        {
            await using var command = new NpgsqlCommand("SELECT slot_code,status FROM kiosk_ops.slots WHERE id=@id AND kiosk_id=@kiosk", connection, transaction);
            command.Parameters.AddWithValue("id", slot); command.Parameters.AddWithValue("kiosk", kiosk);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) { slotCode = reader.GetString(0); slotStatus = reader.GetString(1); }
        }
        var events = await StaffTaskHistory.Read(connection, transaction, taskId, number, ct);
        var users = await StaffIdentityRead.Users(data, events.Where(x => x.ActorId.HasValue).Select(x => x.ActorId!.Value).Distinct().ToArray(), ct);
        var history = events.Take(25).Select(x => new StaffTaskHistoryItem(x.Id, x.ActorId is Guid actor ? users.GetValueOrDefault(actor, "Tài khoản không còn hiển thị") : "Hệ thống", x.Action, x.FromStatus, x.ToStatus, x.Report, x.CreatedAt)).ToList();
        await transaction.CommitAsync(ct);
        return Results.Ok(new StaffTaskDetail(status, heartbeat, incident, slotCode, slotStatus, history, number, events.Count > 25));
    }
}
