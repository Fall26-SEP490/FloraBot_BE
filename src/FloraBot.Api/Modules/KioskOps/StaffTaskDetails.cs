using System.Data;
using System.Security.Claims;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.Ordering;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record StaffTaskHistoryItem(string Id, string ActorName, string Action, string? FromStatus, string? ToStatus, string? Report, DateTime CreatedAt);
public sealed record StaffTaskDetail(string KioskStatus, DateTime? LastHeartbeatAt, StaffIncidentInfo? Incident, string? SlotCode, string? SlotStatus, List<StaffTaskHistoryItem> History, int Page, bool HasMore, int? Version = null, string? Status = null,
    FlowerSlotPresence? SlotObservation = null, DateTimeOffset? SlotObservedAt = null,
    Guid? KioskId = null, List<FlowerSlotPresence>? KioskSlots = null);
public static class StaffTaskDetails
{
    public static void MapStaffTaskDetails(this WebApplication app)
    {
        app.MapGet("/api/staff/tasks/{taskId:guid}", Read).RequireAuthorization("Staff").Produces<StaffTaskDetail>();
        app.MapGet("/api/admin/staff-tasks/{taskId:guid}", Read).RequireAuthorization("Admin").Produces<StaffTaskDetail>();
        app.MapGet("/api/operations/staff-tasks/{taskId:guid}", Read).RequireAuthorization("OperationsManager").Produces<StaffTaskDetail>();
        app.MapGet("/api/sellers/{sellerId:guid}/staff-tasks/{taskId:guid}", Read).RequireAuthorization("Merchant", "SameSeller").Produces<StaffTaskDetail>();
    }
    private static async Task<IResult> Read(Guid taskId, int? page, NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var number = page ?? 1;
        if (number is < 1 or > 100000) return Results.BadRequest();
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        if (await StaffTaskAccess.Lock(connection, transaction, taskId, http, ct, readOnly: true) is null)
            return Results.NotFound();
        Guid kiosk; Guid? incidentId; string status, taskStatus; int version; DateTime? heartbeat;
        await using (var command = new NpgsqlCommand("""
            SELECT t.kiosk_id,t.incident_id,k.status,k.last_heartbeat_at,t.version,t.status FROM kiosk_ops.staff_tasks t
            JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id
            WHERE t.id=@id FOR SHARE OF t
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", taskId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            kiosk = reader.GetGuid(0); incidentId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            status = reader.GetString(2); heartbeat = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
            version = reader.GetInt32(4); taskStatus = reader.GetString(5);
        }
        var incident = incidentId is Guid id ? await StaffIncidentContext.Read(connection, transaction, id, kiosk, ct) : null;
        string? slotCode = null, slotStatus = null;
        FlowerSlotPresence? slotObservation = null;
        DateTimeOffset? slotObservedAt = null;
        var kioskSlots = new List<FlowerSlotPresence>();
        var wholeKiosk = incident is not null && http.User.IsInRole("TECHNICIAN");
        if (wholeKiosk || incident?.SlotId is not null)
        {
            await using var command = new NpgsqlCommand($$"""
                SELECT s.slot_code,s.status,s.relay_channel,p.observed_at,
                  CASE WHEN @online AND p.received_at>CURRENT_TIMESTAMP-interval '45 seconds'
                    AND p.observed_at>extract(epoch FROM CURRENT_TIMESTAMP)*1000-45000
                    THEN p.states[s.relay_channel+1] END,
                  u.status,u.issued_at,u.door_opened_at,u.door_closed_at,s.id
                FROM kiosk_ops.slots s
                LEFT JOIN kiosk_ops.flower_presence p ON p.kiosk_id=s.kiosk_id
                {{SlotUnlockObservation.LatestJoin}}
                WHERE s.kiosk_id=@kiosk AND (@wholeKiosk OR s.id=@id)
                ORDER BY s.relay_channel,s.slot_code
                """, connection, transaction);
            command.Parameters.Add("id", NpgsqlTypes.NpgsqlDbType.Uuid).Value = (object?)incident?.SlotId ?? DBNull.Value;
            command.Parameters.AddWithValue("kiosk", kiosk);
            command.Parameters.AddWithValue("wholeKiosk", wholeKiosk);
            command.Parameters.AddWithValue("online", status == "ONLINE");
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                slotObservedAt = reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));
                var presence = reader.IsDBNull(4) ? -1 : reader.GetInt32(4);
                var observation = new FlowerSlotPresence(reader.GetGuid(9), reader.GetString(0), Convert.ToInt32(reader.GetValue(2)),
                    presence == -1 ? null : presence == 1, SlotUnlockObservation.Read(reader, 5));
                if (wholeKiosk) kioskSlots.Add(observation);
                if (incident?.SlotId == observation.SlotId)
                {
                    slotCode = observation.SlotCode;
                    slotStatus = reader.GetString(1);
                    slotObservation = observation;
                }
            }
        }
        var events = await StaffTaskHistory.Read(connection, transaction, taskId, number, ct);
        var users = await StaffIdentityRead.Users(data, events.Where(x => x.ActorId.HasValue).Select(x => x.ActorId!.Value).Distinct().ToArray(), ct);
        var history = events.Take(25).Select(x => new StaffTaskHistoryItem(x.Id, x.ActorId is Guid actor ? users.GetValueOrDefault(actor, "Tài khoản không còn hiển thị") : "Hệ thống", x.Action, x.FromStatus, x.ToStatus, x.Report, x.CreatedAt)).ToList();
        await transaction.CommitAsync(ct);
        return Results.Ok(new StaffTaskDetail(status, heartbeat, incident, slotCode, slotStatus, history, number,
            events.Count > 25, version, taskStatus, slotObservation, slotObservedAt, kiosk,
            wholeKiosk ? kioskSlots : null));
    }
}
