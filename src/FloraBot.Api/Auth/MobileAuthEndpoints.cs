using System.Text;
using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Auth;

public sealed record MobileLoginRequest(string Email, string Password, string Role);
public sealed record MobileRefreshRequest(string RefreshToken);
public sealed record MobileSessionResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, SessionResponse User);

public static class MobileAuthPolicy
{
    public static bool AllowedRole(string? role) => role is "TECHNICIAN" or "SELLER_STAFF" or "SELLER" or "CUSTOMER";
    public static bool ValidLogin(MobileLoginRequest input) => AllowedRole(input.Role)
        && !string.IsNullOrWhiteSpace(input.Email) && input.Email.Length <= 254
        && !string.IsNullOrEmpty(input.Password) && Encoding.UTF8.GetByteCount(input.Password) <= 72;
    public static bool ValidRefresh(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

public static class MobileAuthEndpoints
{
    public static void MapMobileAuth(this WebApplication app)
    {
        var group = app.MapGroup("/api/mobile/auth").WithTags("MobileAuth").RequireRateLimiting("auth");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });
        group.MapPost("/login", async (MobileLoginRequest input, FloraDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            if (!MobileAuthPolicy.ValidLogin(input)) return Results.BadRequest();
            var email = input.Email.Trim().ToLowerInvariant();
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email && x.Status == "ACTIVE" && x.Role == input.Role, ct);
            var valid = false;
            try { valid = BCrypt.Net.BCrypt.Verify(input.Password, user?.PasswordHash ?? "$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy"); }
            catch (BCrypt.Net.SaltParseException) { }
            if (!valid || user is null) return Results.Unauthorized();
            return await IssueAsync(user, db, tokens, ct);
        }).AllowAnonymous().Produces<MobileSessionResponse>().Produces(401).Produces(400);
        group.MapPost("/refresh", async (MobileRefreshRequest input, FloraDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            if (!MobileAuthPolicy.ValidRefresh(input.RefreshToken)) return Results.Unauthorized();
            var consumed = await tokens.ConsumeMobileRefresh(input.RefreshToken);
            if (consumed is null) return Results.Unauthorized();
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == consumed.Value.Id && x.Status == "ACTIVE", ct);
            if (user is null || !MobileAuthPolicy.AllowedRole(user.Role)
                || TokenService.CredentialVersion(user) != consumed.Value.Version) return Results.Unauthorized();
            return await IssueAsync(user, db, tokens, ct);
        }).AllowAnonymous().Produces<MobileSessionResponse>().Produces(401);
        group.MapPost("/logout", async (MobileRefreshRequest input, TokenService tokens) =>
        {
            if (MobileAuthPolicy.ValidRefresh(input.RefreshToken)) await tokens.ConsumeMobileRefresh(input.RefreshToken);
            return Results.NoContent();
        }).AllowAnonymous().Produces(204);
        group.MapPost("/password", async (PasswordChange input, HttpContext http, FloraDbContext db, NpgsqlDataSource data, CancellationToken ct) =>
        {
            if (!MemberEndpoints.ValidPassword(input.NewPassword) || string.IsNullOrEmpty(input.CurrentPassword)
                || Encoding.UTF8.GetByteCount(input.CurrentPassword) > 72) return Results.BadRequest();
            var id = Guid.Parse(http.User.FindFirstValue("sub")!);
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Status == "ACTIVE", ct);
            if (user is null || !MobileAuthPolicy.AllowedRole(user.Role)) return Results.Forbid();
            var valid = false;
            try { valid = user.PasswordHash is not null && BCrypt.Net.BCrypt.Verify(input.CurrentPassword, user.PasswordHash); }
            catch (BCrypt.Net.SaltParseException) { }
            if (!valid) return Results.Problem(statusCode: 400, detail: "Mật khẩu hiện tại chưa đúng.");
            // Compare-and-swap preserves concurrent credential changes. The credential
            // version check on every access/refresh revokes all older sessions.
            await using var command = data.CreateCommand("""
                UPDATE identity.users SET password_hash=@hash
                WHERE id=@id AND status='ACTIVE' AND role=@role AND password_hash=@old
                """);
            command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("role", user.Role);
            command.Parameters.AddWithValue("old", user.PasswordHash!);
            command.Parameters.AddWithValue("hash", BCrypt.Net.BCrypt.HashPassword(input.NewPassword, workFactor: 12));
            return await command.ExecuteNonQueryAsync(ct) == 1 ? Results.NoContent() : Results.Conflict();
        }).RequireAuthorization("Account").Produces(204).Produces(400).Produces(409);
    }

    private static async Task<IResult> IssueAsync(User user, FloraDbContext db, TokenService tokens, CancellationToken ct)
    {
        if (!MobileAuthPolicy.AllowedRole(user.Role)) return Results.Unauthorized();
        var seller = user.SellerId is null ? null : await db.Sellers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == user.SellerId, ct);
        if (user.Role is "SELLER" or "SELLER_STAFF" && seller is null) return Results.Unauthorized();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(user.Role == "CUSTOMER" ? 10 : 15);
        var access = tokens.Issue(user, seller?.Status, expires: expiry.UtcDateTime, bindCredentialVersion: true);
        return Results.Ok(new MobileSessionResponse(access, await tokens.CreateMobileRefresh(user), expiry,
            new(user.Id, user.FullName, user.Role, user.SellerId, seller?.Status, seller?.PackageExpiresAt)));
    }
}
