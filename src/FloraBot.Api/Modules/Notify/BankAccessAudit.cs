using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Notify;

public static class BankAccessAudit
{
    public static Task RecordRefundAsync(FloraDbContext db, Guid actorId, Guid refundId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT flow.audit({actorId},'REFUND_BANK_VIEWED','payments',{refundId})", ct);

    public static Task RecordWithdrawalAsync(FloraDbContext db, Guid actorId, Guid withdrawalId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT flow.audit({actorId},'WITHDRAWAL_BANK_VIEWED','withdrawal_requests',{withdrawalId})", ct);
}
