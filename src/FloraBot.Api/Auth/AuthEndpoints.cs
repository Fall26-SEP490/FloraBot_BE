using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Auth;

public sealed record LoginRequest(string Email, string Password);
public sealed record SessionResponse(Guid Id, string Name, string Role, Guid? SellerId, string? SellerStatus, DateOnly? PackageExpiresAt);

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        app.MapPost("/api/auth/login", (LoginRequest request, FloraDbContext db, TokenService tokens, HttpContext http) =>
            Login(request, db, tokens, http, ["CUSTOMER", "SELLER"]))
            .AllowAnonymous().RequireRateLimiting("auth").WithTags("Auth").Produces<SessionResponse>();
        app.MapPost("/api/auth/admin/login", (LoginRequest request, FloraDbContext db, TokenService tokens, HttpContext http) =>
            Login(request, db, tokens, http, ["ADMIN", "STAFF"]))
            .AllowAnonymous().RequireRateLimiting("auth").WithTags("Auth").Produces<SessionResponse>();
        app.MapPost("/api/auth/refresh", async (FloraDbContext db, TokenService tokens, HttpContext http) =>
        {
            if (!http.Request.Cookies.TryGetValue("florabot_refresh", out var refresh)) return Results.Unauthorized();
            var id = await tokens.ConsumeRefresh(refresh);
            var user = id is null ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id.Value.Id && x.Status == "ACTIVE" && (x.Role == "SELLER" || x.Role == "ADMIN" || x.Role == "CUSTOMER" || x.Role == "STAFF"));
            if (user is not null && TokenService.CredentialVersion(user) != id!.Value.Version) return Results.Unauthorized();
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
            if (user is null) return Results.Unauthorized();
            var seller = user.SellerId is null ? null : await db.Sellers.AsNoTracking().SingleAsync(x => x.Id == user.SellerId);
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
        var seller = user.SellerId is null ? null : await db.Sellers.AsNoTracking().SingleAsync(x => x.Id == user.SellerId);
        http.Response.Cookies.Append("florabot_access", tokens.Issue(user, seller?.Status), Options(http, "/", TimeSpan.FromMinutes(15)));
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
