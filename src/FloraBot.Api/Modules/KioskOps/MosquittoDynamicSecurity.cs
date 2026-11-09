using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace FloraBot.Api.Modules.KioskOps;

public interface IMqttDeviceProvisioner
{
    Task EnsureAsync(Guid kioskId, string hardwareId, string mqttPassword, CancellationToken ct);
    Task DisableAsync(Guid kioskId, string hardwareId, CancellationToken ct);
}

/// <summary>Only bounded error codes may cross the broker/secret-store boundary.</summary>
public sealed class Esp32ProvisioningException(string code, bool retryable = true) : Exception(code)
{
    public string Code { get; } = code;
    public string ErrorCode => Code;
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Mosquitto 2.1.2 Dynamic Security control API. Preserves legacy password/ACL files and
/// never resets an existing identity, changes global defaults, or controls the OS service.
/// </summary>
public sealed class MosquittoDynamicSecurity(IConfiguration configuration) : IMqttDeviceProvisioner
{
    private const string ControlTopic = "$CONTROL/dynamic-security/v1";
    private const string ResponseTopic = ControlTopic + "/response";
    private const string Unavailable = "ESP32_MQTT_PROVISIONING_UNAVAILABLE";
    private const string Conflict = "ESP32_MQTT_OWNERSHIP_CONFLICT";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);

    public async Task EnsureAsync(Guid kioskId, string hardwareId, string mqttPassword, CancellationToken ct)
    {
        if (kioskId == Guid.Empty || !DeviceProtocol.ValidHardwareId(hardwareId)
            || string.IsNullOrEmpty(mqttPassword) || mqttPassword.Length > 4096 || mqttPassword.Contains('\0'))
            throw new Esp32ProvisioningException("ESP32_MQTT_IDENTITY_INVALID", false);
        try
        {
            var settings = await LoadAsync(ct);
            if (hardwareId == "florabot-backend" || hardwareId == settings.Username || settings.ReservedUsernames.Contains(hardwareId))
                throw new Esp32ProvisioningException(Conflict, false);
            using var ca = X509CertificateLoader.LoadCertificateFromFile(settings.CaFile);
            using var client = new MqttClientFactory().CreateMqttClient();
            await ConnectControlAsync(client, settings, ca, ct);

            var marker = $"FloraBot:{kioskId:N}:{hardwareId}";
            var roleName = "florabot-device-" + kioskId.ToString("N");
            var expectedAcls = DeviceAcls(hardwareId);

            // Inspect both resources before any mutation. An existing foreign client must not even
            // cause creation of a role, and an unknown role must never be silently adopted.
            var existingClient = await GetAsync(client, "getClient", "username", hardwareId, "client", "Client not found", ct);
            if (existingClient.HasValue) RequireClient(existingClient.Value, hardwareId, roleName, marker);
            var existingRole = await GetAsync(client, "getRole", "rolename", roleName, "role", "Role not found", ct);
            if (existingRole.HasValue) RequireRole(existingRole.Value, roleName, marker, expectedAcls);

            if (!existingRole.HasValue)
            {
                await CreateAsync(client, new Dictionary<string, object?>
                {
                    ["command"] = "createRole", ["rolename"] = roleName, ["textdescription"] = marker,
                    ["allowwildcardsubs"] = false, ["acls"] = expectedAcls
                }, "Role already exists", ct);
                existingRole = await GetAsync(client, "getRole", "rolename", roleName, "role", "Role not found", ct);
                if (!existingRole.HasValue) throw new Esp32ProvisioningException(Unavailable);
                RequireRole(existingRole.Value, roleName, marker, expectedAcls);
            }

            if (!existingClient.HasValue)
            {
                await CreateAsync(client, new Dictionary<string, object?>
                {
                    ["command"] = "createClient", ["username"] = hardwareId, ["password"] = mqttPassword,
                    ["textdescription"] = marker, ["groups"] = Array.Empty<object>(),
                    ["roles"] = new[] { new { rolename = roleName, priority = 0 } }
                    // Deliberately no clientid binding: a unique verification client must not kick
                    // an already connected board during a retry. Username and exact ACLs isolate it.
                }, "Client already exists", ct);
                existingClient = await GetAsync(client, "getClient", "username", hardwareId, "client", "Client not found", ct);
                if (!existingClient.HasValue) throw new Esp32ProvisioningException(Unavailable);
                RequireClient(existingClient.Value, hardwareId, roleName, marker);
            }

            // No modifyClient/setClientPassword/modifyRole commands are used on idempotent retries.
            // The real TLS login verifies the supplied stored password without disclosing a hash.
            await VerifyDeviceAsync(settings, ca, hardwareId, mqttPassword, ct);
            await DisconnectAsync(client);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Esp32ProvisioningException) { throw; }
        catch (Exception)
        {
            // MQTT/JSON/crypto/IO exceptions may contain broker reason strings, paths or payload
            // excerpts. Never attach them as inner exceptions, log them, or return their messages.
            throw new Esp32ProvisioningException(Unavailable);
        }
    }

