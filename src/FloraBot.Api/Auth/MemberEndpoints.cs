using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Data;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Auth;

public sealed record MemberRegistration(string FullName, string Email, string Password);
public sealed record MemberProfile(Guid Id, string FullName, string? Email, long LoyaltyPoints);
public sealed record MemberProfileUpdate(string FullName);
public sealed record PasswordChange(string CurrentPassword, string NewPassword);
public sealed record PasswordResetRequest(string Email);
public sealed record PasswordReset(string Token, string NewPassword);
internal sealed record ResetTicket(Guid UserId, string Version, DateTimeOffset ExpiresAt);

public static class MemberEndpoints
{
    internal static bool ValidPassword(string? value) => value is { Length: >= 12 } && Encoding.UTF8.GetByteCount(value) <= 72 && !value.Any(char.IsControl);
    private static bool ValidName(string? value) => value?.Trim() is { Length: >= 2 and <= 100 } name && !name.Any(char.IsControl);
    private static IResult Invalid(string message) => Results.Problem(statusCode: 400, detail: message);
    public static void MapMembers(this WebApplication app)
    {
        app.MapPost("/api/auth/register", async (MemberRegistration input, FloraDbContext db, NpgsqlDataSource source, TokenService tokens, HttpContext http, CancellationToken ct) =>
        {
            var email = input.Email?.Trim().ToLowerInvariant();
            if (!ValidName(input.FullName) || email is null || !EmailSettings.IsAddress(email) || !ValidPassword(input.Password))
                return Invalid("Nhập họ tên 2–100 ký tự, email hợp lệ và mật khẩu từ 12 ký tự, tối đa 72 byte UTF-8.");
            await using var command = source.CreateCommand("INSERT INTO identity.users(email,password_hash,full_name,role) VALUES(@email,@hash,@name,'CUSTOMER') ON CONFLICT(email) DO NOTHING RETURNING id");
            command.Parameters.AddWithValue("email", email); command.Parameters.AddWithValue("name", input.FullName.Trim());
            command.Parameters.AddWithValue("hash", BCrypt.Net.BCrypt.HashPassword(input.Password, workFactor: 12));
            var id = await command.ExecuteScalarAsync(ct);
            if (id is not Guid userId) return Results.Problem(statusCode: 409, detail: "Không thể tạo tài khoản với email này. Hãy đăng nhập hoặc dùng chức năng quên mật khẩu.");
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId, ct);
            return await AuthEndpoints.SignIn(user, db, tokens, http);
        }).AllowAnonymous().RequireRateLimiting("auth").Produces<SessionResponse>();

        app.MapGet("/api/member/profile", async (FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var id = Guid.Parse(http.User.FindFirstValue("sub")!);
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            return Results.Ok(new MemberProfile(id, user.FullName, user.Email, user.LoyaltyPoints));
        }).RequireAuthorization("MemberIdentity").Produces<MemberProfile>();

        app.MapPost("/api/member/profile", async (MemberProfileUpdate input, NpgsqlDataSource source, HttpContext http, CancellationToken ct) =>
        {
            if (!ValidName(input.FullName)) return Invalid("Họ tên cần từ 2 đến 100 ký tự.");
            await using var command = source.CreateCommand("UPDATE identity.users SET full_name=@name WHERE id=@id AND role IN ('CUSTOMER','SELLER') AND status='ACTIVE'");
            command.Parameters.AddWithValue("id", Guid.Parse(http.User.FindFirstValue("sub")!)); command.Parameters.AddWithValue("name", input.FullName.Trim());
            return await command.ExecuteNonQueryAsync(ct) == 1 ? Results.NoContent() : Results.Unauthorized();
        }).RequireAuthorization("MemberIdentity");

        app.MapPost("/api/member/password", async (PasswordChange input, FloraDbContext db, NpgsqlDataSource source, HttpContext http, CancellationToken ct) =>
        {
            if (!ValidPassword(input.NewPassword) || input.CurrentPassword is null || Encoding.UTF8.GetByteCount(input.CurrentPassword) > 72) return Invalid("Mật khẩu mới cần từ 12 ký tự, tối đa 72 byte UTF-8.");
            var id = Guid.Parse(http.User.FindFirstValue("sub")!);
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var valid = false;
            try { valid = user.PasswordHash is not null && BCrypt.Net.BCrypt.Verify(input.CurrentPassword, user.PasswordHash); } catch (BCrypt.Net.SaltParseException) { }
            if (!valid) return Invalid("Mật khẩu hiện tại chưa đúng.");
            return await ReplacePassword(source, id, user.PasswordHash!, input.NewPassword, ct);
        }).RequireAuthorization("MemberIdentity").RequireRateLimiting("auth");

