using Google.Apis.Auth;
using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Notify;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Auth;

public sealed record LoginRequest(string Email, string Password);
public sealed record GoogleAuthRequest(string IdToken);
public sealed record SessionResponse(Guid Id, string Name, string Role, Guid? SellerId, string? SellerStatus, DateOnly? PackageExpiresAt);
public sealed record AuthConfigResponse(string? GoogleClientId, bool GoogleEnabled, bool RegistrationEmailEnabled);

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        app.MapGet("/api/auth/config", (IConfiguration config, EmailSettings emailSettings) =>
        {
            var clientId = config["GOOGLE_CLIENT_ID"] ?? config["Authentication:Google:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId)) clientId = null;
            var googleEnabled = !string.IsNullOrWhiteSpace(clientId);
            var registrationEmailEnabled = emailSettings.Provider != EmailProvider.Disabled;
            return Results.Ok(new AuthConfigResponse(clientId, googleEnabled, registrationEmailEnabled));
        }).AllowAnonymous().WithTags("Auth").Produces<AuthConfigResponse>();

        app.MapPost("/api/auth/google", async (GoogleAuthRequest request, FloraDbContext db, NpgsqlDataSource source, TokenService tokens, IGoogleIdentityValidator googleValidator, IConfiguration config, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.IdToken) || request.IdToken.Length < 10 || request.IdToken.Length > 4096)
                return Results.BadRequest();

            var clientId = config["GOOGLE_CLIENT_ID"] ?? config["Authentication:Google:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return Results.Problem(statusCode: 503, detail: "Google Auth chưa sẵn sàng.");
            }

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
                string.IsNullOrWhiteSpace(payload.Email) || payload.Email.Length > 254 || !EmailSettings.IsAddress(payload.Email) || !payload.EmailVerified ||
                (payload.Name is not null && (payload.Name.Length > 100 || payload.Name.Any(char.IsControl))))
            {
                return Results.Problem(statusCode: 400, detail: "Email xác thực từ Google là bắt buộc.");
            }

            var email = payload.Email.Trim().ToLowerInvariant();
            var subject = payload.Subject.Trim();

            await using var linkCheck = source.CreateCommand("SELECT user_id FROM identity.external_identities WHERE provider = 'google' AND subject = @sub");
            linkCheck.Parameters.AddWithValue("sub", subject);
            var linkedUserId = (Guid?)await linkCheck.ExecuteScalarAsync(ct);

            if (linkedUserId is not null)
            {
                var linkedUser = await db.Users.SingleOrDefaultAsync(x => x.Id == linkedUserId.Value, ct);
                if (linkedUser is null) return Results.Unauthorized();

                // Privileged/STAFF accounts are forbidden and status is checked.
                if (linkedUser.Role is not ("CUSTOMER" or "SELLER"))
                    return Results.StatusCode(403);

                if (linkedUser.Status != "ACTIVE") return Results.Unauthorized();

                return await SignIn(linkedUser, db, tokens, http);
            }

            // Do not automatically merge Google and password identities by unverified email.
            var existingUser = await db.Users.SingleOrDefaultAsync(x => x.Email == email, ct);
            if (existingUser is not null)
            {
                if (existingUser.Role is not ("CUSTOMER" or "SELLER"))
                    return Results.StatusCode(403);

                return Results.Problem(statusCode: 409, detail: "Tài khoản với email này đã tồn tại. Vui lòng đăng nhập bằng mật khẩu và liên kết tài khoản Google trong cài đặt tài khoản.");
            }

            // New Google registration is CUSTOMER
            try
            {
                await using var conn = await source.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);

                var newUserId = Guid.CreateVersion7();
                var fullName = !string.IsNullOrWhiteSpace(payload.Name) ? payload.Name.Trim() : email;

                await using var insertUser = new NpgsqlCommand(
                    "INSERT INTO identity.users(id, email, password_hash, full_name, role, status, loyalty_points, created_at, updated_at) " +
                    "VALUES(@id, @email, NULL, @name, 'CUSTOMER', 'ACTIVE', 0, public.app_now(), public.app_now()) " +
                    "ON CONFLICT(email) DO NOTHING RETURNING id", conn, tx);
                insertUser.Parameters.AddWithValue("id", newUserId);
                insertUser.Parameters.AddWithValue("email", email);
                insertUser.Parameters.AddWithValue("name", fullName);

                var createdId = await insertUser.ExecuteScalarAsync(ct);
                if (createdId is not Guid confirmedUserId)
                {
                    await tx.RollbackAsync(ct);
                    await using var retryLink = source.CreateCommand("SELECT user_id FROM identity.external_identities WHERE provider = 'google' AND subject = @sub");
                    retryLink.Parameters.AddWithValue("sub", subject);
                    if (await retryLink.ExecuteScalarAsync(ct) is Guid recoveredId)
                    {
                        var recoveredUser = await db.Users.SingleOrDefaultAsync(x => x.Id == recoveredId, ct);
                        if (recoveredUser != null && (recoveredUser.Role is "CUSTOMER" or "SELLER") && recoveredUser.Status == "ACTIVE")
                            return await SignIn(recoveredUser, db, tokens, http);
                    }
                    return Results.Problem(statusCode: 409, detail: "Không thể tạo tài khoản với email này.");
                }

                await using var insertLink = new NpgsqlCommand(
                    "INSERT INTO identity.external_identities(user_id, provider, subject, email, created_at, updated_at) " +
                    "VALUES(@userId, 'google', @sub, @email, public.app_now(), public.app_now()) " +
                    "ON CONFLICT(provider, subject) DO NOTHING RETURNING id", conn, tx);
                insertLink.Parameters.AddWithValue("userId", confirmedUserId);
                insertLink.Parameters.AddWithValue("sub", subject);
                insertLink.Parameters.AddWithValue("email", email);
                var linkCreated = await insertLink.ExecuteScalarAsync(ct);
                if (linkCreated is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: 409, detail: "Tài khoản Google này đã được liên kết.");
                }

                await tx.CommitAsync(ct);

                var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == confirmedUserId, ct);
                return await SignIn(user, db, tokens, http);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Results.Problem(statusCode: 409, detail: "Xung đột tài khoản hoặc liên kết Google.");
            }
        }).AllowAnonymous().RequireRateLimiting("auth").WithTags("Auth").Produces<SessionResponse>();

        app.MapPost("/api/auth/login", (LoginRequest request, FloraDbContext db, TokenService tokens, HttpContext http) =>
            Login(request, db, tokens, http, ["CUSTOMER", "SELLER", "SELLER_STAFF"]))
            .AllowAnonymous().RequireRateLimiting("auth").WithTags("Auth").Produces<SessionResponse>();
        app.MapPost("/api/auth/admin/login", (LoginRequest request, FloraDbContext db, TokenService tokens, HttpContext http) =>
            Login(request, db, tokens, http, ["ADMIN", "OPERATIONS_MANAGER", "TECHNICIAN"]))
            .AllowAnonymous().RequireRateLimiting("auth").WithTags("Auth").Produces<SessionResponse>();
        app.MapPost("/api/auth/refresh", async (FloraDbContext db, TokenService tokens, HttpContext http) =>
        {
            if (!http.Request.Cookies.TryGetValue("florabot_refresh", out var refresh)) return Results.Unauthorized();
            var id = await tokens.ConsumeRefresh(refresh);
            var user = id is null ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id.Value.Id && x.Status == "ACTIVE" && (x.Role == "SELLER" || x.Role == "ADMIN" || x.Role == "CUSTOMER" || x.Role == "OPERATIONS_MANAGER" || x.Role == "TECHNICIAN" || x.Role == "SELLER_STAFF"));
            if (user is not null && TokenService.CredentialVersion(user) != id!.Value.Version) return Results.Unauthorized();
            if (user is not null && user.Role == "SELLER_STAFF" && user.SellerId is null) return Results.Unauthorized();
            return user is null ? Results.Unauthorized() : await SignIn(user, db, tokens, http);
        }).AllowAnonymous().RequireRateLimiting("auth").WithTags("Auth").Produces<SessionResponse>();
        app.MapPost("/api/auth/logout", async (TokenService tokens, HttpContext http) =>
        {
            if (http.Request.Cookies.TryGetValue("florabot_refresh", out var refresh)) await tokens.ConsumeRefresh(refresh);
            http.Response.Cookies.Delete("florabot_access", Options(http, "/"));
            http.Response.Cookies.Delete("florabot_refresh", Options(http, "/api/auth"));
            return Results.NoContent();
        }).AllowAnonymous().WithTags("Auth");
        app.MapGet("/api/auth/me", async (FloraDbContext db, HttpContext http) =>
        {
            var id = Guid.Parse(http.User.FindFirstValue("sub")!);
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Status == "ACTIVE");
            if (user is null || user.Role == "STAFF") return Results.Unauthorized();
            if (user.Role == "SELLER_STAFF" && user.SellerId is null) return Results.Unauthorized();
            var seller = user.SellerId is null ? null : await db.Sellers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == user.SellerId);
            if (user.Role == "SELLER_STAFF" && seller is null) return Results.Unauthorized();
            return Results.Ok(Session(user, seller));
        }).RequireAuthorization("Account").WithTags("Auth").Produces<SessionResponse>();
    }
    private static async Task<IResult> Login(LoginRequest request, FloraDbContext db, TokenService tokens, HttpContext http, string[] roles)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254 ||
            string.IsNullOrEmpty(request.Password) || System.Text.Encoding.UTF8.GetByteCount(request.Password) > 72)
            return Results.BadRequest();
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email && x.Status == "ACTIVE" && roles.Contains(x.Role));
        var valid = false;
        try { valid = BCrypt.Net.BCrypt.Verify(request.Password, user?.PasswordHash ?? "$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy"); }
        catch (BCrypt.Net.SaltParseException) { }
        if (!valid || user is null) return Results.Unauthorized();
        return await SignIn(user, db, tokens, http);
    }
    private static SessionResponse Session(User user, Seller? seller) => new(user.Id, user.FullName, user.Role, user.SellerId, seller?.Status, seller?.PackageExpiresAt);
    internal static async Task<IResult> SignIn(User user, FloraDbContext db, TokenService tokens, HttpContext http)
    {
        if (user.Role == "SELLER_STAFF" && user.SellerId is null) return Results.Unauthorized();
        var seller = user.SellerId is null ? null : await db.Sellers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == user.SellerId);
        if (user.Role == "SELLER_STAFF" && seller is null) return Results.Unauthorized();
        http.Response.Cookies.Append("florabot_access", tokens.Issue(user, seller?.Status, bindCredentialVersion: true), Options(http, "/", TimeSpan.FromMinutes(15)));
        http.Response.Cookies.Append("florabot_refresh", await tokens.CreateRefresh(user), Options(http, "/api/auth", TimeSpan.FromDays(7)));
        return Results.Ok(Session(user, seller));
    }
    private static CookieOptions Options(HttpContext http, string path, TimeSpan? age = null) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps || !(http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment() || http.RequestServices.GetRequiredService<IHostEnvironment>().IsEnvironment("Testing")),
        SameSite = SameSiteMode.Strict,
        Path = path,
        MaxAge = age,
        IsEssential = true
    };
}
