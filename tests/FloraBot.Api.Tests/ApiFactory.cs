using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FloraBot.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
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
            ["EMAIL_PROVIDER"] = "Disabled"
        };
        foreach (var pair in settings) builder.UseSetting(pair.Key, pair.Value);
    }
    public string Token(string role = "SELLER", DateTime? expires = null, Guid? userId = null, Guid? sellerId = null)
    {
        using var scope = Services.CreateScope();
        var user = new User
        {
            Id = userId ?? Guid.Parse(role == "ADMIN" ? "10000000-0000-0000-0000-000000000001" : "10000000-0000-0000-0000-000000000003"),
            Role = role,
            FullName = "Test",
            SellerId = role == "SELLER" ? sellerId ?? Guid.Parse("20000000-0000-0000-0000-000000000001") : null
        };
        return scope.ServiceProvider.GetRequiredService<TokenService>().Issue(user, "APPROVED", expires: expires);
    }
}
