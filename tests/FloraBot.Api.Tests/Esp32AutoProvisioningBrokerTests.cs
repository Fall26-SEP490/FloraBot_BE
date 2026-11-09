using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.Extensions.Configuration;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace FloraBot.Api.Tests;

public sealed class SyntheticDynsecFactAttribute : FactAttribute
{
    public SyntheticDynsecFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLORABOT_DYNSEC_E2E") != "1")
            Skip = "Requires the isolated synthetic Mosquitto fixture; see artifacts/esp32-dynsec-broker/prepare.ps1.";
    }
}

/// <summary>Opt-in tests against a disposable broker only. No database, application host, or physical device is used.</summary>
public sealed class Esp32AutoProvisioningBrokerTests
{
    [SyntheticDynsecFact]
    public async Task DynamicProvisioningPreservesLegacyFileAuthenticationAndEnforcesDeviceTopicIsolation()
    {
        var fixture = Fixture.Load();
        using var backend = await fixture.Connect(fixture.BackendCredentialsFile);
        using var legacy = await fixture.Connect(fixture.LegacyCredentialsFile);
        var legacyHardware = fixture.Username(fixture.LegacyCredentialsFile);
        Assert.Equal(1, await Subscribe(legacy, $"kiosk/{legacyHardware}/cmd"));
        Assert.Equal(1, await Subscribe(backend, "kiosk/+/evt"));

        var kiosk = Guid.NewGuid();
        var hardware = Hardware(kiosk);
        var password = Password();
        await new MosquittoDynamicSecurity(fixture.Configuration()).EnsureAsync(kiosk, hardware, password, CancellationToken.None);
        using var device = await fixture.Connect(hardware, password, "esp32-" + hardware);
        Assert.Equal(1, await Subscribe(device, $"kiosk/{hardware}/cmd"));

        var command = Guid.NewGuid().ToString("N");
        var receivedCommand = Receive(device, $"kiosk/{hardware}/cmd", command);
        Assert.True(await Publish(backend, $"kiosk/{hardware}/cmd", command) < 128);
        await receivedCommand.WaitAsync(TimeSpan.FromSeconds(5));
        var evt = Guid.NewGuid().ToString("N");
        var receivedEvent = Receive(backend, $"kiosk/{hardware}/evt", evt);
        Assert.True(await Publish(device, $"kiosk/{hardware}/evt", evt) < 128);
        await receivedEvent.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await Subscribe(device, $"kiosk/{legacyHardware}/cmd") >= 128);
        Assert.True(await Subscribe(device, "kiosk/+/cmd") >= 128);
        Assert.True(await Subscribe(device, "$CONTROL/dynamic-security/#") >= 128);
        Assert.True(await Publish(device, $"kiosk/{legacyHardware}/evt", "synthetic-cross-device") >= 128);
        Assert.True(await Publish(device, $"kiosk/{hardware}/cmd", "synthetic-own-command") >= 128);
        Assert.True(await Publish(device, "$CONTROL/dynamic-security/v1", "{}") >= 128);

