using System.Data;
using FloraBot.Api.Data;
using FloraBot.Api.Modules.Notify;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Ordering;

public sealed record AdminComplaintResponse(Guid Id, Guid OrderId, string OrderCode, Guid KioskId,
    string Status, string Reason, DateTime CreatedAt, string? Decision, long? RefundAmount,
    DateTime? ResolvedAt, long OrderTotal);
public sealed record AdminComplaintPage(List<AdminComplaintResponse> Items, int Page, bool HasMore);
public sealed record AdminComplaintDetail(AdminComplaintResponse Complaint, List<ReceiptItem> Items,
    IncidentEvidenceResponse Evidence);

public static class AdminComplaintsRead
{
    private static IQueryable<AdminComplaintResponse> Project(FloraDbContext db, IQueryable<Data.Entities.Dispute> disputes) =>
        from dispute in disputes
        join order in db.Orders.AsNoTracking() on dispute.OrderId equals order.Id
        where dispute.Kind == "COMPLAINT"
        select new AdminComplaintResponse(dispute.Id, order.Id, order.OrderCode, dispute.KioskId,
            dispute.Status, dispute.Reason, dispute.CreatedAt, dispute.Decision, dispute.RefundAmount,
            dispute.ResolvedAt, order.TotalAmount);

    public static void MapAdminComplaintsRead(this WebApplication app)
    {
        app.MapGet("/api/admin/complaints", async (string? status, int? page, Guid? kioskId,
            FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            status ??= "OPEN";
            var number = page ?? 1;
            if (status is not ("OPEN" or "RESOLVED_REJECT" or "RESOLVED_REFUND") || number < 1 || number > 100000)
                return Results.BadRequest();
            var query = db.Disputes.AsNoTracking().Where(x => x.Kind == "COMPLAINT" && x.Status == status);
            if (kioskId.HasValue) query = query.Where(x => x.KioskId == kioskId);
            query = status == "OPEN" ? query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                : query.OrderByDescending(x => x.ResolvedAt).ThenByDescending(x => x.Id);
            var rows = await Project(db, query.Skip((number - 1) * 25).Take(26)).ToListAsync(ct);
            return Results.Ok(new AdminComplaintPage(rows.Take(25).ToList(), number, rows.Count > 25));
        }).RequireAuthorization("Admin").Produces<AdminComplaintPage>();

        app.MapGet("/api/admin/complaints/{complaintId:guid}", async (Guid complaintId,
            FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var row = await Project(db, db.Disputes.AsNoTracking().Where(x => x.Id == complaintId)).SingleOrDefaultAsync(ct);
            if (row is null) return Results.NotFound();
            var items = await db.OrderItems.AsNoTracking().Where(x => x.OrderId == row.OrderId)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .Select(x => new ReceiptItem(x.NameSnapshot, x.Quantity, x.UnitPrice, x.LineTotal, x.LineStatus)).ToListAsync(ct);
            var evidence = await IncidentEvidence.ReadAsync(db, row.Id, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new AdminComplaintDetail(row, items, evidence));
        }).RequireAuthorization("Admin").Produces<AdminComplaintDetail>();
    }
}