        app.MapPost("/api/auth/forgot-password", async (PasswordResetRequest input, FloraDbContext db, EmailSettings settings, TransactionalEmailSender sender, IDataProtectionProvider protection, IConfiguration config, IHostEnvironment environment, CancellationToken ct) =>
        {
            var email = input.Email?.Trim().ToLowerInvariant();
            if (email is null || !EmailSettings.IsAddress(email)) return Invalid("Nhập email hợp lệ.");
            var origin = config["MEMBER_WEB_URL"] ?? (environment.IsDevelopment() ? "http://localhost:8088" : "");
            if (settings.Provider == EmailProvider.Disabled || !Uri.TryCreate(origin, UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != "https" && !(environment.IsDevelopment() && baseUri.IsLoopback)) || !string.IsNullOrEmpty(baseUri.UserInfo))
                return Results.Problem(statusCode: 503, detail: "Dịch vụ khôi phục mật khẩu chưa sẵn sàng. Vui lòng liên hệ đội ngũ FloraBot.");
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email && (x.Role == "CUSTOMER" || x.Role == "SELLER") && x.Status == "ACTIVE", ct);
            if (user is not null)
            {
                var ticket = protection.CreateProtector("member-password-reset-v1").Protect(JsonSerializer.Serialize(new ResetTicket(user.Id, TokenService.CredentialVersion(user), DateTimeOffset.UtcNow.AddMinutes(20))));
                var link = new Uri(baseUri, "/dat-lai-mat-khau").AbsoluteUri + "#token=" + Uri.EscapeDataString(ticket);
                try { await sender.SendAsync(new(email, "Đặt lại mật khẩu FloraBot", $"Mở liên kết để đặt lại mật khẩu trong 20 phút: {link}\nNếu bạn không yêu cầu, hãy bỏ qua email này."), ct); }
                catch (EmailUnavailableException) { return Results.Problem(statusCode: 503, detail: "Chưa xác nhận được email đã gửi. Hãy thử lại sau."); }
            }
            return Results.Ok(new { message = "Nếu email thuộc tài khoản thành viên, bạn sẽ nhận được liên kết đặt lại mật khẩu." });
        }).AllowAnonymous().RequireRateLimiting("auth");

        app.MapPost("/api/auth/reset-password", async (PasswordReset input, IDataProtectionProvider protection, FloraDbContext db, NpgsqlDataSource source, CancellationToken ct) =>
        {
            if (!ValidPassword(input.NewPassword) || input.Token is null || input.Token.Length > 4096) return Invalid("Mật khẩu hoặc liên kết không hợp lệ.");
            ResetTicket? ticket;
            try { ticket = JsonSerializer.Deserialize<ResetTicket>(protection.CreateProtector("member-password-reset-v1").Unprotect(input.Token)); }
            catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return Invalid("Liên kết không hợp lệ hoặc đã hết hạn."); }
            if (ticket is null || ticket.ExpiresAt <= DateTimeOffset.UtcNow) return Invalid("Liên kết không hợp lệ hoặc đã hết hạn.");
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ticket.UserId && (x.Role == "CUSTOMER" || x.Role == "SELLER") && x.Status == "ACTIVE", ct);
            if (user?.PasswordHash is null || TokenService.CredentialVersion(user) != ticket.Version) return Invalid("Liên kết không hợp lệ hoặc đã được dùng.");
            return await ReplacePassword(source, user.Id, user.PasswordHash, input.NewPassword, ct);
        }).AllowAnonymous().RequireRateLimiting("auth");
    }

    private static async Task<IResult> ReplacePassword(NpgsqlDataSource source, Guid id, string oldHash, string password, CancellationToken ct)
    {
        await using var command = source.CreateCommand("UPDATE identity.users SET password_hash=@hash WHERE id=@id AND role IN ('CUSTOMER','SELLER') AND status='ACTIVE' AND password_hash=@old");
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("old", oldHash);
        command.Parameters.AddWithValue("hash", BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12));
        return await command.ExecuteNonQueryAsync(ct) == 1 ? Results.NoContent() : Results.Conflict();
    }
}
