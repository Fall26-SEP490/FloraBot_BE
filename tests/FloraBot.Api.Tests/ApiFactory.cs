using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Notify;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FloraBot.Api.Tests;

public sealed class TestRemoteIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Client-IP", out var ipHeader) && IPAddress.TryParse(ipHeader, out var clientIp))
                {
                    context.Connection.RemoteIpAddress = clientIp;
                }
                else if (context.Connection.RemoteIpAddress is null && context.Request.Path.StartsWithSegments("/api/auth"))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse($"127.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
                }
                return nextMiddleware();
            });
            next(app);
        };
    }
}

public sealed class TestEmailSender : ITransactionalEmailSender
{
    public List<TransactionalEmail> SentEmails { get; } = [];
    public bool ShouldFail { get; set; }

    public Task SendAsync(TransactionalEmail email, CancellationToken ct)
    {
        if (ShouldFail) throw new EmailUnavailableException();
        SentEmails.Add(email);
        return Task.CompletedTask;
    }

    public string? ExtractLastCode()
    {
        var last = SentEmails.LastOrDefault();
        if (last is null) return null;
        var match = Regex.Match(last.Text, @"\b\d{6}\b");
        return match.Success ? match.Value : null;
    }
}

public sealed class TestGoogleValidator : IGoogleIdentityValidator
{
    public Func<string, string, GoogleJsonWebSignature.Payload>? Handler { get; set; }

    public Task<GoogleJsonWebSignature.Payload> ValidateAsync(string idToken, string audience, CancellationToken ct = default)
    {
        if (Handler is not null)
        {
            return Task.FromResult(Handler(idToken, audience));
        }

        // Untrusted/mock tokens are rejected by default; no runtime magic flags
        throw new InvalidJwtException("Invalid or untrusted token");
    }
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public TestEmailSender EmailSender { get; } = new();
    public TestGoogleValidator GoogleValidator { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // TestServer disposes hosts concurrently; Windows EventLog is not a test output sink.
        builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
        var settings = new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = Environment.GetEnvironmentVariable("TEST_DATABASE_URL") ?? "Host=localhost;Port=55436;Database=florabot_api_tests;Username=florabot;Password=local-florabot-only",
            ["VALKEY_URL"] = Environment.GetEnvironmentVariable("TEST_VALKEY_URL") ?? "localhost:56379",
            ["JWT_SIGNING_KEY"] = "test-only-key-at-least-thirty-two-bytes-long-not-production",
            ["PAYOS_CHECKSUM_KEY"] = "test-only-checksum-key",
            ["JOBS_ENABLED"] = "false",
            ["MQTT_ENABLED"] = "false",
            ["EMAIL_PROVIDER"] = "Brevo",
            ["EMAIL_SENDER"] = "noreply@florabot.test",
            ["BREVO_API_KEY"] = "test-brevo-api-key",
            ["Authentication:Google:ClientId"] = "test-google-client-id"
        };
        foreach (var pair in settings) builder.UseSetting(pair.Key, pair.Value);

        builder.ConfigureServices(services =>
        {
            services.AddTransient<IStartupFilter, TestRemoteIpStartupFilter>();
            services.AddSingleton<ITransactionalEmailSender>(EmailSender);
            services.AddSingleton<IGoogleIdentityValidator>(GoogleValidator);
            services.AddHttpClient<TransactionalEmailSender>().ConfigurePrimaryHttpMessageHandler(() => new TestLoopbackMessageHandler());
        });
    }

    private sealed class TestLoopbackMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"messageId\":\"intercepted-fake-id\"}", Encoding.UTF8, "application/json")
            });
        }
    }

    public async Task<(string ChallengeId, string Code)> RegisterAsync(HttpClient client, string fullName, string email, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/register", new MemberRegistration(fullName, email, password));
        resp.EnsureSuccessStatusCode();
        var challenge = await resp.Content.ReadFromJsonAsync<RegistrationChallengeResponse>();
        var code = EmailSender.ExtractLastCode()!;
        return (challenge!.ChallengeId, code);
    }

    public async Task<HttpResponseMessage> RegisterAndVerifyAsync(HttpClient client, string fullName, string email, string password)
    {
        var (challengeId, code) = await RegisterAsync(client, fullName, email, password);
        return await client.PostAsJsonAsync("/api/auth/register/verify", new VerifyRegistrationRequest(challengeId, code));
    }

    public string Token(string role = "SELLER", DateTime? expires = null, Guid? userId = null, Guid? sellerId = null)
    {
        using var scope = Services.CreateScope();
        var user = new User
        {
            Id = userId ?? Guid.Parse(role == "ADMIN" ? "10000000-0000-0000-0000-000000000001" : "10000000-0000-0000-0000-000000000003"),
            Role = role,
            FullName = "Test",
            SellerId = (role == "SELLER" || role == "SELLER_STAFF") ? sellerId ?? Guid.Parse("20000000-0000-0000-0000-000000000001") : null
        };
        return scope.ServiceProvider.GetRequiredService<TokenService>().Issue(user, "APPROVED", expires: expires);
    }
}
