using System.Security.Cryptography;
using System.Text.Json;
using FloraBot.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Ordering;

public sealed record RefundOrderSummary(Guid Id, string Code, string Status);
public sealed record RefundBankResponse(string BankName, string Holder, string Account);

public static class RefundDestinations
{
    public static Task<Dictionary<Guid, RefundOrderSummary>> OrdersAsync(FloraDbContext db, Guid[] ids, CancellationToken ct) =>
        db.Orders.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new RefundOrderSummary(x.Id, x.OrderCode, x.Status)).ToDictionaryAsync(x => x.Id, ct);

    public static async Task<RefundBankResponse?> ReadAsync(FloraDbContext db, Guid? orderId,
        IDataProtectionProvider protection, bool lockOrder, CancellationToken ct)
    {
        if (!orderId.HasValue) return null;
        var query = lockOrder
            ? db.Orders.FromSqlInterpolated($"SELECT * FROM ordering.orders WHERE id={orderId.Value} FOR UPDATE")
            : db.Orders.Where(x => x.Id == orderId.Value);
        var row = await query.AsNoTracking().Select(x => new { x.RefundBankName, x.RefundBankHolder, x.RefundBankAccountEnc }).SingleOrDefaultAsync(ct);
        if (row?.RefundBankAccountEnc is null) return null;
        try
        {
            var account = protection.CreateProtector("bank-account-v1").Unprotect(row.RefundBankAccountEnc);
            var fields = new Dictionary<string, JsonElement>
            {
                ["p_bank"] = JsonSerializer.SerializeToElement(row.RefundBankName),
                ["p_holder"] = JsonSerializer.SerializeToElement(row.RefundBankHolder),
                ["p_account_enc"] = JsonSerializer.SerializeToElement(account)
            };
            if (RefundBankInput.Normalize(fields) is not null) return null;
            return new(fields["p_bank"].GetString()!, fields["p_holder"].GetString()!, fields["p_account_enc"].GetString()!);
        }
        catch (CryptographicException) { return null; }
    }
}
