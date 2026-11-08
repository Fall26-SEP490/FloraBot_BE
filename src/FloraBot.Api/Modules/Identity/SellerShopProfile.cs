using System.Security.Claims;
using System.Text.RegularExpressions;
using FloraBot.Api.Modules.Notify;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Identity;

public sealed record SellerShopProfileResponse(Guid Id, string ShopName, string Phone,
    string? ContactEmail, string? Address, string Status);
public sealed record UpdateSellerShopProfileRequest(string? ShopName, string? Phone,
    string? ContactEmail);

public static class SellerShopProfile
{
    public static void MapSellerShopProfile(this WebApplication app)
    {
        app.MapGet("/api/me/shop", async (NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Claims(http, out var actor, out var sellerId)) return Results.Forbid();
            await using var conn = await data.OpenConnectionAsync(ct);
            if (!await CanManageShopProfile(conn, null, actor, sellerId, ct)) return Results.Forbid();
            return Results.Ok(await Read(conn, null, sellerId, ct));
        }).RequireAuthorization("Seller").WithTags("Identity")
          .WithSummary("Seller xem hồ sơ shop, kể cả khi kỳ thuê đã hết hạn")
          .WithDescription("Chỉ chủ shop có tài khoản ACTIVE và Seller ACTIVE hoặc PAST_DUE. Hiệu lực thuê kiosk được kiểm tra riêng ở các thao tác bán hàng.")
          .Produces<SellerShopProfileResponse>();

        app.MapPatch("/api/me/shop", async (UpdateSellerShopProfileRequest input,
            NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Claims(http, out var actor, out var sellerId)) return Results.Forbid();
            if (input.ShopName is null && input.Phone is null && input.ContactEmail is null)
                return Results.Problem(statusCode: 400, detail: "Cần cung cấp thông tin cần sửa.");
            var name = input.ShopName?.Trim();
            var phone = input.Phone?.Trim();
            var email = input.ContactEmail?.Trim().ToLowerInvariant();
            if (name is not null && (name.Length is < 2 or > 120 || name.Any(char.IsControl)) ||
                phone is not null && !Regex.IsMatch(phone, @"^0[35789][0-9]{8}$") ||
                email is not null && !EmailSettings.IsAddress(email))
                return Results.Problem(statusCode: 400, detail: "Tên shop, số điện thoại hoặc email không hợp lệ.");

            await using var conn = await data.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            if (!await CanManageShopProfile(conn, tx, actor, sellerId, ct)) return Results.Forbid();
            if (email is not null)
            {
                await using var verified = new NpgsqlCommand("""
                    SELECT 1 FROM identity.user_profiles
                    WHERE user_id=@actor AND lower(contact_email)=@email
                      AND contact_email_verified_at IS NOT NULL FOR SHARE
                    """, conn, tx);
                verified.Parameters.AddWithValue("actor", actor);
                verified.Parameters.AddWithValue("email", email);
                if (await verified.ExecuteScalarAsync(ct) is null)
                    return Results.Problem(statusCode: 409,
                        detail: "Hãy xác minh email qua /api/account/contact-email trước khi dùng làm email shop.");
            }
            await using (var update = new NpgsqlCommand("""
                UPDATE identity.sellers SET shop_name=COALESCE(@name,shop_name),
                  phone=COALESCE(@phone,phone),contact_email=COALESCE(@email,contact_email),
                  updated_at=public.app_now() WHERE id=@seller
                """, conn, tx))
            {
                update.Parameters.AddWithValue("seller", sellerId);
                update.Parameters.Add("name", NpgsqlDbType.Text).Value=(object?)name ?? DBNull.Value;
                update.Parameters.Add("phone", NpgsqlDbType.Text).Value=(object?)phone ?? DBNull.Value;
                update.Parameters.Add("email", NpgsqlDbType.Text).Value=(object?)email ?? DBNull.Value;
                await update.ExecuteNonQueryAsync(ct);
            }
            await using (var audit = new NpgsqlCommand("""
                SELECT flow.audit(@actor,'SELLER_SHOP_PROFILE_UPDATED','sellers',@seller,
                  jsonb_build_object('name_changed',@name,'phone_changed',@phone,'email_changed',@email))
                """, conn, tx))
            {
                audit.Parameters.AddWithValue("actor", actor);
                audit.Parameters.AddWithValue("seller", sellerId);
                audit.Parameters.AddWithValue("name", name is not null);
                audit.Parameters.AddWithValue("phone", phone is not null);
                audit.Parameters.AddWithValue("email", email is not null);
                await audit.ExecuteNonQueryAsync(ct);
            }
            var result = await Read(conn, tx, sellerId, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(result);
        }).RequireAuthorization("Seller").WithTags("Identity")
          .WithSummary("Seller cập nhật thông tin chung của shop, kể cả khi kỳ thuê đã hết hạn")
          .WithDescription("Chỉ chủ shop có tài khoản ACTIVE và Seller ACTIVE hoặc PAST_DUE. Email liên hệ mới vẫn cần được xác minh; ngân hàng shop cập nhật qua API set_seller_bank hiện có.")
          .Produces<SellerShopProfileResponse>().ProducesProblem(400)
          .ProducesProblem(409);
    }

    private static bool Claims(HttpContext http, out Guid actor, out Guid sellerId)
    {
        var hasActor = Guid.TryParse(http.User.FindFirstValue("sub"), out actor);
        var hasSeller = Guid.TryParse(http.User.FindFirstValue("seller_id"), out sellerId);
        return hasActor && hasSeller;
    }

    private static async Task<bool> CanManageShopProfile(NpgsqlConnection conn, NpgsqlTransaction? tx,
        Guid actor, Guid sellerId, CancellationToken ct)
    {
        // An expired lease must not prevent its owner from maintaining the shared shop
        // profile before renting or renewing. Commerce still checks the kiosk's lease.
        await using var cmd = new NpgsqlCommand("""
            SELECT 1 FROM identity.users u JOIN identity.sellers s ON s.id=u.seller_id
            WHERE u.id=@actor AND u.role='SELLER' AND u.status='ACTIVE'
              AND s.id=@seller AND s.status IN ('ACTIVE','PAST_DUE') FOR SHARE OF u,s
            """, conn, tx);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("seller", sellerId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<SellerShopProfileResponse?> Read(NpgsqlConnection conn,
        NpgsqlTransaction? tx, Guid sellerId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id,shop_name,phone,contact_email,address,status
            FROM identity.sellers WHERE id=@seller
            """, conn, tx);
        cmd.Parameters.AddWithValue("seller", sellerId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetGuid(0),r.GetString(1),r.GetString(2),
            r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),
            r.GetString(5)) : null;
    }
}
