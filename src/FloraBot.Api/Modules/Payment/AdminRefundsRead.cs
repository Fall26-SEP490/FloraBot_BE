using System.Data;
using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.Ordering;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Payment;

public sealed record AdminRefundResponse(Guid Id, Guid? OrderId, string? OrderCode, long Amount,
    string Status, string? Reason, DateTime CreatedAt, Guid? ApprovedBy, bool RequiresApproval,
    Guid? PaidBy, DateTime? PaidAt);
public sealed record AdminRefundPage(List<AdminRefundResponse> Items, int Page, bool HasMore);
public sealed record AdminRefundDetail(AdminRefundResponse Refund, string BankStatus, bool CanApprove, bool CanAccessBank);

public static class AdminRefundsRead
{
    public static void MapAdminRefundsRead(this WebApplication app)
    {
        app.MapGet("/api/admin/refunds", async (string? status, int? page, Guid? orderId,
            FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            status ??= "PENDING";
            var number = page ?? 1;
            if (status is not ("PENDING" or "SUCCEEDED" or "FAILED" or "CANCELLED" or "EXPIRED") || number < 1 || number > 100000)
                return Results.BadRequest();
            var query = db.Payments.AsNoTracking().Where(x => x.Kind == "REFUND" && x.Status == status);
            if (orderId.HasValue) query = query.Where(x => x.OrderId == orderId);
            query = status == "PENDING" ? query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                : query.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id);
            var rows = await query.Skip((number - 1) * 25).Take(26).ToListAsync(ct);
            var orders = await RefundDestinations.OrdersAsync(db, rows.Where(x => x.OrderId.HasValue).Select(x => x.OrderId!.Value).Distinct().ToArray(), ct);
            var approvers = await AdminApprovers.ReadAsync(db, rows.Where(x => x.ApprovedBy.HasValue).Select(x => x.ApprovedBy!.Value).Distinct().ToArray(), ct);
            return Results.Ok(new AdminRefundPage(rows.Take(25).Select(x => Map(x, orders, approvers)).ToList(), number, rows.Count > 25));
        }).RequireAuthorization("Admin").Produces<AdminRefundPage>();

        app.MapGet("/api/admin/refunds/{refundId:guid}", async (Guid refundId, FloraDbContext db,
            IDataProtectionProvider protection, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var row = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == refundId && x.Kind == "REFUND", ct);
            if (row is null) return Results.NotFound();
            var orders = await RefundDestinations.OrdersAsync(db, row.OrderId.HasValue ? [row.OrderId.Value] : [], ct);
            var approvers = await AdminApprovers.ReadAsync(db, row.ApprovedBy.HasValue ? [row.ApprovedBy.Value] : [], ct);
            var mapped = Map(row, orders, approvers);
            var pending = row.Status == "PENDING";
            var destination = pending ? await RefundDestinations.ReadAsync(db, row.OrderId, protection, false, ct) : null;
            var canAccess = pending && !mapped.RequiresApproval && row.ApprovedBy.ToString() != http.User.FindFirstValue("sub") && destination is not null;
            await transaction.CommitAsync(ct);
            return Results.Ok(new AdminRefundDetail(mapped, pending ? destination is null ? "NEEDS_UPDATE" : "READY" : "NOT_APPLICABLE",
                pending && mapped.RequiresApproval, canAccess));
        }).RequireAuthorization("Admin").Produces<AdminRefundDetail>();

        app.MapPost("/api/admin/refunds/{refundId:guid}/bank-details", async (Guid refundId, FloraDbContext db,
            IDataProtectionProvider protection, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actor = Guid.Parse(http.User.FindFirstValue("sub")!);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT flow.require_active_admin({actor})", ct);
            // Match confirm_refund's payment-then-order lock order; never reveal a paid destination.
            var row = await db.Payments.FromSqlInterpolated($"SELECT * FROM payment.payments WHERE id={refundId} AND kind='REFUND' FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is null) return Results.NotFound();
            if (row.Status != "PENDING") return Results.Conflict(new { detail = "Khoản hoàn không còn chờ chi." });
            var approvers = await AdminApprovers.ReadAsync(db, row.ApprovedBy.HasValue ? [row.ApprovedBy.Value] : [], ct);
            if (!row.ApprovedBy.HasValue || !approvers.Contains(row.ApprovedBy.Value))
                return Results.Conflict(new { detail = "Khoản hoàn tự động cần quản trị viên duyệt trước khi chi." });
            if (row.ApprovedBy == actor) return Results.Conflict(new { detail = "Người chi phải khác người duyệt." });
            var bank = await RefundDestinations.ReadAsync(db, row.OrderId, protection, true, ct);
            if (bank is null) return Results.Conflict(new { detail = "Chưa có tài khoản nhận hoàn hợp lệ. Khách cần cập nhật qua biên nhận." });
            await BankAccessAudit.RecordRefundAsync(db, actor, refundId, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(bank);
        }).RequireAuthorization("Admin").Produces<RefundBankResponse>();
    }

    private static AdminRefundResponse Map(Data.Entities.Payment row, Dictionary<Guid, RefundOrderSummary> orders, HashSet<Guid> approvers) =>
        new(row.Id, row.OrderId, row.OrderId.HasValue ? orders.GetValueOrDefault(row.OrderId.Value)?.Code : null,
            row.Amount, row.Status, row.Reason, row.CreatedAt, row.ApprovedBy,
            !row.ApprovedBy.HasValue || !approvers.Contains(row.ApprovedBy.Value), row.PaidBy, row.PaidAt);
}
