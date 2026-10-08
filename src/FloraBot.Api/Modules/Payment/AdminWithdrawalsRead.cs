using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Payment;

public sealed record AdminWithdrawalResponse(Guid Id, Guid SellerId, string? ShopName, long Amount, string Status,
    DateTime CreatedAt, Guid? ApprovedBy, DateTime? ApprovedAt, Guid? PaidBy, DateTime? PaidAt, string? RejectReason);
public sealed record AdminWithdrawalPage(List<AdminWithdrawalResponse> Items, int Page, bool HasMore);
public sealed record AdminWithdrawalDetail(AdminWithdrawalResponse Withdrawal, decimal AvailableBalance,
    string BankStatus, string? BankName, string? Holder, string? AccountSuffix, bool CanAccessBank);
public sealed record WithdrawalBankResponse(string BankName, string Holder, string Account);

public static class AdminWithdrawalsRead
{
    public static void MapAdminWithdrawalsRead(this WebApplication app)
    {
        app.MapGet("/api/admin/withdrawals", async (string? status, int? page, Guid? sellerId, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            status ??= "PENDING";
            var number = page ?? 1;
            if (status is not ("PENDING" or "APPROVED" or "PAID" or "REJECTED") || number < 1 || number > 100000) return Results.BadRequest();
            var query = db.WithdrawalRequests.AsNoTracking().Where(x => x.Status == status);
            if (sellerId.HasValue) query = query.Where(x => x.SellerId == sellerId);
            query = status is "PENDING" or "APPROVED" ? query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id) : query.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id);
            var rows = await query.Skip((number - 1) * 25).Take(26).ToListAsync(ct);
            var names = await MerchantNames.ReadAsync(db, rows.Select(x => x.SellerId).Distinct().ToArray(), ct);
            return Results.Ok(new AdminWithdrawalPage(rows.Take(25).Select(x => Map(x, names.GetValueOrDefault(x.SellerId))).ToList(), number, rows.Count > 25));
        }).RequireAuthorization("Admin").Produces<AdminWithdrawalPage>();

        app.MapGet("/api/admin/withdrawals/{withdrawalId:guid}", async (Guid withdrawalId, FloraDbContext db,
            IDataProtectionProvider protection, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var row = await db.WithdrawalRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == withdrawalId, ct);
            if (row is null) return Results.NotFound();
            var names = await MerchantNames.ReadAsync(db, [row.SellerId], ct);
            var balance = await db.VSellerBalances.AsNoTracking().Where(x => x.SellerId == row.SellerId).Select(x => x.AvailableBalance).SingleOrDefaultAsync(ct) ?? 0;
            var account = Account(row, protection);
            var allowed = row.Status == "APPROVED" && row.ApprovedBy.HasValue && row.ApprovedBy.ToString() != http.User.FindFirstValue("sub") && account is not null;
            await transaction.CommitAsync(ct);
            return Results.Ok(new AdminWithdrawalDetail(Map(row, names.GetValueOrDefault(row.SellerId)), balance,
                account is null ? "NEEDS_UPDATE" : "READY", row.BankName, row.BankHolder,
                account is null ? null : account.Length > 4 ? account[^4..] : account[^2..], allowed));
        }).RequireAuthorization("Admin").Produces<AdminWithdrawalDetail>();

        // Explicit access is audited before disclosing the immutable withdrawal destination.
        app.MapPost("/api/admin/withdrawals/{withdrawalId:guid}/bank-details", async (Guid withdrawalId,
            FloraDbContext db, IDataProtectionProvider protection, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actor = Guid.Parse(http.User.FindFirstValue("sub")!);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT flow.require_active_admin({actor})", ct);
            var row = await db.WithdrawalRequests.FromSqlInterpolated($"SELECT * FROM payment.withdrawal_requests WHERE id={withdrawalId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is null) return Results.NotFound();
            if (row.Status != "APPROVED" || row.ApprovedBy is null) return Results.Conflict(new { detail = "Yêu cầu phải được duyệt và còn chờ chi." });
            if (row.ApprovedBy == actor) return Results.Conflict(new { detail = "Người chi phải khác người duyệt." });
            var account = Account(row, protection);
            if (account is null) return Results.Conflict(new { detail = "Không đọc được tài khoản nhận tiền của yêu cầu này. Cần đối chiếu và xử lý yêu cầu trước khi chi." });
            await BankAccessAudit.RecordWithdrawalAsync(db, actor, withdrawalId, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new WithdrawalBankResponse(row.BankName, row.BankHolder, account));
        }).RequireAuthorization("Admin").Produces<WithdrawalBankResponse>();
    }

    private static string? Account(WithdrawalRequest row, IDataProtectionProvider protection)
    {
        if (string.IsNullOrWhiteSpace(row.BankAccountNoEnc) || string.IsNullOrWhiteSpace(row.BankName) || string.IsNullOrWhiteSpace(row.BankHolder)) return null;
        try
        {
            var value = protection.CreateProtector("bank-account-v1").Unprotect(row.BankAccountNoEnc);
            return SellerBankInput.ValidAccount(value) ? value : null;
        }
        catch (CryptographicException) { return null; }
    }

    private static AdminWithdrawalResponse Map(WithdrawalRequest row, string? shopName) =>
        new(row.Id, row.SellerId, shopName, row.Amount, row.Status, row.CreatedAt, row.ApprovedBy, row.ApprovedAt, row.PaidBy, row.PaidAt, row.RejectReason);
}
