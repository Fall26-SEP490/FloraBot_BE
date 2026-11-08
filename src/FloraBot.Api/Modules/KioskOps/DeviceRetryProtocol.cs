using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace FloraBot.Api.Modules.KioskOps;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UnlockRetryCommand(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("hardware_id")] string HardwareId,
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("cmd_id")] Guid CommandId,
    [property: JsonPropertyName("requested_at")] long RequestedAt,
    [property: JsonPropertyName("expires_at")] long ExpiresAt,
    [property: JsonPropertyName("signature")] string Signature)
{
    [JsonIgnore]
    public Guid CmdId => CommandId;
}

public static class DeviceRetryProtocol
{
    public const string Domain = "florabot.retry.v1";

    public static bool Valid(UnlockRetryCommand? command) =>
        command is not null
        && command.Version == 1
        && DeviceProtocol.ValidHardwareId(command.HardwareId)
        && command.RequestId != Guid.Empty
        && command.CommandId != Guid.Empty
        && command.RequestedAt > 0
        && command.ExpiresAt > command.RequestedAt
        && checked(command.ExpiresAt - command.RequestedAt) <= 30;

    public static string Canonical(UnlockRetryCommand command) => string.Join('\n',
        Domain,
        command.HardwareId,
        command.RequestId.ToString("D"),
        command.CommandId.ToString("D"),
        command.RequestedAt.ToString(CultureInfo.InvariantCulture),
        command.ExpiresAt.ToString(CultureInfo.InvariantCulture));

    public static UnlockRetryCommand Sign(UnlockRetryCommand command, byte[] key)
    {
        if (!Valid(command)) throw new ArgumentException("Invalid unlock retry command.", nameof(command));
        if (key is not { Length: 32 }) throw new ArgumentException("A 32-byte device key is required.", nameof(key));
        return command with { Signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(Canonical(command)))) };
    }

    public static bool Verify(UnlockRetryCommand? command, string? hardware, byte[]? key, DateTimeOffset now)
    {
        if (command is null || !Valid(command)) return false;
        if (string.IsNullOrEmpty(hardware) || command.HardwareId != hardware) return false;
        if (key is not { Length: 32 }) return false;
        if (command.Signature is not { Length: 64 } || !command.Signature.All(Uri.IsHexDigit)) return false;

        var nowSec = now.ToUnixTimeSeconds();
        if (nowSec < command.RequestedAt || nowSec >= command.ExpiresAt) return false;

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(command.Signature),
            HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(Canonical(command))));
    }
}
