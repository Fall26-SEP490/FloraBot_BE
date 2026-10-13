using Google.Apis.Auth;
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
public sealed record RegistrationChallengeResponse(string ChallengeId, int ExpiresInSeconds, int ResendAfterSeconds, string Message);
public sealed record VerifyRegistrationRequest(string ChallengeId, string Code);
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
    private static IResult CooldownResult(HttpContext http, int retryAfterSeconds)
    {
        http.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        return Results.Problem(statusCode: 429, detail: $"Vui lòng đợi {retryAfterSeconds} giây trước khi yêu cầu mã xác thực mới.");
    }

    public static void MapMembers(this WebApplication app)
    {
        app.MapPost("/api/auth/register", async (MemberRegistration input, FloraDbContext db, RegistrationOtpService otp, HttpContext http, CancellationToken ct) =>
        {
            var email = input.Email?.Trim().ToLowerInvariant();
            if (!ValidName(input.FullName) || email is null || !EmailSettings.IsAddress(email) || !ValidPassword(input.Password))
                return Invalid("Nhập họ tên 2–100 ký tự, email hợp lệ và mật khẩu từ 12 ký tự, tối đa 72 byte UTF-8.");

            var exists = await db.Users.AnyAsync(x => x.Email == email, ct);
            if (exists)
                return Results.Problem(statusCode: 409, detail: "Không thể tạo tài khoản với email này. Hãy đăng nhập hoặc dùng chức năng quên mật khẩu.");

            var result = await otp.StartRegistrationAsync(input.FullName, email, input.Password, ct);
            return result switch
            {
                RegistrationStartResult.Success s => Results.Ok(new RegistrationChallengeResponse(s.ChallengeId, s.ExpiresInSeconds, s.ResendAfterSeconds, s.Message)),
                RegistrationStartResult.Cooldown c => CooldownResult(http, c.RetryAfterSeconds),
                RegistrationStartResult.Locked => Results.Problem(statusCode: 429, detail: "Tài khoản tạm thời bị khóa do nhập sai mã quá nhiều lần. Vui lòng thử lại sau 15 phút."),
                RegistrationStartResult.EmailUnavailable => Results.Problem(statusCode: 503, detail: "Dịch vụ xác thực email chưa sẵn sàng. Vui lòng thử lại sau."),
                _ => Results.Problem(statusCode: 500)
            };
        }).AllowAnonymous().RequireRateLimiting("auth").WithTags("Auth").Produces<RegistrationChallengeResponse>();

        app.MapPost("/api/auth/register/verify", async (VerifyRegistrationRequest input, FloraDbContext db, NpgsqlDataSource source, RegistrationOtpService otp, TokenService tokens, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.ChallengeId) || string.IsNullOrWhiteSpace(input.Code) || input.Code.Length != 6 || !input.Code.All(char.IsDigit))
                return Invalid("Mã xác thực không hợp lệ hoặc đã hết hạn.");

            var result = await otp.VerifyRegistrationAsync(input.ChallengeId, input.Code, ct);
            switch (result)
            {
                case RegistrationVerifyResult.Success s:
                    await using (var command = source.CreateCommand(
                        "INSERT INTO identity.users(id,email,password_hash,full_name,role,status,loyalty_points,created_at,updated_at) " +
                        "VALUES(@id,@email,@hash,@name,'CUSTOMER','ACTIVE',0,public.app_now(),public.app_now()) " +
                        "ON CONFLICT(email) DO NOTHING RETURNING id"))
                    {
                        var newUserId = Guid.CreateVersion7();
                        command.Parameters.AddWithValue("id", newUserId);
                        command.Parameters.AddWithValue("email", s.Email);
                        command.Parameters.AddWithValue("hash", s.PasswordHash);
                        command.Parameters.AddWithValue("name", s.FullName);
                        var createdId = await command.ExecuteScalarAsync(ct);
                        if (createdId is not Guid userId)
                            return Results.Problem(statusCode: 409, detail: "Không thể tạo tài khoản với email này. Hãy đăng nhập hoặc dùng chức năng quên mật khẩu.");

                        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId, ct);
                        return await AuthEndpoints.SignIn(user, db, tokens, http);
                    }
                case RegistrationVerifyResult.WrongCode:
                    return Invalid("Mã xác thực không chính xác.");
                case RegistrationVerifyResult.Locked:
                    return Results.Problem(statusCode: 429, detail: "Tài khoản tạm thời bị khóa do nhập sai mã quá nhiều lần. Vui lòng thử lại sau 15 phút.");
                case RegistrationVerifyResult.InvalidOrExpired:
                default:
                    return Invalid("Mã xác thực không hợp lệ hoặc đã hết hạn.");
            }
        }).AllowAnonymous().RequireRateLimiting("auth").WithTags("Auth").Produces<SessionResponse>();

        app.MapPost("/api/member/google/link", async (GoogleAuthRequest request, FloraDbContext db, NpgsqlDataSource source, IGoogleIdentityValidator googleValidator, IConfiguration config, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.IdToken) || request.IdToken.Length < 10 || request.IdToken.Length > 4096)
                return Results.BadRequest();

            var clientId = config["GOOGLE_CLIENT_ID"] ?? config["Authentication:Google:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId))
                return Results.Problem(statusCode: 503, detail: "Google Auth chưa sẵn sàng.");

            GoogleJsonWebSignature.Payload payload;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                payload = await googleValidator.ValidateAsync(request.IdToken, clientId, deadline.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidJwtException)
            {
                return Results.Unauthorized();
            }
            catch (Exception)
            {
                return Results.Problem(statusCode: 503, detail: "Dịch vụ xác thực Google tạm thời không khả dụng. Vui lòng thử lại sau.");
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.Subject) || payload.Subject.Length > 255 || payload.Subject.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(payload.Email) || payload.Email.Length > 254 || !EmailSettings.IsAddress(payload.Email) || !payload.EmailVerified)
            {
                return Results.Problem(statusCode: 400, detail: "Email xác thực từ Google là bắt buộc.");
            }

            var userId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
            if (user is null || user.Status != "ACTIVE" || (user.Role != "CUSTOMER" && user.Role != "SELLER"))
                return Results.Unauthorized();

            var googleEmail = payload.Email.Trim().ToLowerInvariant();
            var userEmail = user.Email?.Trim().ToLowerInvariant();
            if (userEmail is null || !string.Equals(userEmail, googleEmail, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(statusCode: 400, detail: "Email tài khoản Google không khớp với email tài khoản của bạn.");
            }

            var subject = payload.Subject.Trim();
            await using var checkCmd = source.CreateCommand(
                "SELECT user_id FROM identity.external_identities WHERE provider = 'google' AND subject = @sub");
            checkCmd.Parameters.AddWithValue("sub", subject);
            var existingLinkedUser = (Guid?)await checkCmd.ExecuteScalarAsync(ct);
            if (existingLinkedUser is not null && existingLinkedUser.Value != userId)
            {
                return Results.Problem(statusCode: 409, detail: "Tài khoản Google này đã được liên kết với một tài khoản khác.");
            }

            await using var userCheckCmd = source.CreateCommand(
                "SELECT subject FROM identity.external_identities WHERE user_id = @userId AND provider = 'google'");
            userCheckCmd.Parameters.AddWithValue("userId", userId);
            var existingUserSubject = (string?)await userCheckCmd.ExecuteScalarAsync(ct);
            if (existingUserSubject is not null)
            {
                if (existingUserSubject == subject)
                    return Results.Ok(new { message = "Tài khoản Google đã được liên kết." });
                return Results.Problem(statusCode: 409, detail: "Tài khoản của bạn đã được liên kết với một tài khoản Google khác.");
            }

            await using var insertCmd = source.CreateCommand(
                "INSERT INTO identity.external_identities(user_id, provider, subject, email, created_at, updated_at) " +
                "VALUES(@userId, 'google', @sub, @email, public.app_now(), public.app_now()) " +
                "ON CONFLICT(provider, subject) DO NOTHING");
            insertCmd.Parameters.AddWithValue("userId", userId);
            insertCmd.Parameters.AddWithValue("sub", subject);
            insertCmd.Parameters.AddWithValue("email", googleEmail);
            var rows = await insertCmd.ExecuteNonQueryAsync(ct);
            if (rows == 0)
            {
                return Results.Problem(statusCode: 409, detail: "Tài khoản Google này đã được liên kết với một tài khoản khác.");
            }
            return Results.Ok(new { message = "Liên kết tài khoản Google thành công." });
        }).RequireAuthorization("MemberIdentity").RequireRateLimiting("auth").WithTags("Auth");

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

        app.MapPost("/api/auth/forgot-password", async (PasswordResetRequest input, FloraDbContext db, EmailSettings settings, ITransactionalEmailSender sender, IDataProtectionProvider protection, IConfiguration config, IHostEnvironment environment, CancellationToken ct) =>
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
                var subject = "Đặt lại mật khẩu FloraBot";
                var text = $"Mở liên kết để đặt lại mật khẩu trong 20 phút: {link}\nNếu bạn không yêu cầu, hãy bỏ qua email này.";
                var html = EmailTemplates.PasswordReset(link, user.FullName);
                try { await sender.SendAsync(new(email, subject, text, html), ct); }
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
            if (user is null || TokenService.CredentialVersion(user) != ticket.Version) return Invalid("Liên kết không hợp lệ hoặc đã được dùng.");
            return await ReplacePassword(source, user.Id, user.PasswordHash, input.NewPassword, ct);
        }).AllowAnonymous().RequireRateLimiting("auth");
    }

    private static async Task<IResult> ReplacePassword(NpgsqlDataSource source, Guid id, string? oldHash, string password, CancellationToken ct)
    {
        await using var command = source.CreateCommand("UPDATE identity.users SET password_hash=@hash WHERE id=@id AND role IN ('CUSTOMER','SELLER') AND status='ACTIVE' AND password_hash IS NOT DISTINCT FROM @old");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("old", (object?)oldHash ?? DBNull.Value);
        command.Parameters.AddWithValue("hash", BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12));
        return await command.ExecuteNonQueryAsync(ct) == 1 ? Results.NoContent() : Results.Conflict();
    }
}
