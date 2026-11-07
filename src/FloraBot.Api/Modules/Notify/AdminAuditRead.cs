using System.Security.Claims;
using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Notify;

public sealed record AuditEntry(long Id, string ActorType, Guid? ActorId, string Action,
    string EntityType, Guid? EntityId, DateTime CreatedAt, bool PayloadRedacted);
public sealed record AuditPage(List<AuditEntry> Items, int Page, bool HasMore);

public static class AdminAuditRead
{
    public static void MapAdminAuditRead(this WebApplication app)
    {
        app.MapGet("/api/admin/audit-logs", async (
            Guid? actorId, string? action, string? entityType, Guid? entityId,
            DateTime? from, DateTime? to, int? page, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
            if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == actor && x.Role == "ADMIN" && x.Status == "ACTIVE", ct))
                return Results.Forbid();
            var number = page.GetValueOrDefault(1);
            if (number is < 1 or > 100000 || action?.Length > 100 || entityType?.Length > 100 || from > to)
                return Results.BadRequest();
            var query = db.AuditLogs.AsNoTracking().AsQueryable();
            if (actorId.HasValue) query = query.Where(x => x.ActorId == actorId);
            if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action == action.Trim());
            if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(x => x.EntityType == entityType.Trim());
            if (entityId.HasValue) query = query.Where(x => x.EntityId == entityId);
            if (from.HasValue) query = query.Where(x => x.CreatedAt >= from.Value);
            if (to.HasValue) query = query.Where(x => x.CreatedAt < to.Value);
            var rows = await query.OrderByDescending(x => x.Id).Skip((number - 1) * 50).Take(51)
                .Select(x => new AuditEntry(x.Id, x.ActorType, x.ActorId, x.Action,
                    x.EntityType, x.EntityId, x.CreatedAt, true)).ToListAsync(ct);
            return Results.Ok(new AuditPage(rows.Take(50).ToList(), number, rows.Count > 50));
        }).RequireAuthorization("Admin").WithTags("Audit").Produces<AuditPage>();

        app.MapGet("/api/admin/audit-logs/{id:long}", async (
            long id, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
            if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == actor && x.Role == "ADMIN" && x.Status == "ACTIVE", ct))
                return Results.Forbid();
            var row = await db.AuditLogs.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new AuditEntry(x.Id, x.ActorType, x.ActorId, x.Action,
                    x.EntityType, x.EntityId, x.CreatedAt, true)).SingleOrDefaultAsync(ct);
            return row is null ? Results.NotFound() : Results.Ok(row);
        }).RequireAuthorization("Admin").WithTags("Audit").Produces<AuditEntry>();
    }
}
