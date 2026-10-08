using System.Text.RegularExpressions;
using System.Security.Claims;
using FloraBot.Api.Auth;
using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using FloraBot.Api.Realtime;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Identity;

public sealed record RegisterSellerRequest(string ShopName, string Phone, string Area, Guid PackageId, string? Website = null);
public sealed record RegistrationResponse(Guid Id, string Status);
public sealed record OpenMemberShopRequest(string ShopName, string Phone, string Address);

public static partial class Registration
{
    [GeneratedRegex("^(0[35789][0-9]{8}|\\+84[35789][0-9]{8})$")]
    private static partial Regex PhonePattern();
    public static void MapRegistration(this WebApplication app)
    {
        app.MapPost("/api/member/shop", async (OpenMemberShopRequest input, HttpContext http, NpgsqlDataSource data, FloraDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.ShopName) || input.ShopName.Trim().Length is < 2 or > 120 || input.ShopName.Any(char.IsControl) ||
                input.Phone is null || !PhonePattern().IsMatch(input.Phone) || string.IsNullOrWhiteSpace(input.Address) || input.Address.Trim().Length > 200 || input.Address.Any(char.IsControl))
                return Results.Problem(statusCode: 400, detail: "Nhập tên shop 2–120 ký tự, số điện thoại Việt Nam và địa chỉ tối đa 200 ký tự.");
            var userId = Guid.Parse(http.User.FindFirstValue("sub")!);
            await using var command = data.CreateCommand("SELECT flow.open_member_shop(@user,@shop,@phone,@address)");
            command.Parameters.AddWithValue("user", userId);
            command.Parameters.AddWithValue("shop", input.ShopName.Trim());
            command.Parameters.AddWithValue("phone", input.Phone);
            command.Parameters.AddWithValue("address", input.Address.Trim());
            try { await command.ExecuteScalarAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == "42501") { return Results.Forbid(); }
            catch (PostgresException ex) when (ex.SqlState == "22023") { return Results.BadRequest(); }
            catch (PostgresException ex) when (ex.SqlState == "23505") { return Results.Problem(statusCode: 409, detail: "Thông tin shop đã được sử dụng. Kiểm tra lại trước khi gửi."); }
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId, ct);
            return await AuthEndpoints.SignIn(user, db, tokens, http);
        }).RequireAuthorization("MemberIdentity").RequireRateLimiting("auth").WithTags("Identity").Produces<SessionResponse>();

        app.MapPost("/api/sellers", async (RegisterSellerRequest request, NpgsqlDataSource data, PortalNotifier notifier, CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(request.Website)) return Results.BadRequest();
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.ShopName) || request.ShopName.Trim().Length is < 2 or > 120) errors["shopName"] = ["Nhập tên shop từ 2 đến 120 ký tự."];
            if (request.Phone is null || !PhonePattern().IsMatch(request.Phone)) errors["phone"] = ["Nhập số điện thoại Việt Nam hợp lệ."];
            if (string.IsNullOrWhiteSpace(request.Area) || request.Area.Length > 200) errors["area"] = ["Nhập khu vực của shop, tối đa 200 ký tự."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            await using var connection = await data.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var package = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM identity.subscription_packages WHERE id=@id AND status='ACTIVE')", connection, transaction);
            package.Parameters.AddWithValue("id", request.PackageId);
            if (await package.ExecuteScalarAsync(ct) is not true) return Results.ValidationProblem(new Dictionary<string, string[]> { ["packageId"] = ["Chọn một gói đang được cung cấp."] });
            await using var command = new NpgsqlCommand("SELECT flow.register_seller(@shop,@phone,@area,@shop,NULL)", connection, transaction);
            command.Parameters.AddWithValue("shop", request.ShopName!.Trim());
            command.Parameters.AddWithValue("phone", request.Phone!);
            command.Parameters.AddWithValue("area", request.Area!);
            var id = (Guid)(await command.ExecuteScalarAsync(ct))!;
            await using var audit = new NpgsqlCommand("SELECT flow.audit(NULL,'PACKAGE_INTEREST','sellers',@id,jsonb_build_object('package_id',@package))", connection, transaction);
            audit.Parameters.AddWithValue("id", id);
            audit.Parameters.AddWithValue("package", request.PackageId);
            await audit.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            // Notification authorization must not need a second connection while this one is idle.
            await connection.CloseAsync();
            await notifier.RefreshAsync(null);
            return Results.Created($"/api/sellers/{id}", new RegistrationResponse(id, "PENDING"));
        }).AllowAnonymous().RequireRateLimiting("auth").WithTags("Identity").Produces<RegistrationResponse>(201);
    }
}
