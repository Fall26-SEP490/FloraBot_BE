using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public sealed record SellerLoyaltySettingsResponse(
    Guid SellerId,
    bool Enabled,
    decimal EarnRatePercent);

public sealed record UpdateSellerLoyaltySettingsRequest(bool Enabled);

public sealed record ShopLoyaltyBalanceItem(
    Guid SellerId,
    string ShopName,
    long Balance,
    long Debt,
    bool LoyaltyEnabled,
    decimal EarnRatePercent);

public sealed record MemberLoyaltyPageResponse(
    List<ShopLoyaltyBalanceItem> Items,
    int Page,
    int PageSize,
    bool HasMore);

public static class ShopLoyalty
{
    public static void MapShopLoyalty(this IEndpointRouteBuilder app)
    {
        // 1. GET /api/sellers/{sellerId}/loyalty-settings
        app.MapGet("/api/sellers/{sellerId:guid}/loyalty-settings", async (
            Guid sellerId,
            NpgsqlDataSource data,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId))
                return Results.Unauthorized();

            await using var conn = await data.OpenConnectionAsync(ct);

            // Recheck owner SELLER active DB only
            await using (var authCmd = new NpgsqlCommand("""
                SELECT u.id
                FROM identity.users u
                JOIN identity.sellers s ON s.id = u.seller_id
                WHERE u.id = @actorId
                  AND u.role = 'SELLER'
                  AND u.seller_id = @sellerId
                  AND u.status = 'ACTIVE'
                  AND s.status = 'ACTIVE'
                FOR SHARE
                """, conn))
            {
                authCmd.Parameters.AddWithValue("actorId", actorId);
                authCmd.Parameters.AddWithValue("sellerId", sellerId);
                var valid = await authCmd.ExecuteScalarAsync(ct);
                if (valid is null) return Results.Forbid();
            }

            await using var queryCmd = new NpgsqlCommand("""
                SELECT enabled, earn_rate_percent
                FROM identity.seller_loyalty_settings
                WHERE seller_id = @sellerId
                """, conn);
            queryCmd.Parameters.AddWithValue("sellerId", sellerId);

            await using var reader = await queryCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return Results.Ok(new SellerLoyaltySettingsResponse(
                    sellerId,
                    reader.GetBoolean(0),
                    reader.GetDecimal(1)));
            }

