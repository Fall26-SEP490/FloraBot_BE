using FloraBot.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace FloraBot.Api.Modules.Identity;

public sealed record SellerBankResponse(string Status, string? BankName, string? Holder, string? AccountSuffix);

public static class SellerBankRead
{
    public static void MapSellerBankRead(this WebApplication app)
    {
        app.MapGet("/api/sellers/{sellerId:guid}/bank", async (Guid sellerId, FloraDbContext db,
            IDataProtectionProvider protection, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var seller = await db.Sellers.AsNoTracking().Where(x => x.Id == sellerId)
                .Select(x => new { x.BankName, x.BankHolder, x.BankAccountNoEnc }).SingleOrDefaultAsync(ct);
            if (seller is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(seller.BankAccountNoEnc)) return Results.Ok(new SellerBankResponse("MISSING", null, null, null));
            try
            {
                var account = protection.CreateProtector("bank-account-v1").Unprotect(seller.BankAccountNoEnc);
                if (!SellerBankInput.ValidAccount(account) || string.IsNullOrWhiteSpace(seller.BankName) || string.IsNullOrWhiteSpace(seller.BankHolder))
                    return Results.Ok(new SellerBankResponse("NEEDS_UPDATE", seller.BankName, seller.BankHolder, null));
                return Results.Ok(new SellerBankResponse("READY", seller.BankName, seller.BankHolder, account.Length > 4 ? account[^4..] : account[^2..]));
            }
            catch (CryptographicException)
            {
                // Legacy demo values or missing keys must never be interpreted as plaintext.
                return Results.Ok(new SellerBankResponse("NEEDS_UPDATE", seller.BankName, seller.BankHolder, null));
            }
        }).RequireAuthorization("Merchant", "SameSeller").Produces<SellerBankResponse>();
    }

    public static async Task<bool?> IsConfiguredAsync(FloraDbContext db, IDataProtectionProvider protection, Guid sellerId, CancellationToken ct)
    {
        var seller = await db.Sellers.AsNoTracking().Where(x => x.Id == sellerId)
            .Select(x => new { x.BankName, x.BankHolder, x.BankAccountNoEnc }).SingleOrDefaultAsync(ct);
        if (seller is null) return null;
        if (string.IsNullOrWhiteSpace(seller.BankAccountNoEnc) || string.IsNullOrWhiteSpace(seller.BankName) || string.IsNullOrWhiteSpace(seller.BankHolder)) return false;
        try { return SellerBankInput.ValidAccount(protection.CreateProtector("bank-account-v1").Unprotect(seller.BankAccountNoEnc)); }
        catch (CryptographicException) { return false; }
    }
}
