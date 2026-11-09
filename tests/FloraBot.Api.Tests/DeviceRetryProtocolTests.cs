using System.Text.Json;
using FloraBot.Api.Modules.KioskOps;

namespace FloraBot.Api.Tests;

public sealed class DeviceRetryProtocolTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset ReferenceNow = DateTimeOffset.FromUnixTimeSeconds(1800000010);

    private static UnlockRetryCommand ReferenceCommand => new(
        1,
        "ESP32-A1B2C3",
        Guid.Parse("a1b2c3d4-3333-3333-3333-333333333333"),
        Guid.Parse("abcdef01-1111-1111-1111-111111111111"),
        1800000000,
        1800000030,
        "");

    [Fact]
    public void ReferenceHexVectorMatchesIndependentPythonCalculation()
    {
        // Canonical string format: florabot.retry.v1\n<hardware>\n<request_id>\n<cmd_id>\n<requested_at>\n<expires_at>
        const string expectedCanonical =
            "florabot.retry.v1\nESP32-A1B2C3\na1b2c3d4-3333-3333-3333-333333333333\nabcdef01-1111-1111-1111-111111111111\n1800000000\n1800000030";

        Assert.Equal(expectedCanonical, DeviceRetryProtocol.Canonical(ReferenceCommand));

        var signed = DeviceRetryProtocol.Sign(ReferenceCommand, Key);

        // Independently verified HMAC-SHA256 for the canonical string with synthetic key bytes 0..31
        const string verifiedHmac = "9f2aec7aae56d638d32e4a365236147fc4a38ae2358dc6ce37d68cb507fc41e5";
        Assert.Equal(verifiedHmac, signed.Signature);

        // A signature from a different valid payload must not authorize this payload.
        const string unverifiedMismatchedHmac = "fd721badd2b45c686de98a41c02a70178ddd7f0aa831340ea060388ee4f319c7";
        Assert.NotEqual(unverifiedMismatchedHmac, signed.Signature);

        // Valid signature verifies successfully
        Assert.True(DeviceRetryProtocol.Verify(signed, signed.HardwareId, Key, ReferenceNow));

        // Command with the mismatched unverified signature fails verification
        var badSigCommand = ReferenceCommand with { Signature = unverifiedMismatchedHmac };
        Assert.False(DeviceRetryProtocol.Verify(badSigCommand, badSigCommand.HardwareId, Key, ReferenceNow));
    }

    [Fact]
    public void PublishedFirmwareVectorMatchesBackendExactly()
    {
        var command = new UnlockRetryCommand(1, "ESP32-A1B2C3",
            Guid.Parse("a1b2c3d4-e5f6-4a1b-8c2d-3e4f5a6b7c8d"),
            Guid.Parse("abcdef01-2345-4678-9abc-def012345678"),
            1800000030, 1800000060, "");
        var signed = DeviceRetryProtocol.Sign(command, Key);
        Assert.Equal("fd721badd2b45c686de98a41c02a70178ddd7f0aa831340ea060388ee4f319c7", signed.Signature);
        Assert.True(DeviceRetryProtocol.Verify(signed, command.HardwareId, Key,
            DateTimeOffset.FromUnixTimeSeconds(1800000030)));
    }

    [Fact]
    public void SecondaryIndependentPythonVectorVerifies()
    {
        var cmd2 = new UnlockRetryCommand(
            1,
            "KIOSK-002",
            Guid.Parse("12345678-1234-1234-1234-123456789abc"),
            Guid.Parse("87654321-4321-4321-4321-cba987654321"),
            1700000000,
            1700000015,
            "");

        Assert.Equal(
            "florabot.retry.v1\nKIOSK-002\n12345678-1234-1234-1234-123456789abc\n87654321-4321-4321-4321-cba987654321\n1700000000\n1700000015",
            DeviceRetryProtocol.Canonical(cmd2));

        var signed2 = DeviceRetryProtocol.Sign(cmd2, Key);
        Assert.Equal("267b8702d21f340b9c8e3fa682e39c54c055cede30f722d56cb18d47eb5b3625", signed2.Signature);
        Assert.True(DeviceRetryProtocol.Verify(signed2, signed2.HardwareId, Key, DateTimeOffset.FromUnixTimeSeconds(1700000005)));
    }

    [Fact]
    public void EveryFieldTamperFailsVerification()
    {
        var signed = DeviceRetryProtocol.Sign(ReferenceCommand, Key);

        UnlockRetryCommand[] tampered =
        [
            signed with { Version = 2 },
            signed with { HardwareId = "ESP32-D4E5F6" },
            signed with { RequestId = Guid.NewGuid() },
            signed with { CommandId = Guid.NewGuid() },
            signed with { RequestedAt = signed.RequestedAt - 1 },
            signed with { ExpiresAt = signed.ExpiresAt - 1 },
            signed with { Signature = new string('0', 64) },
            signed with { Signature = new string('f', 64) },
            signed with { Signature = new string('z', 64) },
            signed with { Signature = "" },
            signed with { Signature = "not-hex-at-all" },
            signed with { Signature = signed.Signature[..63] }
        ];

        foreach (var cmd in tampered)
        {
            Assert.False(DeviceRetryProtocol.Verify(cmd, cmd.HardwareId, Key, ReferenceNow));
        }
    }

    [Fact]
    public void WrongKeyAndHardwareRejected()
    {
        var signed = DeviceRetryProtocol.Sign(ReferenceCommand, Key);

        // Wrong hardware in verify argument
        Assert.False(DeviceRetryProtocol.Verify(signed, "OTHER-HARDWARE", Key, ReferenceNow));

        // Wrong key
        var wrongKey = new byte[32];
        Assert.False(DeviceRetryProtocol.Verify(signed, signed.HardwareId, wrongKey, ReferenceNow));

        // Invalid key lengths
        Assert.False(DeviceRetryProtocol.Verify(signed, signed.HardwareId, new byte[16], ReferenceNow));
        Assert.False(DeviceRetryProtocol.Verify(signed, signed.HardwareId, new byte[64], ReferenceNow));
        Assert.False(DeviceRetryProtocol.Verify(signed, signed.HardwareId, null!, ReferenceNow));

        // Sign throws for non-32 byte keys
        Assert.Throws<ArgumentException>(() => DeviceRetryProtocol.Sign(ReferenceCommand, new byte[16]));
        Assert.Throws<ArgumentException>(() => DeviceRetryProtocol.Sign(ReferenceCommand, new byte[31]));
        Assert.Throws<ArgumentException>(() => DeviceRetryProtocol.Sign(ReferenceCommand, new byte[33]));
    }

    [Fact]
    public void ExpiryBoundariesAreEnforced()
    {
        var signed = DeviceRetryProtocol.Sign(ReferenceCommand, Key);

        // RequestedAt = 1800000000, ExpiresAt = 1800000030
        // Window is: now >= RequestedAt and now < ExpiresAt

        // Before requested: invalid
        Assert.False(DeviceRetryProtocol.Verify(signed, signed.HardwareId, Key, DateTimeOffset.FromUnixTimeSeconds(1799999999)));

        // Exactly at requested: valid
        Assert.True(DeviceRetryProtocol.Verify(signed, signed.HardwareId, Key, DateTimeOffset.FromUnixTimeSeconds(1800000000)));

        // Inside window: valid
        Assert.True(DeviceRetryProtocol.Verify(signed, signed.HardwareId, Key, DateTimeOffset.FromUnixTimeSeconds(1800000015)));

        // One second before expiry: valid
        Assert.True(DeviceRetryProtocol.Verify(signed, signed.HardwareId, Key, DateTimeOffset.FromUnixTimeSeconds(1800000029)));

        // Exactly at expiry: invalid (now < expires required)
        Assert.False(DeviceRetryProtocol.Verify(signed, signed.HardwareId, Key, DateTimeOffset.FromUnixTimeSeconds(1800000030)));

        // Past expiry: invalid
        Assert.False(DeviceRetryProtocol.Verify(signed, signed.HardwareId, Key, DateTimeOffset.FromUnixTimeSeconds(1800000031)));
    }

    [Fact]
    public void InvalidVersionsUUIDAndHardwareRejected()
    {
        // Version != 1
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { Version = 0 }));
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { Version = 2 }));
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { Version = -1 }));

        // Empty UUIDs
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { RequestId = Guid.Empty }));
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { CommandId = Guid.Empty }));

        // Invalid HardwareId
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { HardwareId = "" }));
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { HardwareId = "   " }));
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { HardwareId = null! }));
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { HardwareId = "device\nnewline" }));
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { HardwareId = "esp32@bad" }));
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { HardwareId = new string('A', 65) }));

        // Null command
        Assert.False(DeviceRetryProtocol.Valid(null));
        Assert.False(DeviceRetryProtocol.Verify(null, "ESP32-A1B2C3", Key, ReferenceNow));

        // Sign throws on invalid command
        Assert.Throws<ArgumentException>(() => DeviceRetryProtocol.Sign(ReferenceCommand with { Version = 2 }, Key));
        Assert.Throws<ArgumentException>(() => DeviceRetryProtocol.Sign(ReferenceCommand with { RequestId = Guid.Empty }, Key));
    }

    [Fact]
    public void TTL31RejectedAndMax30SecondsEnforced()
    {
        // Exactly 30 seconds TTL: valid
        var valid30Sec = ReferenceCommand with { RequestedAt = 1000, ExpiresAt = 1030 };
        Assert.True(DeviceRetryProtocol.Valid(valid30Sec));
        var signed30Sec = DeviceRetryProtocol.Sign(valid30Sec, Key);
        Assert.True(DeviceRetryProtocol.Verify(signed30Sec, signed30Sec.HardwareId, Key, DateTimeOffset.FromUnixTimeSeconds(1015)));

        // TTL 31 seconds: rejected
        var invalid31Sec = ReferenceCommand with { RequestedAt = 1000, ExpiresAt = 1031 };
        Assert.False(DeviceRetryProtocol.Valid(invalid31Sec));
        Assert.Throws<ArgumentException>(() => DeviceRetryProtocol.Sign(invalid31Sec, Key));
        Assert.False(DeviceRetryProtocol.Verify(invalid31Sec with { Signature = new string('0', 64) }, invalid31Sec.HardwareId, Key, DateTimeOffset.FromUnixTimeSeconds(1015)));

        // TTL <= 0: rejected
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { RequestedAt = 1000, ExpiresAt = 1000 }));
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { RequestedAt = 1000, ExpiresAt = 999 }));
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { RequestedAt = 0, ExpiresAt = 30 }));
        Assert.False(DeviceRetryProtocol.Valid(ReferenceCommand with { RequestedAt = -5, ExpiresAt = 25 }));
    }

    [Fact]
    public void ExtremeInt64ValuesDoNotOverflow()
    {
        // RequestedAt > 0 check and ExpiresAt > RequestedAt
        var extremeMax = ReferenceCommand with { RequestedAt = long.MaxValue, ExpiresAt = long.MaxValue };
        Assert.False(DeviceRetryProtocol.Valid(extremeMax));

        // ExpiresAt near max, but difference > 30
        var extremeSpan = ReferenceCommand with { RequestedAt = 1, ExpiresAt = long.MaxValue };
        Assert.False(DeviceRetryProtocol.Valid(extremeSpan));

        // RequestedAt negative
        var extremeMin = ReferenceCommand with { RequestedAt = long.MinValue, ExpiresAt = 30 };
        Assert.False(DeviceRetryProtocol.Valid(extremeMin));

        var extremeMinExp = ReferenceCommand with { RequestedAt = 10, ExpiresAt = long.MinValue };
        Assert.False(DeviceRetryProtocol.Valid(extremeMinExp));

        // Extreme values near long.MaxValue with difference <= 30
        var validExtreme = ReferenceCommand with { RequestedAt = long.MaxValue - 20, ExpiresAt = long.MaxValue - 5 };
        Assert.True(DeviceRetryProtocol.Valid(validExtreme));
        var signedExtreme = DeviceRetryProtocol.Sign(validExtreme, Key);
        // Verify does not throw overflow even when now is normal timestamp
        Assert.False(DeviceRetryProtocol.Verify(signedExtreme, signedExtreme.HardwareId, Key, ReferenceNow));
    }

    [Fact]
    public void DomainSeparationFromUnlockCommand()
    {
        // UnlockCommand canonical starts with florabot.unlock.v1
        var unlock = new UnlockCommand(1, ReferenceCommand.HardwareId, ReferenceCommand.CommandId,
            Guid.Parse("22222222-2222-2222-2222-222222222222"), 7, "CUSTOMER_PICKUP",
            ReferenceCommand.RequestedAt, ReferenceCommand.RequestedAt + 60, "");
        var signedUnlock = DeviceProtocol.Sign(unlock, Key);

        // An UnlockCommand signature must never verify as an UnlockRetryCommand
        var forgedRetry = ReferenceCommand with { Signature = signedUnlock.Signature };
        Assert.False(DeviceRetryProtocol.Verify(forgedRetry, forgedRetry.HardwareId, Key, ReferenceNow));

        // Domain strings are distinct
        Assert.Equal("florabot.retry.v1", DeviceRetryProtocol.Domain);
        Assert.StartsWith("florabot.retry.v1\n", DeviceRetryProtocol.Canonical(ReferenceCommand));
    }

    [Fact]
    public void RoundtripJsonAndSerializerStrict7Fields()
    {
        var signed = DeviceRetryProtocol.Sign(ReferenceCommand, Key);

        // Serialize to JSON
        var json = JsonSerializer.Serialize(signed);

        // Verify JSON has strictly 7 properties
        using var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(7, properties.Count);

        string[] expectedFields = ["version", "hardware_id", "request_id", "cmd_id", "requested_at", "expires_at", "signature"];
        foreach (var field in expectedFields)
        {
            Assert.Contains(field, properties);
        }

        // Roundtrip deserialization
        var deserialized = JsonSerializer.Deserialize<UnlockRetryCommand>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(signed.Version, deserialized.Version);
        Assert.Equal(signed.HardwareId, deserialized.HardwareId);
        Assert.Equal(signed.RequestId, deserialized.RequestId);
        Assert.Equal(signed.CommandId, deserialized.CommandId);
        Assert.Equal(signed.CmdId, deserialized.CmdId);
        Assert.Equal(signed.RequestedAt, deserialized.RequestedAt);
        Assert.Equal(signed.ExpiresAt, deserialized.ExpiresAt);
        Assert.Equal(signed.Signature, deserialized.Signature);

        // Strict 7 fields: deserializing with an extra unmapped field throws JsonException
        var jsonWithExtraField = """
        {
            "version": 1,
            "hardware_id": "ESP32-A1B2C3",
            "request_id": "a1b2c3d4-3333-3333-3333-333333333333",
            "cmd_id": "abcdef01-1111-1111-1111-111111111111",
            "requested_at": 1800000000,
            "expires_at": 1800000030,
            "signature": "9f2aec7aae56d638d32e4a365236147fc4a38ae2358dc6ce37d68cb507fc41e5",
            "slot_id": "22222222-2222-2222-2222-222222222222"
        }
        """;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UnlockRetryCommand>(jsonWithExtraField));
    }

    [Fact]
    public void DeviceKeyRingOverloadsSignAndVerify()
    {
        var keyRing = new DeviceKeyRing(new Dictionary<string, byte[]>
        {
            [ReferenceCommand.HardwareId] = Key
        });

        // Sign via KeyRing delegates to DeviceRetryProtocol
        var signed = keyRing.Sign(ReferenceCommand);
        Assert.Equal("9f2aec7aae56d638d32e4a365236147fc4a38ae2358dc6ce37d68cb507fc41e5", signed.Signature);

        // Verify overloads
        Assert.True(keyRing.Verify(signed, ReferenceNow));
        Assert.True(keyRing.Verify(signed, signed.HardwareId, ReferenceNow));

        // Wrong hardware in explicit overload
        Assert.False(keyRing.Verify(signed, "ESP32-OTHER", ReferenceNow));

        // Unknown hardware
        var unknownHwCommand = ReferenceCommand with { HardwareId = "UNKNOWN-HW" };
        Assert.Throws<InvalidOperationException>(() => keyRing.Sign(unknownHwCommand));
        Assert.False(keyRing.Verify(signed with { HardwareId = "UNKNOWN-HW" }, ReferenceNow));

        // Existing UnlockCommand behavior preserved
        var unlock = new UnlockCommand(1, ReferenceCommand.HardwareId, ReferenceCommand.CommandId,
            Guid.Parse("22222222-2222-2222-2222-222222222222"), 7, "CUSTOMER_PICKUP",
            1800000000, 1800000060, "");
        var signedUnlock = keyRing.Sign(unlock);
        Assert.True(keyRing.Verify(signedUnlock, ReferenceNow));
    }
}
