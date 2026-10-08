using System.Data;
using FloraBot.Api.Data;
using FloraBot.Api.Modules.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Payment;

public sealed record WalletBalanceResponse(Guid? SellerId, decimal? PendingBalance, decimal? AvailableBalance, decimal? Debt);
public sealed record WalletEntryResponse(Guid Id, DateTime CreatedAt, string Account, long Amount, string RefType, string? Memo);
public sealed record WithdrawalResponse(Guid Id, long Amount, string Status, DateTime CreatedAt, DateTime? PaidAt, string? RejectReason);
public sealed record WalletResponse(WalletBalanceResponse? Balance, List<WalletEntryResponse> Entries,
    bool BankConfigured, long MinimumWithdrawal, List<WithdrawalResponse> Withdrawals, bool HasMoreEntries, bool HasMoreWithdrawals);

public static class WalletRead
{
    public static void MapWalletRead(this WebApplication app)
    {
        app.MapGet("/api/sellers/{sellerId:guid}/wallet", async (Guid sellerId, FloraDbContext db, IDataProtectionProvider protection, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            // Balance and history must describe the same committed ledger snapshot.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var configured = await SellerBankRead.IsConfiguredAsync(db, protection, sellerId, ct);
            if (configured is null) return Results.NotFound();
            var balance = await db.VSellerBalances.AsNoTracking().Where(x => x.SellerId == sellerId)
                .Select(x => new WalletBalanceResponse(x.SellerId, x.PendingBalance, x.AvailableBalance, x.Debt)).SingleOrDefaultAsync(ct);
            var entries = await db.LedgerEntries.AsNoTracking().Where(x => x.SellerId == sellerId)
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(101)
                .Select(x => new WalletEntryResponse(x.Id, x.CreatedAt, x.Account, x.Amount, x.RefType, x.Memo)).ToListAsync(ct);
            var withdrawals = await db.WithdrawalRequests.AsNoTracking().Where(x => x.SellerId == sellerId)
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(101)
                .Select(x => new WithdrawalResponse(x.Id, x.Amount, x.Status, x.CreatedAt, x.PaidAt, x.RejectReason)).ToListAsync(ct);
            var minimum = await db.Database.SqlQueryRaw<long>("SELECT flow.cfg('withdraw_min') AS \"Value\"").SingleAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new WalletResponse(balance, entries.Take(100).ToList(), configured.Value, minimum,
                withdrawals.Take(100).ToList(), entries.Count > 100, withdrawals.Count > 100));
        }).RequireAuthorization("Merchant", "SameSeller").Produces<WalletResponse>();
    }
}
