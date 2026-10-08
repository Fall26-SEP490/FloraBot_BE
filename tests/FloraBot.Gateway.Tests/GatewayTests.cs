using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using FloraBot.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace FloraBot.Gateway.Tests;

public sealed class GatewayFixture : IAsyncLifetime
{
    public const string Key = "gateway-test-only-signing-key-at-least-thirty-two-bytes";
    private WebApplication upstream = null!;
    public WebApplicationFactory<GatewayProgram> Factory { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        upstream = builder.Build();
        upstream.UseWebSockets();
        upstream.Map("/hub/socket", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[1024];
            var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            await socket.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Text, true, CancellationToken.None);
        });
        upstream.Map("/{**rest}", async context =>
        {
            context.Response.Cookies.Append("upstream-test", "cookie-value", new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict });
            await context.Response.WriteAsJsonAsync(new Echo(context.Request.Path, context.Request.QueryString.Value ?? "", context.Request.Method,
                await new StreamReader(context.Request.Body).ReadToEndAsync(), context.Request.Host.Value ?? "", context.Request.Headers.ContainsKey("X-Kiosk-Key")));
        });
        await upstream.StartAsync();
        var address = upstream.Urls.Single();
        Factory = new WebApplicationFactory<GatewayProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("API_UPSTREAM_URL", address); builder.UseSetting("JWT_SIGNING_KEY", Key);
            builder.UseSetting("TRUSTED_PROXY_IPS", "");
        });
    }
    public async Task DisposeAsync() { await Factory.DisposeAsync(); await upstream.DisposeAsync(); }
    public static string Token(DateTime expiry) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("FloraBot", "FloraBot",
        [new Claim("sub", Guid.NewGuid().ToString()), new Claim("role", "ADMIN")], expires: expiry,
        signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256)));
    public sealed record Echo(string Path, string Query, string Method, string Body, string Host, bool DeviceKey);
}

public sealed class GatewayTests(GatewayFixture fixture) : IClassFixture<GatewayFixture>
{
    [Theory]
    [InlineData("/openapi", false)]
    [InlineData("/openapi/", false)]
    [InlineData("/openapi", true)]
    [InlineData("/openapi/", true)]
    public async Task DocumentationEntryRedirectsToLoginOrDocument(string path, bool authenticated)
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", "florabot_access=" +
            (authenticated ? GatewayFixture.Token(DateTime.UtcNow.AddMinutes(5)) : "expired-session"));
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(authenticated ? "/openapi/v1.json" : "/admin/login", response.Headers.Location?.OriginalString);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task AnonymousDocumentationEntryRedirectsToLogin()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/openapi");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ProxyPreservesPathBodyHostDeviceKeyAndCookies()
    {
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = "flowers.example";
        client.DefaultRequestHeaders.Add("X-Kiosk-Key", "test-device-key");
        client.DefaultRequestHeaders.Add("Cookie", "florabot_access=old-portal-session");
        var response = await client.PostAsync("/api/kiosks/test?state=online", new StringContent("{\"value\":42}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = await response.Content.ReadFromJsonAsync<GatewayFixture.Echo>();
        Assert.Equal("/api/kiosks/test", echo!.Path); Assert.Equal("?state=online", echo.Query);
        Assert.Equal("POST", echo.Method); Assert.Equal("{\"value\":42}", echo.Body);
        Assert.Equal("flowers.example", echo.Host); Assert.True(echo.DeviceKey);
        Assert.Contains("httponly", response.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/not-an-api-route")).StatusCode);
    }
    [Fact]
    public async Task InvalidSessionsAreRejectedButRenewalStillReachesApi()
    {
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", GatewayFixture.Token(DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", GatewayFixture.Token(DateTime.UtcNow.AddMinutes(5)));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add("Cookie", "florabot_access=invalid-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/admin/login", null)).StatusCode);
    }
    [Fact]
    public async Task ClientCannotBypassRateLimitUsingForwardedHeaders()
    {
        using var app = fixture.Factory.WithWebHostBuilder(builder => builder.UseSetting("GATEWAY_REQUESTS_PER_MINUTE", "2"));
        using var client = app.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/packages");
            request.Headers.Add("X-Forwarded-For", $"192.0.2.{i + 1}");
            var response = await client.SendAsync(request);
            Assert.Equal(i < 2 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, response.StatusCode);
            if (i == 2) Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.RetryAfter?.Delta);
        }
    }
    [Fact]
    public async Task ExplicitlyTrustedProxyPartitionsByForwardedClientIp()
    {
        using var app = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("GATEWAY_REQUESTS_PER_MINUTE", "1");
            builder.UseSetting("TRUSTED_PROXY_IPS", "127.0.0.1,::1");
        });
        app.UseKestrel(0);
        using var client = app.CreateClient();
        var ips = new[] { "192.0.2.10", "192.0.2.11", "192.0.2.10" };
        for (var index = 0; index < ips.Length; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/packages");
            request.Headers.Add("X-Forwarded-For", ips[index]);
            var response = await client.SendAsync(request);
            Assert.Equal(index < 2 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }
    [Fact]
    public async Task UnavailableUpstreamReturnsBadGateway()
    {
        using var app = fixture.Factory.WithWebHostBuilder(builder => builder.UseSetting("API_UPSTREAM_URL", "http://127.0.0.1:1"));
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync("/api/packages")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
    [Fact]
    public async Task RealtimeRouteSupportsWebSocketUpgrade()
    {
        using var gateway = fixture.Factory.WithWebHostBuilder(_ => { });
        gateway.UseKestrel(0);
        using var client = gateway.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new UriBuilder(client.BaseAddress!) { Scheme = "ws", Path = "/hub/socket" }.Uri, timeout.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("door-event"), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[1024];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal("door-event", Encoding.UTF8.GetString(buffer, 0, result.Count));
    }
}