    public async Task DisableAsync(Guid kioskId, string hardwareId, CancellationToken ct)
    {
        if (kioskId == Guid.Empty || !DeviceProtocol.ValidHardwareId(hardwareId))
            throw new Esp32ProvisioningException("ESP32_MQTT_IDENTITY_INVALID", false);
        try
        {
            var settings = await LoadAsync(ct);
            using var ca = X509CertificateLoader.LoadCertificateFromFile(settings.CaFile);
            using var client = new MqttClientFactory().CreateMqttClient();
            await ConnectControlAsync(client, settings, ca, ct);
            var existing = await GetAsync(client, "getClient", "username", hardwareId, "client", "Client not found", ct);
            if (existing.HasValue)
            {
                var marker = $"FloraBot:{kioskId:N}:{hardwareId}";
                var roleName = "florabot-device-" + kioskId.ToString("N");
                RequireClient(existing.Value, hardwareId, roleName, marker, allowDisabled: true);
                var role = await GetAsync(client, "getRole", "rolename", roleName, "role", "Role not found", ct);
                if (!role.HasValue) throw new Esp32ProvisioningException(Conflict, false);
                RequireRole(role.Value, roleName, marker, DeviceAcls(hardwareId));
                if (!existing.Value.TryGetProperty("disabled", out var disabled) || disabled.ValueKind != JsonValueKind.True)
                {
                    var result = await CommandAsync(client, new Dictionary<string, object?>
                        { ["command"] = "disableClient", ["username"] = hardwareId }, ct);
                    var error = Error(result);
                    if (error is not null && error != "Client not found") throw new Esp32ProvisioningException(Unavailable);
                    var verified = await GetAsync(client, "getClient", "username", hardwareId, "client", "Client not found", ct);
                    if (verified.HasValue)
                    {
                        RequireClient(verified.Value, hardwareId, roleName, marker, allowDisabled: true);
                        if (!verified.Value.TryGetProperty("disabled", out disabled) || disabled.ValueKind != JsonValueKind.True)
                            throw new Esp32ProvisioningException(Unavailable);
                    }
                }
            }
            await DisconnectAsync(client);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Esp32ProvisioningException) { throw; }
        catch (Exception) { throw new Esp32ProvisioningException(Unavailable); }
    }

    private static object[] DeviceAcls(string hardware)
    {
        var command = $"kiosk/{hardware}/cmd";
        var events = $"kiosk/{hardware}/evt";
        var acls = new List<object>
        {
            new { acltype = "publishClientSend", topic = events, priority = 100, allow = true },
            new { acltype = "publishClientReceive", topic = command, priority = 100, allow = true },
            new { acltype = "subscribeLiteral", topic = command, priority = 100, allow = true },
            new { acltype = "unsubscribeLiteral", topic = command, priority = 100, allow = true }
        };
        // An unmatched dynsec ACL can defer to legacy acl_file. Explicit deny rules stop that
        // fallback for managed clients. MQTT # does not cover $ topics, so deny control/sys too.
        foreach (var kind in new[] { "publishClientSend", "publishClientReceive", "subscribePattern", "unsubscribePattern" })
            foreach (var topic in new[] { "#", "$CONTROL/#", "$SYS/#" })
                acls.Add(new { acltype = kind, topic, priority = 0, allow = false });
        return acls.ToArray();
    }

    private static void RequireClient(JsonElement client, string hardware, string role, string marker, bool allowDisabled = false)
    {
        if (!StringEquals(client, "username", hardware) || !StringEquals(client, "textdescription", marker)
            || client.TryGetProperty("clientid", out var clientId) && clientId.ValueKind != JsonValueKind.Null && clientId.GetString() is not (null or "")
            || client.TryGetProperty("disabled", out var disabled)
                && disabled.ValueKind != JsonValueKind.False && !(allowDisabled && disabled.ValueKind == JsonValueKind.True)
            || !client.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() != 0
            || !client.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array || roles.GetArrayLength() != 1
            || !StringEquals(roles[0], "rolename", role) || !IntegerEquals(roles[0], "priority", 0))
            throw new Esp32ProvisioningException(Conflict, false);
    }

