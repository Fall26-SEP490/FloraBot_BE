using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Identity;

public sealed record MaskedBankSummary(
    string? BankName,
    string? AccountHolder,
    string? AccountLast4,
    bool HasBank);

public sealed record AdminAccountSummary(
    Guid Id,
    string Role,
    string Status,
    string Email,
    string FullName,
    string? Phone,
    string? Address,
    string? ContactEmail,
    bool ContactEmailVerified,
    MaskedBankSummary? Bank,
    Guid? SellerId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AdminAccountPage(
    List<AdminAccountSummary> Items,
    int Page,
    bool HasMore);

public sealed record AdminShopBankInfo(
    string? BankName,
    string? BankHolder,
    string? AccountSuffix,
    string Status,
    string? AccountNumber = null);

public sealed record AdminAccountProfileResponse(
    Guid Id,
    string Role,
    string Status,
    string LoginEmail,
    string FullName,
    string? Phone,
    string? Address,
    string? ContactEmail,
    bool ContactEmailVerified,
    Guid? SellerId,
    PersonalBankInfo? PersonalBank,
    AdminShopBankInfo? ShopBank,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? Gender = null,
    DateOnly? DateOfBirth = null);

public static class AdminAccountEndpoints
{
    private static readonly string[] ValidRolesArray =
    [
        "ADMIN", "OPERATIONS_MANAGER", "TECHNICIAN", "SELLER", "SELLER_STAFF", "CUSTOMER"
    ];

    private static readonly HashSet<string> ValidRoles = new(ValidRolesArray, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACTIVE", "LOCKED", "DISABLED"
    };

    public static void MapAdminAccounts(this WebApplication app)
    {
        // 1. GET /api/admin/accounts
        app.MapGet("/api/admin/accounts", async (
            string? search,
            string? role,
            string? status,
            int? page,
            int? pageSize,
            FloraDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            var pageNumber = page ?? 1;
            var size = pageSize ?? 25;
            if (pageNumber < 1 || pageNumber > 100000 || size < 1 || size > 100)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Tham số phân trang không hợp lệ.");

            // Default: strictly filter where ValidRolesArray contains u.Role to avoid exposing retired/legacy STAFF
            var query = db.Users.AsNoTracking().Include(u => u.Profile)
                .Where(u => ValidRolesArray.Contains(u.Role))
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(role))
            {
                var upperRole = role.Trim().ToUpperInvariant();
                if (!ValidRoles.Contains(upperRole))
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Vai trò không hợp lệ.");
                query = query.Where(u => u.Role == upperRole);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                var upperStatus = status.Trim().ToUpperInvariant();
                if (!ValidStatuses.Contains(upperStatus))
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Trạng thái không hợp lệ.");
                query = query.Where(u => u.Status == upperStatus);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                if (term.Length > 100)
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Từ khóa tìm kiếm tối đa 100 ký tự.");
                query = query.Where(u => EF.Functions.ILike(u.FullName, $"%{term}%") ||
                                         (u.Email != null && EF.Functions.ILike(u.Email, $"%{term}%")) ||
                                         (u.Phone != null && EF.Functions.ILike(u.Phone, $"%{term}%")));
            }

            query = query.OrderByDescending(u => u.CreatedAt).ThenByDescending(u => u.Id);
            var rows = await query.Skip((pageNumber - 1) * size).Take(size + 1).ToListAsync(ct);

            var items = rows.Take(size).Select(u =>
            {
                var prof = u.Profile;
                MaskedBankSummary? bankSummary = null;
                if (prof is not null && (!string.IsNullOrWhiteSpace(prof.BankName) || !string.IsNullOrWhiteSpace(prof.AccountLast4)))
                {
                    bankSummary = new MaskedBankSummary(prof.BankName, prof.AccountHolder, prof.AccountLast4, true);
                }

                return new AdminAccountSummary(
                    u.Id,
                    u.Role,
                    u.Status,
                    u.Email ?? "",
                    u.FullName,
                    u.Phone,
                    prof?.Address,
                    prof?.ContactEmail,
                    prof?.ContactEmailVerifiedAt.HasValue == true,
                    bankSummary,
                    u.SellerId,
                    u.CreatedAt,
                    u.UpdatedAt);
            }).ToList();

            return Results.Ok(new AdminAccountPage(items, pageNumber, rows.Count > size));
        })
        .RequireAuthorization("Admin")
        .WithTags("Identity")
        .WithSummary("Danh sách tài khoản hệ thống cho Admin, hỗ trợ tìm kiếm, lọc theo vai trò và trạng thái")
        .Produces<AdminAccountPage>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        // 2. GET /api/admin/accounts/{userId}/profile
        app.MapGet("/api/admin/accounts/{userId:guid}/profile", async (
            Guid userId,
            FloraDbContext db,
            IDataProtectionProvider protection,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var adminId))
                return Results.Unauthorized();

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Recheck active admin status within transaction
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT flow.require_active_admin({adminId})", ct);

            // Row-level lock on target user and profile for consistent read
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM identity.users WHERE id = {userId} FOR SHARE", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT user_id FROM identity.user_profiles WHERE user_id = {userId} FOR SHARE", ct);

            var user = await db.Users
                .Include(u => u.Profile)
                .SingleOrDefaultAsync(u => u.Id == userId, ct);

            if (user is null)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Tài khoản không tồn tại.");
            }

            PersonalBankInfo? personalBank = null;
            bool hasPersonalBank = false;
            var prof = user.Profile;
            if (prof is not null && (!string.IsNullOrWhiteSpace(prof.BankName) || !string.IsNullOrWhiteSpace(prof.AccountLast4)))
            {
                if (string.IsNullOrWhiteSpace(prof.AccountEncrypted))
                {
                    personalBank = new PersonalBankInfo(
                        prof.BankName ?? "",
                        prof.AccountHolder ?? "",
                        prof.AccountLast4 ?? "",
                        true,
                        null,
                        "NEEDS_UPDATE");
                }
                else
                {
                    hasPersonalBank = true;
                    try
                    {
                        var raw = protection.CreateProtector("personal-bank-account-v1").Unprotect(prof.AccountEncrypted);
                        if (!SellerBankInput.ValidAccount(raw))
                        {
                            personalBank = new PersonalBankInfo(
                                prof.BankName ?? "",
                                prof.AccountHolder ?? "",
                                prof.AccountLast4 ?? "",
                                true,
                                null,
                                "NEEDS_UPDATE");
                        }
                        else
                        {
                            personalBank = new PersonalBankInfo(
                                prof.BankName ?? "",
                                prof.AccountHolder ?? "",
                                prof.AccountLast4 ?? "",
                                true,
                                raw,
                                "READY");
                        }
                    }
                    catch (CryptographicException)
                    {
                        personalBank = new PersonalBankInfo(
                            prof.BankName ?? "",
                            prof.AccountHolder ?? "",
                            prof.AccountLast4 ?? "",
                            true,
                            null,
                            "NEEDS_UPDATE");
                    }
                }
            }

            AdminShopBankInfo? shopBank = null;
            bool hasShopBank = false;
            if (user.Role == "SELLER" && user.SellerId.HasValue)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM identity.sellers WHERE id = {user.SellerId.Value} FOR SHARE", ct);

                var seller = await db.Sellers
                    .Where(s => s.Id == user.SellerId.Value)
                    .Select(s => new { s.BankName, s.BankHolder, s.BankAccountNoEnc })
                    .SingleOrDefaultAsync(ct);

                if (seller is not null)
                {
                    if (string.IsNullOrWhiteSpace(seller.BankAccountNoEnc))
                    {
                        shopBank = new AdminShopBankInfo(null, null, null, "MISSING", null);
                    }
                    else
                    {
                        hasShopBank = true;
                        try
                        {
                            var account = protection.CreateProtector("bank-account-v1").Unprotect(seller.BankAccountNoEnc);
                            if (!SellerBankInput.ValidAccount(account) || string.IsNullOrWhiteSpace(seller.BankName) || string.IsNullOrWhiteSpace(seller.BankHolder))
                            {
                                shopBank = new AdminShopBankInfo(seller.BankName, seller.BankHolder, null, "NEEDS_UPDATE", null);
                            }
                            else
                            {
                                var suffix = account.Length > 4 ? account[^4..] : account;
                                shopBank = new AdminShopBankInfo(seller.BankName, seller.BankHolder, suffix, "READY", account);
                            }
                        }
                        catch (CryptographicException)
                        {
                            shopBank = new AdminShopBankInfo(seller.BankName, seller.BankHolder, null, "NEEDS_UPDATE", null);
                        }
                    }
                }
            }

            // Audit before disclosure - no banking secrets in payload!
            var auditPayload = JsonSerializer.Serialize(new
            {
                target_user_id = user.Id,
                has_personal_bank = hasPersonalBank,
                has_shop_bank = hasShopBank
            });
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT flow.audit({adminId}, 'ADMIN_ACCOUNT_PROFILE_VIEWED', 'users', {user.Id}, {auditPayload}::jsonb)", ct);

            await tx.CommitAsync(ct);

            var response = new AdminAccountProfileResponse(
                user.Id,
                user.Role,
                user.Status,
                user.Email ?? "",
                user.FullName,
                user.Phone,
                user.Profile?.Address,
                user.Profile?.ContactEmailVerifiedAt is not null && !string.IsNullOrWhiteSpace(user.Profile.ContactEmail)
                    ? user.Profile.ContactEmail : user.Email,
                user.Profile?.ContactEmailVerifiedAt.HasValue == true,
                user.SellerId,
                personalBank,
                shopBank,
                user.CreatedAt,
                user.UpdatedAt,
                user.Profile?.Gender,
                user.Profile?.DateOfBirth);

            return Results.Ok(response);
        })
        .RequireAuthorization("Admin")
        .WithTags("Identity")
        .WithSummary("Xem thông tin hồ sơ chi tiết của bất kỳ tài khoản nào cho Admin")
        .Produces<AdminAccountProfileResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