        // The same file-authenticated legacy connection still works after adding a dynsec client.
        var oldEvent = Guid.NewGuid().ToString("N");
        var receivedOldEvent = Receive(backend, $"kiosk/{legacyHardware}/evt", oldEvent);
        Assert.True(await Publish(legacy, $"kiosk/{legacyHardware}/evt", oldEvent) < 128);
        await receivedOldEvent.WaitAsync(TimeSpan.FromSeconds(5));
        var oldCommand = Guid.NewGuid().ToString("N");
        var receivedOldCommand = Receive(legacy, $"kiosk/{legacyHardware}/cmd", oldCommand);
        Assert.True(await Publish(backend, $"kiosk/{legacyHardware}/cmd", oldCommand) < 128);
        await receivedOldCommand.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [SyntheticDynsecFact]
    public async Task RetryDoesNotDisconnectTheBoardOrRotateItsPassword()
    {
        var fixture = Fixture.Load();
        var kiosk = Guid.NewGuid();
        var hardware = Hardware(kiosk);
        var password = Password();
        var provisioner = new MosquittoDynamicSecurity(fixture.Configuration());
        await provisioner.EnsureAsync(kiosk, hardware, password, CancellationToken.None);
        using var board = await fixture.Connect(hardware, password, "esp32-" + hardware);
        var disconnects = 0;
        board.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnects); return Task.CompletedTask; };
        Assert.Equal(1, await Subscribe(board, $"kiosk/{hardware}/cmd"));

        await provisioner.EnsureAsync(kiosk, hardware, password, CancellationToken.None);
        Assert.True(board.IsConnected);
        Assert.Equal(0, Volatile.Read(ref disconnects));
        using var backend = await fixture.Connect(fixture.BackendCredentialsFile);
        var command = Guid.NewGuid().ToString("N");
        var received = Receive(board, $"kiosk/{hardware}/cmd", command);
        Assert.True(await Publish(backend, $"kiosk/{hardware}/cmd", command) < 128);
        await received.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, Volatile.Read(ref disconnects));

        // A different offered secret must never overwrite an already-owned broker identity.
        var differentPassword = Password();
        var mismatch = await Assert.ThrowsAsync<Esp32ProvisioningException>(() => provisioner.EnsureAsync(kiosk, hardware, differentPassword, CancellationToken.None));
        Assert.Equal("ESP32_MQTT_DEVICE_VERIFICATION_FAILED", mismatch.Code);
        Assert.True(board.IsConnected);
        Assert.Equal(0, Volatile.Read(ref disconnects));
        using var reconnect = await fixture.Connect(hardware, password, "original-password-probe-" + Guid.NewGuid().ToString("N"));
        Assert.True(reconnect.IsConnected);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var rejected = await fixture.Connect(hardware, differentPassword, "wrong-password-probe-" + Guid.NewGuid().ToString("N"));
        });
    }

    [SyntheticDynsecFact]
    public async Task ReservedLegacyUsernameCannotBeShadowedByANewDynamicIdentity()
    {
        var fixture = Fixture.Load();
        var legacyHardware = fixture.Username(fixture.LegacyCredentialsFile);
        using var legacy = await fixture.Connect(fixture.LegacyCredentialsFile);
        Assert.Equal(1, await Subscribe(legacy, $"kiosk/{legacyHardware}/cmd"));
        var kiosk = Guid.NewGuid();
        var conflict = await Assert.ThrowsAsync<Esp32ProvisioningException>(() => new MosquittoDynamicSecurity(fixture.Configuration())
            .EnsureAsync(kiosk, legacyHardware, Password(), CancellationToken.None));
        Assert.Equal("ESP32_MQTT_OWNERSHIP_CONFLICT", conflict.Code);
        Assert.False(conflict.Retryable);
        var client = await fixture.Control(new() { ["command"] = "getClient", ["username"] = legacyHardware });
        Assert.True(client.TryGetProperty("error", out _));
        var role = await fixture.Control(new() { ["command"] = "getRole", ["rolename"] = "florabot-device-" + kiosk.ToString("N") });
        Assert.True(role.TryGetProperty("error", out _));
        using var reconnect = await fixture.Connect(fixture.LegacyCredentialsFile);
        Assert.True(reconnect.IsConnected);
        Assert.True(await Publish(reconnect, $"kiosk/{legacyHardware}/evt", "synthetic-legacy-still-authorized") < 128);
    }

    [SyntheticDynsecFact]
    public async Task ForeignClientMarkerIsRejectedWithoutChangingItsCredentialsOrDescription()
    {
        var fixture = Fixture.Load();
        var kiosk = Guid.NewGuid();
        var hardware = Hardware(kiosk);
        var password = Password();
        const string foreignMarker = "Owned-by-another-synthetic-system";
        Success(await fixture.Control(new()
        {
            ["command"] = "createClient", ["username"] = hardware, ["password"] = password,
            ["textdescription"] = foreignMarker
        }));

        var conflict = await Assert.ThrowsAsync<Esp32ProvisioningException>(() => new MosquittoDynamicSecurity(fixture.Configuration())
            .EnsureAsync(kiosk, hardware, Password(), CancellationToken.None));
        Assert.Equal("ESP32_MQTT_OWNERSHIP_CONFLICT", conflict.Code);
        Assert.False(conflict.Retryable);
        var disableConflict = await Assert.ThrowsAsync<Esp32ProvisioningException>(() => new MosquittoDynamicSecurity(fixture.Configuration())
            .DisableAsync(kiosk, hardware, CancellationToken.None));
        Assert.Equal("ESP32_MQTT_OWNERSHIP_CONFLICT", disableConflict.Code);
        Assert.False(disableConflict.Retryable);
        var client = await fixture.Control(new() { ["command"] = "getClient", ["username"] = hardware });
        Success(client);
        Assert.Equal(foreignMarker, client.GetProperty("data").GetProperty("client").GetProperty("textdescription").GetString());
        using var original = await fixture.Connect(hardware, password, "foreign-owner-probe-" + Guid.NewGuid().ToString("N"));
        Assert.True(original.IsConnected);
    }

    [SyntheticDynsecFact]
    public async Task ForeignRoleMarkerIsRejectedBeforeCreatingTheDeviceClient()
    {
        var fixture = Fixture.Load();
        var kiosk = Guid.NewGuid();
        var hardware = Hardware(kiosk);
        var role = "florabot-device-" + kiosk.ToString("N");
        const string foreignMarker = "Another-synthetic-role-owner";
        Success(await fixture.Control(new()
        {
            ["command"] = "createRole", ["rolename"] = role, ["textdescription"] = foreignMarker
        }));
        var conflict = await Assert.ThrowsAsync<Esp32ProvisioningException>(() => new MosquittoDynamicSecurity(fixture.Configuration())
            .EnsureAsync(kiosk, hardware, Password(), CancellationToken.None));
        Assert.Equal("ESP32_MQTT_OWNERSHIP_CONFLICT", conflict.Code);
        Assert.False(conflict.Retryable);
        var client = await fixture.Control(new() { ["command"] = "getClient", ["username"] = hardware });
        Assert.True(client.TryGetProperty("error", out _));
        var savedRole = await fixture.Control(new() { ["command"] = "getRole", ["rolename"] = role });
        Success(savedRole);
        Assert.Equal(foreignMarker, savedRole.GetProperty("data").GetProperty("role").GetProperty("textdescription").GetString());
    }

    [SyntheticDynsecFact]
    public async Task ProvisioningRejectsAnUntrustedBrokerCertificateBeforeCreatingAnyIdentity()
    {
        var fixture = Fixture.Load();
        using var knownGood = await fixture.Connect(fixture.AdminCredentialsFile);
        Assert.True(knownGood.IsConnected);
        var kiosk = Guid.NewGuid();
        var hardware = Hardware(kiosk);
        var failure = await Assert.ThrowsAsync<Esp32ProvisioningException>(() => new MosquittoDynamicSecurity(fixture.Configuration(fixture.WrongCaFile))
            .EnsureAsync(kiosk, hardware, Password(), CancellationToken.None));
        Assert.Equal("ESP32_MQTT_PROVISIONING_UNAVAILABLE", failure.Code);
        var client = await fixture.Control(new() { ["command"] = "getClient", ["username"] = hardware });
        Assert.True(client.TryGetProperty("error", out _));
    }

    [SyntheticDynsecFact]
    public async Task DisableIsIdempotentRevokesTheBoardAndNeverAllowsEnsureToReenableIt()
    {
        var fixture = Fixture.Load();
        var kiosk = Guid.NewGuid();
        var hardware = Hardware(kiosk);
        var password = Password();
        var provisioner = new MosquittoDynamicSecurity(fixture.Configuration());
        // An absent identity is already disabled; this must not create a client or role.
        await provisioner.DisableAsync(kiosk, hardware, CancellationToken.None);
        var absent = await fixture.Control(new() { ["command"] = "getClient", ["username"] = hardware });
        Assert.True(absent.TryGetProperty("error", out _));
        await provisioner.EnsureAsync(kiosk, hardware, password, CancellationToken.None);
        using var board = await fixture.Connect(hardware, password, "esp32-" + hardware);
        Assert.Equal(1, await Subscribe(board, $"kiosk/{hardware}/cmd"));
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        board.DisconnectedAsync += _ => { disconnected.TrySetResult(); return Task.CompletedTask; };
        await provisioner.DisableAsync(kiosk, hardware, CancellationToken.None);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await provisioner.DisableAsync(kiosk, hardware, CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var rejected = await fixture.Connect(hardware, password, "disabled-probe-" + Guid.NewGuid().ToString("N"));
        });
        var conflict = await Assert.ThrowsAsync<Esp32ProvisioningException>(() =>
            provisioner.EnsureAsync(kiosk, hardware, password, CancellationToken.None));
        Assert.Equal("ESP32_MQTT_OWNERSHIP_CONFLICT", conflict.Code);
        Assert.False(conflict.Retryable);
        var client = await fixture.Control(new() { ["command"] = "getClient", ["username"] = hardware });
        Success(client);
        Assert.True(client.GetProperty("data").GetProperty("client").GetProperty("disabled").GetBoolean());
    }

    private static string Hardware(Guid kiosk) => "ESP32-E2E-" + kiosk.ToString("N");
    private static string Password() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private static void Success(JsonElement result) => Assert.False(result.TryGetProperty("error", out _), "Synthetic broker control command failed.");

    private static async Task<int> Subscribe(IMqttClient client, string topic)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter =>
            filter.WithTopic(topic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)).Build(), timeout.Token);
        return (int)Assert.Single(result.Items).ResultCode;
    }

    private static async Task<int> Publish(IMqttClient client, string topic, string payload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(false).Build(), timeout.Token);
        return (int)result.ReasonCode;
    }

    private static Task Receive(IMqttClient client, string topic, string payload)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += args =>
        {
            if (args.ApplicationMessage.Topic == topic && args.ApplicationMessage.ConvertPayloadToString() == payload)
                result.TrySetResult();
            return Task.CompletedTask;
        };
        return result.Task;
    }

    private sealed record Fixture(string FixtureKind, string Host, int Port, string CaFile, string WrongCaFile,
        string AdminCredentialsFile, string LegacyCredentialsFile, string BackendCredentialsFile)
    {
        public static Fixture Load()
        {
            var path = Environment.GetEnvironmentVariable("FLORABOT_DYNSEC_TEST_CONFIG");
            Assert.False(string.IsNullOrWhiteSpace(path), "FLORABOT_DYNSEC_TEST_CONFIG must point to the isolated fixture manifest.");
            var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path!), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal("florabot-esp32-dynsec-synthetic-v1", fixture.FixtureKind);
            Assert.Equal("localhost", fixture.Host);
            Assert.Equal(58985, fixture.Port);
            return fixture;
        }

        public IConfiguration Configuration(string? caFile = null) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MQTT_HOST"] = Host, ["MQTT_PORT"] = Port.ToString(), ["MQTT_CA_FILE"] = caFile ?? CaFile,
            ["MQTT_PROVISIONING_ADMIN_FILE"] = AdminCredentialsFile
        }).Build();

        public string Username(string credentialsFile)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(credentialsFile));
            return document.RootElement.GetProperty("username").GetString()!;
        }

        public async Task<IMqttClient> Connect(string credentialsFile)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(credentialsFile));
            return await Connect(document.RootElement.GetProperty("username").GetString()!,
                document.RootElement.GetProperty("password").GetString()!, "synthetic-test-" + Guid.NewGuid().ToString("N"));
        }

        public async Task<IMqttClient> Connect(string username, string password, string clientId)
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(CaFile);
            var client = new MqttClientFactory().CreateMqttClient();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var options = new MqttClientOptionsBuilder().WithTcpServer(Host, Port).WithClientId(clientId)
                    .WithCredentials(username, password).WithProtocolVersion(MqttProtocolVersion.V500)
                    .WithCleanSession(true).WithTimeout(TimeSpan.FromSeconds(5))
                    .WithTlsOptions(tls => tls.UseTls().WithTrustChain(new X509Certificate2Collection(certificate))).Build();
                var response = await client.ConnectAsync(options, timeout.Token);
                if ((int)response.ResultCode != 0) throw new InvalidOperationException("Synthetic broker authentication was rejected.");
                return client;
            }
            catch { client.Dispose(); throw; }
        }

        public async Task<JsonElement> Control(Dictionary<string, object> command)
        {
            using var admin = await Connect(AdminCredentialsFile);
            const string responseTopic = "$CONTROL/dynamic-security/v1/response";
            var correlation = Guid.NewGuid().ToString("N");
            command["correlationData"] = correlation;
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            admin.ApplicationMessageReceivedAsync += args =>
            {
                if (args.ApplicationMessage.Topic != responseTopic) return Task.CompletedTask;
                using var json = JsonDocument.Parse(args.ApplicationMessage.ConvertPayloadToString());
                foreach (var response in json.RootElement.GetProperty("responses").EnumerateArray())
                    if (response.TryGetProperty("correlationData", out var id) && id.GetString() == correlation)
                        completion.TrySetResult(response.Clone());
                return Task.CompletedTask;
            };
            Assert.Equal(1, await Subscribe(admin, responseTopic));
            Assert.True(await Publish(admin, "$CONTROL/dynamic-security/v1", JsonSerializer.Serialize(new { commands = new[] { command } })) < 128);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
