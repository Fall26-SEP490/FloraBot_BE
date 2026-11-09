using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FloraBot.Api.Tests;

public sealed class Esp32ConfigurationTests
{
    private static readonly Guid KioskId = Guid.Parse("40000000-0000-0000-0000-000000000099");
    private static readonly Guid InstallationId = Guid.Parse("70000000-0000-0000-0000-000000000099");
    private const string TestPassword = "test-only-mqtt-secret\"\\\n?F";
    private const string TestKey = "abababababababababababababababababababababababababababababababab";

    [Fact]
    public async Task MetadataSeparatesRegisteredAndFirmwareClientIdsAndNeverReadsSecrets()
    {
        var sessions = new FakeSessions();
        var source = new FakeSource();
        await using var app = await Host(sessions, source);
        var client = app.GetTestClient();
        using var response = await client.SendAsync(Request("GET", $"/api/operations/kiosks/{KioskId}/esp32-config", "OPERATIONS_MANAGER"));
        Assert.Equal(200, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("registered-client", json.RootElement.GetProperty("mqttClientId").GetString());
        Assert.Equal("esp32-ESP32-TEST", json.RootElement.GetProperty("firmwareMqttClientId").GetString());
        Assert.False(json.RootElement.GetProperty("mqttClientIdsMatch").GetBoolean());
        Assert.True(json.RootElement.GetProperty("canDownload").GetBoolean());
        Assert.DoesNotContain(TestPassword, body);
        Assert.DoesNotContain(TestKey, body);
        Assert.DoesNotContain("hmac", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, source.SecretReads);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal((KioskId, false, "SUCCESS"), sessions.Audits.Single());
    }

    [Fact]
    public async Task DownloadRequiresFreshReauthenticationThenReturnsPrivateAttachmentWithBlankWifi()
    {
        var sessions = new FakeSessions { Reauthenticated = false };
        var source = new FakeSource();
        await using var app = await Host(sessions, source);
        var client = app.GetTestClient();
        var url = $"/api/operations/kiosks/{KioskId}/esp32-config/download";
        using var denied = await client.SendAsync(Request("POST", url, "OPERATIONS_MANAGER", "wrong"));
        Assert.Equal(401, (int)denied.StatusCode);
        Assert.Contains("ESP32_CONFIG_REAUTHENTICATION_FAILED", await denied.Content.ReadAsStringAsync());
        Assert.Equal(0, source.SecretReads);
        sessions.Reauthenticated = true;
        using var response = await client.SendAsync(Request("POST", url, "OPERATIONS_MANAGER", "current-account-password"));
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("config.local.h", response.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("text/x-c++hdr", response.Content.Headers.ContentType?.MediaType);
        var file = await response.Content.ReadAsStringAsync();
        Assert.Contains("wifiSsid[] = \"\";", file);
        Assert.Contains("wifiPassword[] = \"\";", file);
        Assert.Contains("hardwareId[] = \"ESP32-TEST\";", file);
        Assert.Contains("mqttPort = 8883;", file);
        Assert.Contains("mqttPassword[] = " + Esp32ConfigurationHeader.CppString(TestPassword), file);
        Assert.Contains("hmacKeyHex[] = \"" + TestKey + "\";", file);
        Assert.Equal(2, sessions.ReauthenticationChecks);
        Assert.Equal(1, source.SecretReads);
        Assert.Equal((KioskId, true, "SUCCESS"), sessions.Audits.Last());
    }

    [Theory]
    [InlineData("ADMIN", "operations")]
    [InlineData("ADMIN", "staff")]
    [InlineData("SELLER", "operations")]
    [InlineData("TECHNICIAN", "operations")]
    [InlineData("OPERATIONS_MANAGER", "staff")]
    public async Task WrongRoleCannotDownloadAndDenialIsAudited(string caller, string route)
    {
        var sessions = new FakeSessions();
        var source = new FakeSource();
        await using var app = await Host(sessions, source);
        var url = route == "staff"
            ? $"/api/staff/kiosk-installations/{InstallationId}/esp32-config/download"
            : $"/api/operations/kiosks/{KioskId}/esp32-config/download";
        using var response = await app.GetTestClient().SendAsync(Request("POST", url, caller, "password"));
        Assert.Equal(403, (int)response.StatusCode);
        Assert.Equal(0, sessions.ScopeChecks);
        Assert.Equal(0, source.SecretReads);
        Assert.Equal("ESP32_CONFIG_FORBIDDEN", sessions.Audits.Single().Result);
    }

    [Fact]
    public async Task AdminMetadataNeverGrantsDownloadAndHasNoDownloadRoute()
    {
        var sessions = new FakeSessions();
        var source = new FakeSource();
        await using var app = await Host(sessions, source);
        var client = app.GetTestClient();
        using var response = await client.SendAsync(Request("GET", $"/api/admin/kiosks/{KioskId}/esp32-config", "ADMIN"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("canDownload").GetBoolean());
        Assert.False(json.RootElement.GetProperty("requiresReauthentication").GetBoolean());
        Assert.Equal(0, source.SecretReads);
        using var absent = await client.SendAsync(Request("POST", $"/api/admin/kiosks/{KioskId}/esp32-config/download", "ADMIN", "password"));
        Assert.Equal(404, (int)absent.StatusCode);
    }

    [Theory]
    [InlineData("OPERATIONS_MANAGER", false)]
    [InlineData("OPERATIONS_MANAGER", true)]
    [InlineData("TECHNICIAN", false)]
    [InlineData("TECHNICIAN", true)]
    public async Task MissingForeignOrRevokedScopeReturns404BeforeReadingSecrets(string role, bool download)
    {
        var sessions = new FakeSessions { InScope = false };
        var source = new FakeSource();
        await using var app = await Host(sessions, source);
        var url = role == "TECHNICIAN" ? $"/api/staff/kiosk-installations/{InstallationId}/esp32-config"
            : $"/api/operations/kiosks/{KioskId}/esp32-config";
        if (download) url += "/download";
        using var response = await app.GetTestClient().SendAsync(Request(download ? "POST" : "GET", url, role, "password"));
        Assert.Equal(404, (int)response.StatusCode);
        Assert.Equal(0, sessions.ReauthenticationChecks);
        Assert.Equal(0, source.SecretReads);
        Assert.Equal("ESP32_CONFIG_NOT_FOUND", sessions.Audits.Single().Result);
    }

    [Fact]
    public async Task AssignmentIsCheckedAgainForDownloadAfterSuccessfulRead()
    {
        var sessions = new FakeSessions();
        var source = new FakeSource();
        await using var app = await Host(sessions, source);
        var client = app.GetTestClient();
        var url = $"/api/staff/kiosk-installations/{InstallationId}/esp32-config";
        using var read = await client.SendAsync(Request("GET", url, "TECHNICIAN"));
        Assert.Equal(200, (int)read.StatusCode);
        sessions.InScope = false;
        using var download = await client.SendAsync(Request("POST", url + "/download", "TECHNICIAN", "password"));
        Assert.Equal(404, (int)download.StatusCode);
        Assert.Equal(2, sessions.ScopeChecks);
        Assert.Equal(0, source.SecretReads);
    }

    [Fact]
    public async Task RevokedAccountIsRejectedBeforeDeploymentOrSecretAccess()
    {
        var sessions = new FakeSessions { ActorActive = false };
        var source = new FakeSource();
        await using var app = await Host(sessions, source);
        using var response = await app.GetTestClient().SendAsync(Request("POST",
            $"/api/staff/kiosk-installations/{InstallationId}/esp32-config/download", "TECHNICIAN", "password"));
        Assert.Equal(403, (int)response.StatusCode);
        Assert.Equal(0, sessions.ReauthenticationChecks);
        Assert.Equal(0, source.SecretReads);
        Assert.Equal("ESP32_CONFIG_FORBIDDEN", sessions.Audits.Single().Result);
    }

    [Theory]
    [InlineData("ESP32_CONFIG_NOT_PROVISIONED")]
    [InlineData("ESP32_CONFIG_HMAC_MISMATCH")]
    public async Task ProvisioningErrorsAreSpecificAndDoNotExposeSecrets(string code)
    {
        var sessions = new FakeSessions();
        var source = new FakeSource { SecretError = code };
        await using var app = await Host(sessions, source);
        using var response = await app.GetTestClient().SendAsync(Request("POST", $"/api/operations/kiosks/{KioskId}/esp32-config/download", "OPERATIONS_MANAGER", "password"));
        Assert.Equal(409, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(code, body);
        Assert.DoesNotContain(TestPassword, body);
        Assert.DoesNotContain(TestKey, body);
        Assert.Equal(code, sessions.Audits.Single().Result);
    }

    [Fact]
    public async Task MissingBoardDeploymentIsVisibleWithoutReadingSecrets()
    {
        var sessions = new FakeSessions();
        var source = new FakeSource { BrokerReady = false };
        await using var app = await Host(sessions, source);
        var client = app.GetTestClient();
        var url = $"/api/operations/kiosks/{KioskId}/esp32-config";
        using var read = await client.SendAsync(Request("GET", url, "OPERATIONS_MANAGER"));
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("canDownload").GetBoolean());
        Assert.Equal("ESP32_CONFIG_DEPLOYMENT_NOT_CONFIGURED", json.RootElement.GetProperty("downloadUnavailableCode").GetString());
        using var download = await client.SendAsync(Request("POST", url + "/download", "OPERATIONS_MANAGER", "password"));
        Assert.Equal(409, (int)download.StatusCode);
        Assert.Equal(0, source.SecretReads);
    }

    [Fact]
    public async Task AuditFailurePreventsFileDisclosure()
    {
        var sessions = new FakeSessions { AuditFails = true };
        var source = new FakeSource();
        await using var app = await Host(sessions, source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.GetTestClient().SendAsync(Request("POST",
            $"/api/operations/kiosks/{KioskId}/esp32-config/download", "OPERATIONS_MANAGER", "password")));
    }

    [Fact]
    public void CppEscapingPreservesUtf8AndPreventsLiteralOrDirectiveInjection()
    {
        Assert.Equal("\"A\\042\\134\\012\\015\\011\\001\\077\\077/\\303\\251F\"",
            Esp32ConfigurationHeader.CppString("A\"\\\n\r\t\u0001??/éF"));
        Assert.Equal("\"\\001Af\"", Esp32ConfigurationHeader.CppString("\u0001Af"));
        Assert.Equal("{}", JsonSerializer.Serialize(new Esp32DeviceSecrets(TestPassword, TestKey)));
        Assert.DoesNotContain(TestPassword, new Esp32DeviceSecrets(TestPassword, TestKey).ToString());
        Assert.DoesNotContain("password-value", new Esp32ConfigDownloadRequest("password-value").ToString());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("localhost", false)]
    [InlineData("localhost.", false)]
    [InlineData("device.localhost", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("mqtt://broker.test", false)]
    [InlineData("broker.test:8883", false)]
    [InlineData("192.168.1.15", true)]
    [InlineData("mqtt.example.test", true)]
    public void BoardBrokerHostMustBeAnExplicitNonLoopbackHost(string? host, bool valid) =>
        Assert.Equal(valid, Esp32ConfigurationSource.ValidBoardHost(host));

    [Fact]
    public void BackendKeyComparisonUsesAValidProtocolProbeAndRejectsWrongHardwareAndKey()
    {
        var key = Convert.FromHexString(TestKey);
        var ring = new DeviceKeyRing(new Dictionary<string, byte[]> { ["ESP32-TEST"] = key });
        Assert.True(Esp32ConfigurationSource.MatchesBackendKey(ring, "ESP32-TEST", key));
        Assert.False(Esp32ConfigurationSource.MatchesBackendKey(ring, "ESP32-OTHER", key));
        Assert.False(Esp32ConfigurationSource.MatchesBackendKey(ring, "ESP32-TEST", new byte[32]));
    }

    [Fact]
    public async Task SecretStoreUsesExactHardwareKeyAndCurrentInMemoryBackendKey()
    {
        // Every file/key in this test is synthetic and private to the test's temporary directory.
        var path = Path.Combine(Path.GetTempPath(), "florabot-esp32-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ESP32-TEST"] = new { password = TestPassword, hmacKey = TestKey },
                ["florabot-backend"] = new { password = "backend-must-never-be-exported" }
            }));
            using var services = new ServiceCollection().AddSingleton(new DeviceKeyRing(new Dictionary<string, byte[]>
                { ["ESP32-TEST"] = Convert.FromHexString(TestKey) })).BuildServiceProvider();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["MQTT_CREDENTIALS_FILE"] = path }).Build();
            var source = new Esp32ConfigurationSource(configuration, services);
            Assert.NotNull((await source.ReadSecretsAsync("ESP32-TEST", default)).Secrets);
            Assert.Equal("ESP32_CONFIG_NOT_PROVISIONED", (await source.ReadSecretsAsync("ESP32-OTHER", default)).ErrorCode);
            Assert.Equal("ESP32_CONFIG_NOT_PROVISIONED", (await source.ReadSecretsAsync("florabot-backend", default)).ErrorCode);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Dictionary<string, object>
                { ["ESP32-TEST"] = new { password = TestPassword, hmacKey = new string('c', 64) } }));
            Assert.Equal("ESP32_CONFIG_HMAC_MISMATCH", (await source.ReadSecretsAsync("ESP32-TEST", default)).ErrorCode);
            await File.WriteAllTextAsync(path, "{invalid-json");
            Assert.Equal("ESP32_CONFIG_NOT_PROVISIONED", (await source.ReadSecretsAsync("ESP32-TEST", default)).ErrorCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BrokerReadsOnlyExplicitBoardSettingsAndExportsOnlyPublicCertificate()
    {
        var path = Path.Combine(Path.GetTempPath(), "florabot-esp32-ca-" + Guid.NewGuid().ToString("N") + ".pem");
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=synthetic-esp32-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            await File.WriteAllTextAsync(path, cert.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
            using var services = new ServiceCollection().BuildServiceProvider();
            var settings = new Dictionary<string, string?> { ["MQTT_HOST"] = "internal-container", ["MQTT_CA_FILE"] = path };
            Assert.Null(await new Esp32ConfigurationSource(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), services).ReadBrokerAsync(default));
            settings["ESP32_MQTT_HOST"] = "mqtt.example.test";
            settings["ESP32_MQTT_CA_FILE"] = path;
            var broker = await new Esp32ConfigurationSource(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), services).ReadBrokerAsync(default);
            Assert.Equal("mqtt.example.test", broker?.Host);
            Assert.Contains("BEGIN CERTIFICATE", broker?.CaCertificatePem);
            Assert.DoesNotContain("PRIVATE KEY", broker?.CaCertificatePem);
        }
        finally { File.Delete(path); }
    }

    private static HttpRequestMessage Request(string method, string path, string role, string? password = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-Test-Role", role);
        if (method == "POST") request.Content = new StringContent(JsonSerializer.Serialize(new { password }), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<WebApplication> Host(FakeSessions sessions, FakeSource source)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization(options => options.AddPolicy("Account", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddRateLimiter(options => options.AddConcurrencyLimiter("auth", limit => limit.PermitLimit = 100));
        builder.Services.AddSingleton<IEsp32ConfigurationSessions>(sessions);
        builder.Services.AddSingleton<IEsp32ConfigurationSource>(source);
        builder.Services.AddEsp32Configuration();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var role = context.Request.Headers["X-Test-Role"].ToString();
            context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", "10000000-0000-0000-0000-000000000099"), new Claim(ClaimTypes.Role, role)
            ], "synthetic-test"));
            await next(context);
        });
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapEsp32Configuration();
        await app.StartAsync();
        return app;
    }

    private sealed class FakeSource : IEsp32ConfigurationSource
    {
        public bool BrokerReady { get; set; } = true;
        public int SecretReads { get; private set; }
        public string? SecretError { get; set; }
        public Task<Esp32BrokerConfiguration?> ReadBrokerAsync(CancellationToken ct) => Task.FromResult(BrokerReady
            ? new Esp32BrokerConfiguration("mqtt.example.test", 8883, "-----BEGIN CERTIFICATE-----\nsynthetic\n-----END CERTIFICATE-----\n") : null);
        public Task<Esp32SecretResult> ReadSecretsAsync(string hardwareId, CancellationToken ct)
        {
            SecretReads++;
            return Task.FromResult(SecretError is null ? new Esp32SecretResult(new(TestPassword, TestKey), null) : new(null, SecretError));
        }
    }

    private sealed class FakeSessions : IEsp32ConfigurationSessions, IEsp32ConfigurationSession
    {
        public bool InScope { get; set; } = true;
        public bool ActorActive { get; set; } = true;
        public bool Reauthenticated { get; set; } = true;
        public bool AuditFails { get; set; }
        public int ScopeChecks { get; private set; }
        public int ReauthenticationChecks { get; private set; }
        public List<(Guid? Kiosk, bool Download, string Result)> Audits { get; } = [];
        public Task<IEsp32ConfigurationSession> OpenAsync(CancellationToken ct) => Task.FromResult<IEsp32ConfigurationSession>(this);
        public Task<Esp32KioskAccess> ResolveAsync(Guid resource, Guid actor, string role, CancellationToken ct)
        {
            ScopeChecks++;
            return Task.FromResult(new Esp32KioskAccess(InScope ? new(KioskId, "K-TEST", "OFFLINE", "ESP32-TEST", "registered-client") : null, KioskId, ActorActive));
        }
        public Task<bool> ReauthenticateAsync(Guid actor, string role, string? password, CancellationToken ct)
        {
            ReauthenticationChecks++;
            return Task.FromResult(Reauthenticated);
        }
        public Task AuditAndCommitAsync(Guid actor, Guid? kiosk, Guid? installation, bool download, string result, CancellationToken ct)
        {
            if (AuditFails) throw new InvalidOperationException("synthetic audit unavailable");
            Audits.Add((kiosk, download, result));
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
