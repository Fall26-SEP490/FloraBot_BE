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
    string Status);

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
    DateTime UpdatedAt);

public sealed record RevealBankRequest(string Reason);

public sealed record RevealBankDetailResponse(
    string BankName,
    string AccountHolder,
    string AccountNumber);

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

            var user = await db.Users.AsNoTracking()
                .Include(u => u.Profile)
                .SingleOrDefaultAsync(u => u.Id == userId, ct);

            if (user is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Tài khoản không tồn tại.");

            PersonalBankInfo? personalBank = null;
            if (user.Profile is not null && (!string.IsNullOrWhiteSpace(user.Profile.BankName) || !string.IsNullOrWhiteSpace(user.Profile.AccountLast4)))
            {
                personalBank = new PersonalBankInfo(
                    user.Profile.BankName ?? "",
                    user.Profile.AccountHolder ?? "",
                    user.Profile.AccountLast4 ?? "",
                    true);
            }

            AdminShopBankInfo? shopBank = null;
            if (user.Role == "SELLER" && user.SellerId.HasValue)
            {
                var seller = await db.Sellers.AsNoTracking()
                    .Where(s => s.Id == user.SellerId.Value)
                    .Select(s => new { s.BankName, s.BankHolder, s.BankAccountNoEnc })
                    .SingleOrDefaultAsync(ct);

                if (seller is not null)
                {
                    if (string.IsNullOrWhiteSpace(seller.BankAccountNoEnc))
                    {
                        shopBank = new AdminShopBankInfo(null, null, null, "MISSING");
                    }
                    else
                    {
                        try
                        {
                            var account = protection.CreateProtector("bank-account-v1").Unprotect(seller.BankAccountNoEnc);
                            if (!SellerBankInput.ValidAccount(account) || string.IsNullOrWhiteSpace(seller.BankName) || string.IsNullOrWhiteSpace(seller.BankHolder))
                                shopBank = new AdminShopBankInfo(seller.BankName, seller.BankHolder, null, "NEEDS_UPDATE");
                            else
                                shopBank = new AdminShopBankInfo(seller.BankName, seller.BankHolder, account.Length > 4 ? account[^4..] : account[^2..], "READY");
                        }
                        catch (CryptographicException)
                        {
                            shopBank = new AdminShopBankInfo(seller.BankName, seller.BankHolder, null, "NEEDS_UPDATE");
                        }
                    }
                }
            }

            var response = new AdminAccountProfileResponse(
                user.Id,
                user.Role,
                user.Status,
                user.Email ?? "",
                user.FullName,
                user.Phone,
                user.Profile?.Address,
                user.Profile?.ContactEmail,
                user.Profile?.ContactEmailVerifiedAt.HasValue == true,
                user.SellerId,
                personalBank,
                shopBank,
                user.CreatedAt,
                user.UpdatedAt);

            return Results.Ok(response);
        })
        .RequireAuthorization("Admin")
        .WithTags("Identity")
        .WithSummary("Xem thông tin hồ sơ chi tiết của bất kỳ tài khoản nào cho Admin")
        .Produces<AdminAccountProfileResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // 3. POST /api/admin/accounts/{userId}/bank/reveal
        app.MapPost("/api/admin/accounts/{userId:guid}/bank/reveal", async (
            Guid userId,
            RevealBankRequest request,
            FloraDbContext db,
            IDataProtectionProvider protection,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var adminId))
                return Results.Unauthorized();

            var reason = request?.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length is < 5 or > 1000)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Lý do xem thông tin ngân hàng phải từ 5 đến 1000 ký tự.");

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Recheck active admin status within transaction
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT flow.require_active_admin({adminId})", ct);

            // Lock target user row
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM identity.users WHERE id = {userId} FOR UPDATE", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT user_id FROM identity.user_profiles WHERE user_id = {userId} FOR UPDATE", ct);

            var user = await db.Users.Include(u => u.Profile).SingleOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Tài khoản không tồn tại.");
            }

            if (user.Profile is null || string.IsNullOrWhiteSpace(user.Profile.AccountEncrypted) || string.IsNullOrWhiteSpace(user.Profile.BankName))
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Tài khoản chưa cấu hình thông tin ngân hàng cá nhân.");
            }

            string rawAccount;
            try
            {
                rawAccount = protection.CreateProtector("personal-bank-account-v1").Unprotect(user.Profile.AccountEncrypted);
            }
            catch (CryptographicException)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Không thể giải mã số tài khoản ngân hàng cá nhân.");
            }

            if (string.IsNullOrWhiteSpace(rawAccount))
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Số tài khoản ngân hàng cá nhân không hợp lệ.");
            }

            // Audit before disclosure - no banking secrets in payload!
            var payload = JsonSerializer.Serialize(new { reason });
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT flow.audit({adminId}, 'ADMIN_PERSONAL_BANK_REVEALED', 'user_profiles', {user.Id}, {payload}::jsonb)", ct);

            await tx.CommitAsync(ct);

            return Results.Ok(new RevealBankDetailResponse(
                user.Profile.BankName,
                user.Profile.AccountHolder ?? "",
                rawAccount));
        })
        .RequireAuthorization("Admin")
        .WithTags("Identity")
        .WithSummary("Giải mã và hiển thị số tài khoản ngân hàng cá nhân cho Admin với kiểm toán bắt buộc")
        .Produces<RevealBankDetailResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // 4. POST /api/admin/sellers/{sellerId}/bank/reveal
        app.MapPost("/api/admin/sellers/{sellerId:guid}/bank/reveal", async (
            Guid sellerId,
            RevealBankRequest request,
            FloraDbContext db,
            IDataProtectionProvider protection,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var adminId))
                return Results.Unauthorized();

            var reason = request?.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length is < 5 or > 1000)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Lý do xem thông tin ngân hàng phải từ 5 đến 1000 ký tự.");

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Recheck active admin status within transaction
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT flow.require_active_admin({adminId})", ct);

            // Lock target seller row
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM identity.sellers WHERE id = {sellerId} FOR UPDATE", ct);

            var seller = await db.Sellers.SingleOrDefaultAsync(s => s.Id == sellerId, ct);
            if (seller is null)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Shop không tồn tại.");
            }

            if (string.IsNullOrWhiteSpace(seller.BankAccountNoEnc) || string.IsNullOrWhiteSpace(seller.BankName))
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Shop chưa cấu hình thông tin ngân hàng.");
            }

            string rawAccount;
            try
            {
                rawAccount = protection.CreateProtector("bank-account-v1").Unprotect(seller.BankAccountNoEnc);
            }
            catch (CryptographicException)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Không thể giải mã số tài khoản ngân hàng của shop.");
            }

            if (!SellerBankInput.ValidAccount(rawAccount))
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Số tài khoản ngân hàng của shop không hợp lệ.");
            }

            // Audit before disclosure - no banking secrets in payload!
            var payload = JsonSerializer.Serialize(new { reason });
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT flow.audit({adminId}, 'ADMIN_SELLER_BANK_REVEALED', 'sellers', {seller.Id}, {payload}::jsonb)", ct);

            await tx.CommitAsync(ct);

            return Results.Ok(new RevealBankDetailResponse(
                seller.BankName,
                seller.BankHolder ?? "",
                rawAccount));
        })
        .RequireAuthorization("Admin")
        .WithTags("Identity")
        .WithSummary("Giải mã và hiển thị số tài khoản ngân hàng của shop cho Admin với kiểm toán bắt buộc")
        .Produces<RevealBankDetailResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
