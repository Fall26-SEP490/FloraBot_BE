using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Identity;

public sealed record EditSellerStaffProfile(string FullName, string? Phone, string? Address);
public sealed record SellerStaffProfile(Guid Id, Guid SellerId, string Email, string FullName,
    string? Phone, string? Address, string Status);

public static class SellerStaffProfiles
{
    public static void MapSellerStaffProfiles(this WebApplication app)
    {
        app.MapGet("/api/sellers/{sellerId:guid}/staff/{staffId:guid}", async (
            Guid sellerId, Guid staffId, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
            await using var conn = await data.OpenConnectionAsync(ct);
            if (!await ActiveSeller(conn, null, actor, sellerId, ct)) return Results.Forbid();
            var profile = await Read(conn, null, sellerId, staffId, ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        }).RequireAuthorization("Seller", "SameSeller").WithTags("Identity").Produces<SellerStaffProfile>();

        app.MapPut("/api/sellers/{sellerId:guid}/staff/{staffId:guid}", async (
            Guid sellerId, Guid staffId, EditSellerStaffProfile input,
            NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
            var name = input.FullName?.Trim();
            var phone = string.IsNullOrWhiteSpace(input.Phone) ? null : AccountHierarchyEndpoints.NormalizePhone(input.Phone);
            var address = string.IsNullOrWhiteSpace(input.Address) ? null : input.Address.Trim();
            if (name is null || name.Length is < 2 or > 120 || name.Any(char.IsControl) ||
                (input.Phone is not null && !string.IsNullOrWhiteSpace(input.Phone) &&
                 !AccountHierarchyEndpoints.PhonePattern().IsMatch(input.Phone.Trim())) ||
                address?.Length > 300 || address?.Any(char.IsControl) == true)
                return Results.BadRequest();
            await using var conn = await data.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            if (!await ActiveSeller(conn, tx, actor, sellerId, ct)) return Results.Forbid();
            await using (var lockStaff = new NpgsqlCommand("SELECT 1 FROM identity.users WHERE id=@id AND role='SELLER_STAFF' AND seller_id=@seller FOR UPDATE", conn, tx))
            {
                lockStaff.Parameters.AddWithValue("id", staffId);
                lockStaff.Parameters.AddWithValue("seller", sellerId);
                if (await lockStaff.ExecuteScalarAsync(ct) is null) return Results.NotFound();
            }
            try
            {
                await using var update = new NpgsqlCommand("UPDATE identity.users SET full_name=@name,phone=@phone,updated_at=public.app_now() WHERE id=@id", conn, tx);
                update.Parameters.AddWithValue("id", staffId);
                update.Parameters.AddWithValue("name", name);
                update.Parameters.Add("phone", NpgsqlDbType.Text).Value = (object?)phone ?? DBNull.Value;
                await update.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            { return Results.Problem(statusCode: 409, detail: "Số điện thoại đã được sử dụng."); }
            await using (var upsert = new NpgsqlCommand("""
                INSERT INTO identity.user_profiles(user_id,address)
                VALUES(@id,@address)
                ON CONFLICT (user_id) DO UPDATE SET address=EXCLUDED.address,updated_at=public.app_now()
                """, conn, tx))
            {
                upsert.Parameters.AddWithValue("id", staffId);
                upsert.Parameters.Add("address", NpgsqlDbType.Text).Value = (object?)address ?? DBNull.Value;
                await upsert.ExecuteNonQueryAsync(ct);
            }
            await using (var audit = new NpgsqlCommand("SELECT flow.audit(@actor,'SELLER_STAFF_PROFILE_UPDATED','users',@staff)", conn, tx))
            {
                audit.Parameters.AddWithValue("actor", actor);
                audit.Parameters.AddWithValue("staff", staffId);
                await audit.ExecuteNonQueryAsync(ct);
            }
            var profile = await Read(conn, tx, sellerId, staffId, ct);
            await tx.CommitAsync(ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        }).RequireAuthorization("Seller", "SameSeller").WithTags("Identity").Produces<SellerStaffProfile>();
    }

    private static async Task<bool> ActiveSeller(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid actor, Guid seller, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT 1 FROM identity.users u JOIN identity.sellers s ON s.id=u.seller_id
            WHERE u.id=@actor AND u.role='SELLER' AND u.status='ACTIVE'
              AND s.id=@seller AND s.status='ACTIVE'
            """, conn, tx);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("seller", seller);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<SellerStaffProfile?> Read(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid seller, Guid staff, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT u.id,u.seller_id,u.email,u.full_name,u.phone,p.address,u.status
            FROM identity.users u LEFT JOIN identity.user_profiles p ON p.user_id=u.id
            WHERE u.id=@staff AND u.seller_id=@seller AND u.role='SELLER_STAFF'
            """, conn, tx);
        cmd.Parameters.AddWithValue("staff", staff);
        cmd.Parameters.AddWithValue("seller", seller);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? "" : reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6));
    }
}
