using FloraBot.Api.Data;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Notify;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Ordering;

public sealed record AdminIncidentResponse(Guid Id, string Kind, string Status, string Reason, DateTime CreatedAt,
    DateTime? ResolvedAt, string? Decision, Guid? OrderId, Guid KioskId, Guid? SlotId,
    IncidentLocation? Location, IncidentSlot? Slot);
public sealed record AdminIncidentPage(List<AdminIncidentResponse> Items, int Page, bool HasMore);
public sealed record AdminIncidentDetail(AdminIncidentResponse Incident, IncidentEvidenceResponse Evidence);

public static class AdminIncidentsRead
{
    public static void MapAdminIncidentsRead(this WebApplication app)
    {
        app.MapGet("/api/admin/incidents", async (string? status, int? page, Guid? kioskId, FloraDbContext db, HttpContext http, CancellationToken ct) =>
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
            return Results.Ok(new AdminIncidentPage(rows.Take(25).Select(x => Map(x, locations, slots)).ToList(), number, rows.Count > 25));
        }).RequireAuthorization("Admin").Produces<AdminIncidentPage>();

        app.MapGet("/api/admin/incidents/{incidentId:guid}", async (Guid incidentId, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var row = await db.Disputes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == incidentId && (x.Kind == "DEVICE_FAULT" || x.Kind == "DISPENSE_FAILED"), ct);
            if (row is null) return Results.NotFound();
            var locations = await IncidentLocations.KiosksAsync(db, [row.KioskId], ct);
            var slots = await IncidentLocations.SlotsAsync(db, row.SlotId.HasValue ? [row.SlotId.Value] : [], ct);
            return Results.Ok(new AdminIncidentDetail(Map(row, locations, slots), await IncidentEvidence.ReadAsync(db, row.Id, ct)));
        }).RequireAuthorization("Admin").Produces<AdminIncidentDetail>();
    }

    private static AdminIncidentResponse Map(Data.Entities.Dispute row, List<IncidentLocation> locations, List<IncidentSlot> slots) =>
        new(row.Id, row.Kind, row.Status, row.Reason, row.CreatedAt, row.ResolvedAt, row.Decision, row.OrderId,
            row.KioskId, row.SlotId, locations.Find(x => x.KioskId == row.KioskId), slots.Find(x => x.Id == row.SlotId));
}
