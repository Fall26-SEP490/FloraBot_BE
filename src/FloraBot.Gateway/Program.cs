using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Yarp.ReverseProxy.Configuration;

namespace FloraBot.Gateway;

public class GatewayProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var destination = builder.Configuration["API_UPSTREAM_URL"] ?? "http://127.0.0.1:5080";
        if (!Uri.TryCreate(destination, UriKind.Absolute, out var upstream) || upstream.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(upstream.UserInfo) || upstream.AbsolutePath != "/" || !string.IsNullOrEmpty(upstream.Query) || !string.IsNullOrEmpty(upstream.Fragment))
            throw new InvalidOperationException("API_UPSTREAM_URL must be an HTTP(S) origin without credentials, path, query or fragment.");
        var signingKey = builder.Configuration["JWT_SIGNING_KEY"] ?? "";
        if (Encoding.UTF8.GetByteCount(signingKey) < 32) throw new InvalidOperationException("Gateway and API require the same JWT_SIGNING_KEY of at least 32 bytes.");
        var limit = builder.Configuration.GetValue("GATEWAY_REQUESTS_PER_MINUTE", 600);
        if (limit is < 1 or > 10000) throw new InvalidOperationException("GATEWAY_REQUESTS_PER_MINUTE must be between 1 and 10000.");
        var trustedProxies = (builder.Configuration["TRUSTED_PROXY_IPS"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(IPAddress.Parse).ToArray();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear(); options.KnownProxies.Clear();
            foreach (var ip in trustedProxies) options.KnownProxies.Add(ip);
        });
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new()
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                ValidateIssuer = true,
                ValidIssuer = "FloraBot",
                ValidateAudience = true,
                ValidAudience = "FloraBot",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                RoleClaimType = "role",
                NameClaimType = "name"
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (!context.Request.Headers.ContainsKey("Authorization") && !context.Request.Headers.ContainsKey("X-Kiosk-Key"))
                        context.Token = context.Request.Cookies["florabot_access"];
                    return Task.CompletedTask;
                }
            };
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("gateway", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.OnRejected = (context, _) => { context.HttpContext.Response.Headers.RetryAfter = "60"; return ValueTask.CompletedTask; };
        });
        var routes = new[]
        {
            new RouteConfig { RouteId = "reconciliation", ClusterId = "api", Order = -1,
                Match = new RouteMatch { Path = "/api/admin/flows/reconcile_gateway" }, MaxRequestBodySize = 8 * 1024 * 1024 },
            new RouteConfig { RouteId = "api", ClusterId = "api", Match = new RouteMatch { Path = "/api/{**rest}" } },
            new RouteConfig { RouteId = "realtime", ClusterId = "api", Match = new RouteMatch { Path = "/hub/{**rest}" } },
            new RouteConfig { RouteId = "openapi", ClusterId = "api", Match = new RouteMatch { Path = "/openapi/{**rest}" } },
        }.Select(route => route with { Transforms = [new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" }] }).ToArray();
        builder.Services.AddReverseProxy().LoadFromMemory(routes,
            [new ClusterConfig { ClusterId = "api", Destinations = new Dictionary<string, DestinationConfig> { ["primary"] = new() { Address = upstream.ToString() } } }]);
        var app = builder.Build();
        // Forwarded identity is accepted only from explicitly configured immediate proxies.
        if (trustedProxies.Length > 0) app.UseForwardedHeaders();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.Use(async (http, next) =>
        {
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            var renewsSession = http.Request.Path.Value is "/api/auth/login" or "/api/auth/admin/login" or "/api/auth/refresh" or "/api/auth/logout";
            var opensDocumentation = HttpMethods.IsGet(http.Request.Method) &&
                string.Equals(http.Request.Path.Value?.TrimEnd('/'), "/openapi", StringComparison.OrdinalIgnoreCase);
            var hasSession = http.Request.Headers.ContainsKey("Authorization") ||
                (!http.Request.Headers.ContainsKey("X-Kiosk-Key") && http.Request.Cookies.ContainsKey("florabot_access"));
            if (!renewsSession && !opensDocumentation && hasSession && !(await http.AuthenticateAsync()).Succeeded)
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "gateway" }));
        app.MapGet("/openapi", async (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Redirect((await http.AuthenticateAsync()).Succeeded ? "/openapi/v1.json" : "/admin/login");
        });
        // The API still validates current user status, device keys, endpoint roles and tenant ownership.
        app.MapReverseProxy().RequireRateLimiting("gateway");
        app.Run();
    }
}
