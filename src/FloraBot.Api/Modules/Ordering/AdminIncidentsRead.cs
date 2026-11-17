using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Notify;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Modules.Ordering;

public sealed record IncidentLinkedTask(Guid Id, string Status, Guid AssigneeId, string AssigneeName);
public sealed record AdminIncidentResponse(Guid Id, string Kind, string Status, string Reason, DateTime CreatedAt,
    DateTime? ResolvedAt, string? Decision, Guid? OrderId, Guid KioskId, Guid? SlotId,
    IncidentLocation? Location, IncidentSlot? Slot, IncidentLinkedTask? Task = null)
{
    public string Progress => Status == "RESOLVED_FIXED" ? "COMPLETED" : Task?.Status switch
    {
        "ASSIGNED" or "IN_PROGRESS" => "IN_PROGRESS",
        "SUBMITTED" => "AWAITING_REVIEW",
        "COMPLETED" => "COMPLETED",
        _ => "UNASSIGNED"
    };
}
public sealed record AdminIncidentPage(List<AdminIncidentResponse> Items, int Page, bool HasMore);
public sealed record AdminIncidentDetail(AdminIncidentResponse Incident, AdminIncidentEvidencePage Evidence);

public static class AdminIncidentsRead
{
    public static void MapAdminIncidentsRead(this WebApplication app)
    {
        app.MapAdminIncidentTaskEvidenceRead();

        app.MapGet("/api/admin/incidents", async (string? status, int? page, Guid? kioskId, FloraDbContext db, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            status ??= "OPEN";
            var number = page ?? 1;
            if (status is not ("OPEN" or "RESOLVED_FIXED") || number < 1 || number > 100000) return Results.BadRequest();
            var query = db.Disputes.AsNoTracking().Where(x => (x.Kind == "DEVICE_FAULT" || x.Kind == "DISPENSE_FAILED") && x.Status == status);
            if (kioskId.HasValue) query = query.Where(x => x.KioskId == kioskId);
            query = status == "OPEN" ? query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id) : query.OrderByDescending(x => x.ResolvedAt).ThenByDescending(x => x.Id);
            var rows = await query.Skip((number - 1) * 25).Take(26).ToListAsync(ct);
            var locations = await IncidentLocations.KiosksAsync(db, rows.Select(x => x.KioskId).Distinct().ToArray(), ct);
            var slots = await IncidentLocations.SlotsAsync(db, rows.Where(x => x.SlotId.HasValue).Select(x => x.SlotId!.Value).Distinct().ToArray(), ct);
            var tasks = await ReadTasksAsync(data, rows.Take(25).Select(x => x.Id).ToArray(), ct);
            return Results.Ok(new AdminIncidentPage(rows.Take(25).Select(x => Map(x, locations, slots, tasks)).ToList(), number, rows.Count > 25));
        }).RequireAuthorization("Admin").Produces<AdminIncidentPage>();

        app.MapGet("/api/operations/incidents", async (string? status, int? page, Guid? kioskId, FloraDbContext db, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            status ??= "OPEN";
            var number = page ?? 1;
            if (status is not ("OPEN" or "RESOLVED_FIXED") || number < 1 || number > 100000) return Results.BadRequest();
            var managerId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var allowedKiosks = await StaffTaskAccess.ManagerKiosks(data, managerId, ct);
            if (allowedKiosks.Count == 0) return Results.Ok(new AdminIncidentPage([], number, false));
            if (kioskId.HasValue && !allowedKiosks.Contains(kioskId.Value)) return Results.Ok(new AdminIncidentPage([], number, false));
            var targetKiosks = kioskId.HasValue ? [kioskId.Value] : allowedKiosks.ToArray();
            var query = db.Disputes.AsNoTracking().Where(x => (x.Kind == "DEVICE_FAULT" || x.Kind == "DISPENSE_FAILED") && x.Status == status && targetKiosks.Contains(x.KioskId));
            query = status == "OPEN" ? query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id) : query.OrderByDescending(x => x.ResolvedAt).ThenByDescending(x => x.Id);
            var rows = await query.Skip((number - 1) * 25).Take(26).ToListAsync(ct);
            var locations = await IncidentLocations.KiosksAsync(db, rows.Select(x => x.KioskId).Distinct().ToArray(), ct);
            var slots = await IncidentLocations.SlotsAsync(db, rows.Where(x => x.SlotId.HasValue).Select(x => x.SlotId!.Value).Distinct().ToArray(), ct);
            var tasks = await ReadTasksAsync(data, rows.Take(25).Select(x => x.Id).ToArray(), ct);
            return Results.Ok(new AdminIncidentPage(rows.Take(25).Select(x => Map(x, locations, slots, tasks)).ToList(), number, rows.Count > 25));
        }).RequireAuthorization("OperationsManager").Produces<AdminIncidentPage>();

        app.MapGet("/api/admin/incidents/{incidentId:guid}", async (Guid incidentId, int? evidencePage,
            FloraDbContext db, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var page = evidencePage ?? 1;
            if (page is < 1 or > 100000) return Results.BadRequest();
            var row = await db.Disputes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == incidentId && (x.Kind == "DEVICE_FAULT" || x.Kind == "DISPENSE_FAILED"), ct);
            if (row is null) return Results.NotFound();
            var locations = await IncidentLocations.KiosksAsync(db, [row.KioskId], ct);
            var slots = await IncidentLocations.SlotsAsync(db, row.SlotId.HasValue ? [row.SlotId.Value] : [], ct);
            var tasks = await ReadTasksAsync(data, [row.Id], ct);
            return Results.Ok(new AdminIncidentDetail(Map(row, locations, slots, tasks),
                await IncidentEvidence.ReadForAdminIncidentAsync(data, row.Id, page, ct)));
        }).RequireAuthorization("Admin").Produces<AdminIncidentDetail>().ProducesProblem(400)
            .WithDescription("Returns incident and task evidence together. Use evidencePage (default 1) to load 20 evidence items per page.");
    }

    private static async Task<Dictionary<Guid, IncidentLinkedTask>> ReadTasksAsync(NpgsqlDataSource data, Guid[] incidentIds, CancellationToken ct)
    {
        var rows = new Dictionary<Guid, IncidentLinkedTask>();
        if (incidentIds.Length == 0) return rows;
        await using var command = data.CreateCommand("""
            SELECT DISTINCT ON (t.incident_id) t.incident_id,t.id,t.status,t.assignee_id,u.full_name
            FROM kiosk_ops.staff_tasks t
            JOIN ordering.disputes d ON d.id=t.incident_id AND d.kiosk_id=t.kiosk_id
            JOIN identity.users u ON u.id=t.assignee_id
            WHERE t.kind='INCIDENT' AND t.incident_id=ANY(@incidents)
            ORDER BY t.incident_id,(t.status IN ('ASSIGNED','IN_PROGRESS','SUBMITTED')) DESC,
                     t.created_at DESC,t.id DESC
            """);
        command.Parameters.AddWithValue("incidents", incidentIds);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(reader.GetGuid(0), new(reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4)));
        return rows;
    }

    private static AdminIncidentResponse Map(Data.Entities.Dispute row, List<IncidentLocation> locations, List<IncidentSlot> slots,
        Dictionary<Guid, IncidentLinkedTask> tasks) =>
        new(row.Id, row.Kind, row.Status, row.Reason, row.CreatedAt, row.ResolvedAt, row.Decision, row.OrderId,
            row.KioskId, row.SlotId, locations.Find(x => x.KioskId == row.KioskId), slots.Find(x => x.Id == row.SlotId),
            tasks.GetValueOrDefault(row.Id));
}