    private static void RequireRole(JsonElement role, string name, string marker, object[] expectedAcls)
    {
        if (!StringEquals(role, "rolename", name) || !StringEquals(role, "textdescription", marker)
            || !role.TryGetProperty("allowwildcardsubs", out var wildcards) || wildcards.ValueKind != JsonValueKind.False
            || !role.TryGetProperty("acls", out var acls) || acls.ValueKind != JsonValueKind.Array)
            throw new Esp32ProvisioningException(Conflict, false);
        using var expected = JsonDocument.Parse(JsonSerializer.Serialize(expectedAcls));
        var expectedSet = expected.RootElement.EnumerateArray().Select(AclIdentity).ToHashSet(StringComparer.Ordinal);
        var actualList = acls.EnumerateArray().Select(AclIdentity).ToArray();
        if (actualList.Length != expectedSet.Count || !expectedSet.SetEquals(actualList))
            throw new Esp32ProvisioningException(Conflict, false);
    }

    private static string AclIdentity(JsonElement acl)
    {
        if (acl.ValueKind != JsonValueKind.Object
            || !acl.TryGetProperty("acltype", out var kind) || kind.ValueKind != JsonValueKind.String
            || !acl.TryGetProperty("topic", out var topic) || topic.ValueKind != JsonValueKind.String
            || !acl.TryGetProperty("priority", out var priority) || priority.ValueKind != JsonValueKind.Number || !priority.TryGetInt32(out var value)
            || !acl.TryGetProperty("allow", out var allow) || allow.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new Esp32ProvisioningException(Conflict, false);
        return string.Join('\n', kind.GetString(), topic.GetString(), value.ToString(System.Globalization.CultureInfo.InvariantCulture), allow.GetBoolean());
    }

    private static bool StringEquals(JsonElement value, string property, string expected) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item)
        && item.ValueKind == JsonValueKind.String && item.GetString() == expected;

