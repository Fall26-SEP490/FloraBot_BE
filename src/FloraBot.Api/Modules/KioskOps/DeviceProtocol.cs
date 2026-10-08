using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record UnlockCommand(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("hardware_id")] string HardwareId,
    [property: JsonPropertyName("cmd_id")] Guid CommandId,
    [property: JsonPropertyName("slot_id")] Guid SlotId,
    [property: JsonPropertyName("relay_channel")] int RelayChannel,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("issued_at")] long IssuedAt,
    [property: JsonPropertyName("expires_at")] long ExpiresAt,
    [property: JsonPropertyName("signature")] string Signature);

public sealed record SignedDeviceEvent(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("hardware_id")] string HardwareId,
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("cmd_id")] Guid CommandId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("occurred_at")] long OccurredAt,
    [property: JsonPropertyName("signature")] string Signature);

public static class DeviceProtocol
{
    public static bool ValidHardwareId(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static UnlockCommand Sign(UnlockCommand command, byte[] key)
    {
        if (!Valid(command)) throw new ArgumentException("Invalid unlock command.", nameof(command));
        return command with { Signature = Sign(Canonical(command), key) };
    }

    public static bool Verify(UnlockCommand command, string hardware, byte[] key, DateTimeOffset now) =>
        Valid(command) && command.HardwareId == hardware
        && command.IssuedAt <= now.ToUnixTimeSeconds() && now.ToUnixTimeSeconds() < command.ExpiresAt
        && Verify(Canonical(command), command.Signature, key);

    public static SignedDeviceEvent Sign(SignedDeviceEvent message, byte[] key)
    {
        if (!Valid(message)) throw new ArgumentException("Invalid device event.", nameof(message));
        return message with { Signature = Sign(Canonical(message), key) };
    }

    public static bool Verify(SignedDeviceEvent message, string hardware, byte[] key, DateTimeOffset now) =>
        Valid(message) && message.HardwareId == hardware
        && message.OccurredAt >= now.ToUnixTimeSeconds() - 120 && message.OccurredAt <= now.ToUnixTimeSeconds() + 5
        && Verify(Canonical(message), message.Signature, key);

    private static bool Valid(UnlockCommand c) => c.Version == 1 && ValidHardwareId(c.HardwareId)
        && c.CommandId != Guid.Empty && c.SlotId != Guid.Empty && c.RelayChannel is >= 0 and <= 63
        && c.Purpose is "CUSTOMER_PICKUP" or "SELLER_ACCESS" && c.IssuedAt > 0 && c.ExpiresAt > c.IssuedAt;

    private static bool Valid(SignedDeviceEvent e) => e.Version == 1 && ValidHardwareId(e.HardwareId)
        && e.EventId != Guid.Empty && e.OccurredAt > 0
        && (e.Event == "HEARTBEAT" ? e.CommandId == Guid.Empty
            : e.CommandId != Guid.Empty && e.Event is "ACK" or "OPENED" or "CLOSED" or "FAILED");

    private static string Canonical(UnlockCommand c) => string.Join('\n', "florabot.unlock.v1", c.HardwareId,
        c.CommandId.ToString("D"), c.SlotId.ToString("D"), c.RelayChannel.ToString(CultureInfo.InvariantCulture),
        c.Purpose, c.IssuedAt.ToString(CultureInfo.InvariantCulture), c.ExpiresAt.ToString(CultureInfo.InvariantCulture));

    private static string Canonical(SignedDeviceEvent e) => string.Join('\n', "florabot.event.v1", e.HardwareId,
        e.EventId.ToString("D"), e.CommandId.ToString("D"), e.Event, e.OccurredAt.ToString(CultureInfo.InvariantCulture));

    private static string Sign(string canonical, byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("A 32-byte device key is required.", nameof(key));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(canonical)));
    }

    private static bool Verify(string canonical, string? signature, byte[] key)
    {
        if (key.Length != 32 || signature is not { Length: 64 } || !signature.All(Uri.IsHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature),
            HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(canonical)));
    }
}