            // Default OFF opt-in, rate fixed at 1.00%
            return Results.Ok(new SellerLoyaltySettingsResponse(sellerId, false, 1.00m));
        })
        .RequireAuthorization("Seller", "SameSeller")
        .WithTags("Identity")
        .WithSummary("Lấy cài đặt tích điểm của shop (chỉ chủ shop đang hoạt động)")
        .Produces<SellerLoyaltySettingsResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        // 2. PUT /api/sellers/{sellerId}/loyalty-settings
        app.MapPut("/api/sellers/{sellerId:guid}/loyalty-settings", async (
            Guid sellerId,
            UpdateSellerLoyaltySettingsRequest request,
            NpgsqlDataSource data,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId))
                return Results.Unauthorized();

            await using var conn = await data.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // Recheck owner SELLER active DB only with lock
            await using (var authCmd = new NpgsqlCommand("""
                SELECT u.id
                FROM identity.users u
                JOIN identity.sellers s ON s.id = u.seller_id
                WHERE u.id = @actorId
                  AND u.role = 'SELLER'
                  AND u.seller_id = @sellerId
                  AND u.status = 'ACTIVE'
                  AND s.status = 'ACTIVE'
                FOR SHARE
                """, conn, tx))
            {
                authCmd.Parameters.AddWithValue("actorId", actorId);
                authCmd.Parameters.AddWithValue("sellerId", sellerId);
                var valid = await authCmd.ExecuteScalarAsync(ct);
                if (valid is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Forbid();
                }
            }

            // Atomic upsert with fixed rate 1.00%
            await using (var upsertCmd = new NpgsqlCommand("""
                INSERT INTO identity.seller_loyalty_settings (seller_id, enabled, earn_rate_percent, updated_at)
                VALUES (@sellerId, @enabled, 1.00, public.app_now())
                ON CONFLICT (seller_id) DO UPDATE
                SET enabled = @enabled, updated_at = public.app_now()
                """, conn, tx))
            {
                upsertCmd.Parameters.AddWithValue("sellerId", sellerId);
                upsertCmd.Parameters.AddWithValue("enabled", request.Enabled);
                await upsertCmd.ExecuteNonQueryAsync(ct);
            }

            // Atomic audit log in same transaction
            await using (var auditCmd = new NpgsqlCommand("""
                SELECT flow.audit(@actor, 'SELLER_LOYALTY_SETTINGS_UPDATED', 'seller_loyalty_settings', @sellerId,
                    jsonb_build_object('enabled', @enabled, 'earn_rate_percent', 1.00))
                """, conn, tx))
            {
                auditCmd.Parameters.AddWithValue("actor", actorId);
                auditCmd.Parameters.AddWithValue("sellerId", sellerId);
                auditCmd.Parameters.AddWithValue("enabled", request.Enabled);
                await auditCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            return Results.Ok(new SellerLoyaltySettingsResponse(sellerId, request.Enabled, 1.00m));
        })
        .RequireAuthorization("Seller", "SameSeller")
        .WithTags("Identity")
        .WithSummary("Bật/tắt chương trình tích điểm của shop (tỷ lệ cố định 1%, mặc định tắt)")
        .Produces<SellerLoyaltySettingsResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        // 3. GET /api/member/loyalty
        // Paged own shop balances for authenticated member; NO full customer directory for seller
        app.MapGet("/api/member/loyalty", async (
            int? page,
            int? pageSize,
            NpgsqlDataSource data,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var memberId))
                return Results.Unauthorized();

            var pageNumber = page ?? 1;
            var size = pageSize ?? 20;
            if (pageNumber < 1 || pageNumber > 100000 || size < 1 || size > 100)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Tham số phân trang không hợp lệ.");

            await using var conn = await data.OpenConnectionAsync(ct);

            // Recheck member active status in DB
            await using (var memberCheckCmd = new NpgsqlCommand("""
                SELECT id FROM identity.users
                WHERE id = @memberId AND role IN ('CUSTOMER', 'SELLER') AND status = 'ACTIVE'
                FOR SHARE
                """, conn))
            {
                memberCheckCmd.Parameters.AddWithValue("memberId", memberId);
                var valid = await memberCheckCmd.ExecuteScalarAsync(ct);
                if (valid is null) return Results.Unauthorized();
            }

            var offset = (pageNumber - 1) * size;
            var fetchCount = size + 1;

            var items = new List<ShopLoyaltyBalanceItem>();
            await using (var cmd = new NpgsqlCommand("""
                SELECT b.seller_id,
                       s.shop_name,
                       b.balance,
                       b.debt,
                       coalesce(ls.enabled, false) AS loyalty_enabled,
                       coalesce(ls.earn_rate_percent, 1.00) AS earn_rate_percent
                FROM identity.shop_loyalty_balances b
                JOIN identity.sellers s ON s.id = b.seller_id
                LEFT JOIN identity.seller_loyalty_settings ls ON ls.seller_id = b.seller_id
                WHERE b.customer_id = @memberId
                ORDER BY b.updated_at DESC, b.seller_id
                OFFSET @offset LIMIT @fetchCount
                """, conn))
            {
                cmd.Parameters.AddWithValue("memberId", memberId);
                cmd.Parameters.AddWithValue("offset", offset);
                cmd.Parameters.AddWithValue("fetchCount", fetchCount);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new ShopLoyaltyBalanceItem(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetBoolean(4),
                        reader.GetDecimal(5)));
                }
            }

            var hasMore = items.Count > size;
            if (hasMore)
            {
                items.RemoveAt(items.Count - 1);
            }

            return Results.Ok(new MemberLoyaltyPageResponse(items, pageNumber, size, hasMore));
        })
        .RequireAuthorization("MemberIdentity")
        .WithTags("Identity")
        .WithSummary("Xem danh sách số dư điểm của thành viên tại các shop")
        .Produces<MemberLoyaltyPageResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized);

        // 4. GET /api/member/loyalty/{sellerId}
        app.MapGet("/api/member/loyalty/{sellerId:guid}", async (
            Guid sellerId,
            NpgsqlDataSource data,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var memberId))
                return Results.Unauthorized();

            await using var conn = await data.OpenConnectionAsync(ct);

            // Recheck member active status in DB
            await using (var memberCheckCmd = new NpgsqlCommand("""
                SELECT id FROM identity.users
                WHERE id = @memberId AND role IN ('CUSTOMER', 'SELLER') AND status = 'ACTIVE'
                FOR SHARE
                """, conn))
            {
                memberCheckCmd.Parameters.AddWithValue("memberId", memberId);
                var valid = await memberCheckCmd.ExecuteScalarAsync(ct);
                if (valid is null) return Results.Unauthorized();
            }

            // Verify seller exists
            string? shopName = null;
            bool loyaltyEnabled = false;
            decimal earnRatePercent = 1.00m;

            await using (var sellerCmd = new NpgsqlCommand("""
                SELECT s.shop_name,
                       coalesce(ls.enabled, false),
                       coalesce(ls.earn_rate_percent, 1.00)
                FROM identity.sellers s
                LEFT JOIN identity.seller_loyalty_settings ls ON ls.seller_id = s.id
                WHERE s.id = @sellerId
                """, conn))
            {
                sellerCmd.Parameters.AddWithValue("sellerId", sellerId);
                await using var reader = await sellerCmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    shopName = reader.GetString(0);
                    loyaltyEnabled = reader.GetBoolean(1);
                    earnRatePercent = reader.GetDecimal(2);
                }
            }

            if (shopName is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Shop không tồn tại.");

            // Get balance and debt for member at this shop (default 0 if not yet earned/redeemed)
            long balance = 0;
            long debt = 0;

            await using (var balCmd = new NpgsqlCommand("""
                SELECT balance, debt
                FROM identity.shop_loyalty_balances
                WHERE customer_id = @memberId AND seller_id = @sellerId
                """, conn))
            {
                balCmd.Parameters.AddWithValue("memberId", memberId);
                balCmd.Parameters.AddWithValue("sellerId", sellerId);
                await using var reader = await balCmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    balance = reader.GetInt64(0);
                    debt = reader.GetInt64(1);
                }
            }

            return Results.Ok(new ShopLoyaltyBalanceItem(
                sellerId,
                shopName,
                balance,
                debt,
                loyaltyEnabled,
                earnRatePercent));
        })
        .RequireAuthorization("MemberIdentity")
        .WithTags("Identity")
        .WithSummary("Xem số dư điểm và thông tin tích điểm của thành viên tại một shop cụ thể")
        .Produces<ShopLoyaltyBalanceItem>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