    private static bool IntegerEquals(JsonElement value, string property, int expected) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item)
        && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var actual) && actual == expected;

    private static async Task<JsonElement?> GetAsync(IMqttClient client, string command, string key, string value,
        string resultKey, string missingError, CancellationToken ct)
    {
        var result = await CommandAsync(client, new Dictionary<string, object?> { ["command"] = command, [key] = value }, ct);
        var error = Error(result);
        if (error == missingError) return null;
        if (error is not null || !result.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(resultKey, out var item)
            || item.ValueKind != JsonValueKind.Object) throw new Esp32ProvisioningException(Unavailable);
        return item.Clone();
    }

    private static async Task CreateAsync(IMqttClient client, Dictionary<string, object?> command, string existsError, CancellationToken ct)
    {
        var result = await CommandAsync(client, command, ct);
        var error = Error(result);
        if (error is not null && error != existsError) throw new Esp32ProvisioningException(Unavailable);
        // A simultaneous creator may have won. The caller must re-read and verify ownership.
    }

    private static string? Error(JsonElement response) => response.TryGetProperty("error", out var error)
        ? error.ValueKind == JsonValueKind.String ? error.GetString() : "invalid" : null;

    private static async Task<JsonElement> CommandAsync(IMqttClient client, Dictionary<string, object?> command, CancellationToken ct)
    {
        var correlation = Guid.NewGuid().ToString("N");
        command["correlationData"] = correlation;
        var expected = (string)command["command"]!;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Receive(MqttApplicationMessageReceivedEventArgs args)
        {
            if (args.ApplicationMessage.Topic != ResponseTopic || args.ApplicationMessage.Retain
                || args.ApplicationMessage.Payload.Length > 65536) return Task.CompletedTask;
            try
            {
                using var json = JsonDocument.Parse(args.ApplicationMessage.ConvertPayloadToString());
                if (json.RootElement.ValueKind != JsonValueKind.Object
                    || !json.RootElement.TryGetProperty("responses", out var responses) || responses.ValueKind != JsonValueKind.Array)
                    return Task.CompletedTask;
                foreach (var response in responses.EnumerateArray())
                {
                    if (!StringEquals(response, "correlationData", correlation)) continue;
                    if (responses.GetArrayLength() != 1 || !StringEquals(response, "command", expected))
                        completion.TrySetException(new Esp32ProvisioningException(Unavailable));
                    else completion.TrySetResult(response.Clone());
                }
            }
            catch (JsonException) { }
            return Task.CompletedTask;
        }
        client.ApplicationMessageReceivedAsync += Receive;
        try
        {
            var packet = new MqttApplicationMessageBuilder().WithTopic(ControlTopic)
                .WithPayload(JsonSerializer.Serialize(new { commands = new[] { command } }))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(false).Build();
            var published = await client.PublishAsync(packet, ct);
            if ((int)published.ReasonCode >= 128) throw new Esp32ProvisioningException(Unavailable);
            return await completion.Task.WaitAsync(CommandTimeout, ct);
        }
        finally { client.ApplicationMessageReceivedAsync -= Receive; }
    }

    private static async Task VerifyDeviceAsync(Settings settings, X509Certificate2 ca, string hardware, string password, CancellationToken ct)
    {
        try
        {
            using var device = new MqttClientFactory().CreateMqttClient();
            await device.ConnectAsync(Options(settings, ca, hardware, password, "florabot-probe-" + Guid.NewGuid().ToString("N")), ct);
            var subscribed = await device.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter => filter.WithTopic($"kiosk/{hardware}/cmd").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)).Build(), ct);
            if (subscribed.Items.Count != 1 || (int)subscribed.Items.Single().ResultCode != 1)
                throw new Esp32ProvisioningException("ESP32_MQTT_DEVICE_VERIFICATION_FAILED");
            // No command, event or synthetic heartbeat is published by this probe.
            await DisconnectAsync(device);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new Esp32ProvisioningException("ESP32_MQTT_DEVICE_VERIFICATION_FAILED"); }
    }

    private static async Task ConnectControlAsync(IMqttClient client, Settings settings, X509Certificate2 ca, CancellationToken ct)
    {
        await client.ConnectAsync(Options(settings, ca, settings.Username, settings.Password,
            "florabot-provision-" + Guid.NewGuid().ToString("N")), ct);
        var subscribed = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(filter => filter.WithTopic(ResponseTopic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)).Build(), ct);
        if (subscribed.Items.Count != 1 || (int)subscribed.Items.Single().ResultCode != 1)
            throw new Esp32ProvisioningException(Unavailable);
    }

    private static MqttClientOptions Options(Settings settings, X509Certificate2 ca, string username, string password, string clientId) =>
        new MqttClientOptionsBuilder().WithClientId(clientId).WithTcpServer(settings.Host, settings.Port)
            .WithCredentials(username, password).WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanSession(true).WithSessionExpiryInterval(0).WithTimeout(CommandTimeout)
            .WithTlsOptions(tls => tls.UseTls().WithTrustChain(new X509Certificate2Collection(ca))).Build();

    private static async Task DisconnectAsync(IMqttClient client)
    {
        // A missing DISCONNECT acknowledgement cannot invalidate already verified provisioning.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (client.IsConnected) await client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token);
        }
        catch (Exception) { }
    }

    private async Task<Settings> LoadAsync(CancellationToken ct)
    {
        try
        {
            var host = configuration["MQTT_HOST"];
            var portText = configuration["MQTT_PORT"] ?? "8883";
            var ca = configuration["MQTT_CA_FILE"];
            var file = configuration["MQTT_PROVISIONING_ADMIN_FILE"];
            if (string.IsNullOrWhiteSpace(host) || !int.TryParse(portText, out var port) || port is < 1 or > 65535
                || string.IsNullOrWhiteSpace(ca) || string.IsNullOrWhiteSpace(file))
                throw new Esp32ProvisioningException("ESP32_MQTT_PROVISIONER_NOT_CONFIGURED");
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[1024 * 1024 + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), ct);
                if (read == 0) break;
                count += read;
            }
            if (count > 1024 * 1024) throw new Esp32ProvisioningException("ESP32_MQTT_PROVISIONER_NOT_CONFIGURED");
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
            if (!json.RootElement.TryGetProperty("username", out var username) || username.ValueKind != JsonValueKind.String
                || !json.RootElement.TryGetProperty("password", out var password) || password.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(username.GetString()) || username.GetString()!.Length > 128 || username.GetString()!.Any(char.IsControl)
                || string.IsNullOrEmpty(password.GetString()) || password.GetString()!.Length > 4096 || password.GetString()!.Contains('\0')
                || !json.RootElement.TryGetProperty("reservedUsernames", out var reserved)
                || reserved.ValueKind != JsonValueKind.Array || reserved.GetArrayLength() > 10000)
                throw new Esp32ProvisioningException("ESP32_MQTT_PROVISIONER_NOT_CONFIGURED");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in reserved.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()) || item.GetString()!.Any(char.IsControl))
                    throw new Esp32ProvisioningException("ESP32_MQTT_PROVISIONER_NOT_CONFIGURED");
                names.Add(item.GetString()!);
            }
            return new Settings(host, port, ca, username.GetString()!, password.GetString()!, names);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new Esp32ProvisioningException("ESP32_MQTT_PROVISIONER_NOT_CONFIGURED"); }
    }

    private sealed class Settings(string host, int port, string caFile, string username, string password, HashSet<string> reservedUsernames)
    {
        public string Host { get; } = host;
        public int Port { get; } = port;
        public string CaFile { get; } = caFile;
        public string Username { get; } = username;
        public string Password { get; } = password;
        public HashSet<string> ReservedUsernames { get; } = reservedUsernames;
        public override string ToString() => "[MQTT provisioner settings redacted]";
    }
}
