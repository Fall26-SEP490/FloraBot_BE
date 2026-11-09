using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record Esp32BrokerConfiguration(string Host, int TlsPort, string CaCertificatePem);

// Secret values deliberately have no public serializable properties or generated record ToString.
public sealed class Esp32DeviceSecrets(string mqttPassword, string hmacKeyHex)
{
    internal string MqttPassword { get; } = mqttPassword;
    internal string HmacKeyHex { get; } = hmacKeyHex;
    public override string ToString() => "[ESP32 device secrets redacted]";
}

public sealed record Esp32SecretResult(Esp32DeviceSecrets? Secrets, string? ErrorCode);

public interface IEsp32ConfigurationSource
{
    Task<Esp32BrokerConfiguration?> ReadBrokerAsync(CancellationToken ct);
    Task<Esp32SecretResult> ReadSecretsAsync(string hardwareId, CancellationToken ct);
}

/// <summary>Public board-facing TLS settings and the existing hardware-keyed server secret file.</summary>
public sealed class Esp32ConfigurationSource(IConfiguration configuration, IServiceProvider services) : IEsp32ConfigurationSource
{
    public async Task<Esp32BrokerConfiguration?> ReadBrokerAsync(CancellationToken ct)
    {
        // The backend's MQTT_HOST may be localhost or a container-only DNS name. Never reuse it
        // implicitly for a physical board. The operator must configure the external TLS endpoint.
        var host = configuration["ESP32_MQTT_HOST"]?.Trim();
        var portText = configuration["ESP32_MQTT_PORT"] ?? "8883";
        var caFile = configuration["ESP32_MQTT_CA_FILE"];
        if (!ValidBoardHost(host) || !int.TryParse(portText, out var port) || port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(caFile)) return null;
        try
        {
            var pem = await ReadBoundedAsync(caFile, 65536, ct);
            using var certificate = X509Certificate2.CreateFromPem(pem);
            // Return only the public certificate even if an operator accidentally provides a PEM
            // bundle containing extra text or private key blocks.
            return new(host!, port, certificate.ExportCertificatePem() + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<Esp32SecretResult> ReadSecretsAsync(string hardwareId, CancellationToken ct)
    {
        const string missing = "ESP32_CONFIG_NOT_PROVISIONED";
        if (!DeviceProtocol.ValidHardwareId(hardwareId)) return new(null, missing);
        var ring = services.GetService<DeviceKeyRing>();
        var provisioning = services.GetService<Esp32ProvisioningStore>();
        if (provisioning is not null && await provisioning.GetStatusByHardwareAsync(hardwareId, ct) is { } status)
        {
            if (status.State != "READY") return new(null, status.State == "FAILED"
                ? "ESP32_CONFIG_PROVISIONING_FAILED" : "ESP32_CONFIG_NOT_READY");
            try
            {
                var saved = await provisioning.GetReadySecretsAsync(hardwareId, ct);
                if (saved is null || ring is null) return new(null, missing);
                var savedKey = Convert.FromHexString(saved.HmacKeyHex);
                try
                {
                    if (!ring.AddOrVerify(hardwareId, savedKey)) return new(null, "ESP32_CONFIG_HMAC_MISMATCH");
                }
                finally { CryptographicOperations.ZeroMemory(savedKey); }
                return new(saved, null);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                return new(null, missing);
            }
            catch (Esp32ProvisioningException)
            {
                return new(null, missing);
            }
        }
        if (ring is null || !ring.HardwareIds.Contains(hardwareId, StringComparer.Ordinal)) return new(null, missing);
        var file = configuration["ESP32_CREDENTIALS_FILE"] ?? configuration["MQTT_CREDENTIALS_FILE"];
        if (string.IsNullOrWhiteSpace(file)) return new(null, missing);
        try
        {
            using var json = JsonDocument.Parse(await ReadBoundedAsync(file, 1024 * 1024, ct));
            if (!json.RootElement.TryGetProperty(hardwareId, out var device)
                || !device.TryGetProperty("password", out var passwordValue)
                || !device.TryGetProperty("hmacKey", out var keyValue)
                || passwordValue.ValueKind != JsonValueKind.String || keyValue.ValueKind != JsonValueKind.String)
                return new(null, missing);
            var password = passwordValue.GetString();
            var keyHex = keyValue.GetString();
            // Firmware and the broker treat these as NUL-terminated UTF-8 strings.
            if (string.IsNullOrEmpty(password) || password.Length > 4096 || password.Contains('\0')
                || keyHex is not { Length: 64 } || !keyHex.All(char.IsAsciiHexDigit)) return new(null, missing);
            var key = Convert.FromHexString(keyHex);
            try
            {
                // Compare against the in-memory signing key actually used by MQTT, not a second
                // reload of the same file. This also detects a pending rotation after process start.
                if (!MatchesBackendKey(ring, hardwareId, key)) return new(null, "ESP32_CONFIG_HMAC_MISMATCH");
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            return new(new(password, keyHex.ToLowerInvariant()), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            // File paths, parser excerpts and key material must never appear in errors/telemetry.
            return new(null, missing);
        }
    }

    public static bool MatchesBackendKey(DeviceKeyRing ring, string hardwareId, byte[] candidate)
    {
        if (candidate.Length != 32 || !ring.HardwareIds.Contains(hardwareId, StringComparer.Ordinal)) return false;
        var probe = new UnlockCommand(1, hardwareId,
            Guid.Parse("ffffffff-ffff-4fff-8fff-fffffffffff1"),
            Guid.Parse("ffffffff-ffff-4fff-8fff-fffffffffff2"),
            0, "CUSTOMER_PICKUP", 1, 2, "");
        var expected = ring.Sign(probe).Signature;
        var actual = DeviceProtocol.Sign(probe, candidate).Signature;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    }

    public static bool ValidBoardHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(char.IsControl)
            || host.TrimEnd('.').Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.TrimEnd('.').EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IPAddress.TryParse(host, out var ip))
            return !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any)
                && !ip.Equals(IPAddress.IPv6None) && !ip.Equals(IPAddress.Broadcast);
        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    private static async Task<string> ReadBoundedAsync(string path, int maximumBytes, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) throw new IOException("Configuration file exceeds size limit.");
        var bytes = new byte[maximumBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
        }
        if (length > maximumBytes) throw new IOException("Configuration file exceeds size limit.");
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
