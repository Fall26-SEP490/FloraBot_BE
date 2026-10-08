using FloraBot.Api.Modules.KioskOps;

namespace FloraBot.Api.Tests;

public sealed class DeviceProtocolTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000001);
    private static UnlockCommand Command => new(1, "ESP32-A1B2C3",
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        7, "CUSTOMER_PICKUP", 1800000000, 1800000060, "");

    [Fact]
    public void CommandMatchesIndependentPythonVector()
    {
        var signed = DeviceProtocol.Sign(Command, Key);
        Assert.Equal("c8795c2a6674c0de6c04d8823b5d53b5e606a493e358a3fffa14c475b6b20d29", signed.Signature);
        Assert.True(DeviceProtocol.Verify(signed, signed.HardwareId, Key, Now));
        Assert.False(DeviceProtocol.Verify(signed, "ESP32-D4E5F6", Key, Now));
        Assert.False(DeviceProtocol.Verify(signed, signed.HardwareId, new byte[32], Now));
        Assert.False(DeviceProtocol.Verify(signed, signed.HardwareId, Key, Now.AddSeconds(-2)));
        Assert.False(DeviceProtocol.Verify(signed, signed.HardwareId, Key, DateTimeOffset.FromUnixTimeSeconds(signed.ExpiresAt)));
    }

    [Fact]
    public void EveryActuationFieldIsAuthenticated()
    {
        var signed = DeviceProtocol.Sign(Command, Key);
        UnlockCommand[] altered = [signed with { Version = 2 }, signed with { HardwareId = "ESP32-D4E5F6" },
            signed with { CommandId = Guid.NewGuid() }, signed with { SlotId = Guid.NewGuid() },
            signed with { RelayChannel = 8 }, signed with { Purpose = "SELLER_ACCESS" },
            signed with { IssuedAt = signed.IssuedAt - 1 }, signed with { ExpiresAt = signed.ExpiresAt + 1 },
            signed with { Signature = new string('z', 64) }, signed with { Signature = "" }];
        foreach (var message in altered) Assert.False(DeviceProtocol.Verify(message, message.HardwareId, Key, Now));
        Assert.Throws<ArgumentException>(() => DeviceProtocol.Sign(Command with { HardwareId = "device\nother" }, Key));
        Assert.Throws<ArgumentException>(() => DeviceProtocol.Sign(Command with { RelayChannel = 64 }, Key));
        Assert.Throws<ArgumentException>(() => DeviceProtocol.Sign(Command, new byte[16]));
    }

    [Fact]
    public void DeviceEventRejectsReplayWindowAndAlteredState()
    {
        var message = DeviceProtocol.Sign(new SignedDeviceEvent(1, Command.HardwareId, Guid.NewGuid(), Command.CommandId,
            "CLOSED", Now.ToUnixTimeSeconds(), ""), Key);
        Assert.True(DeviceProtocol.Verify(message, message.HardwareId, Key, Now));
        Assert.False(DeviceProtocol.Verify(message with { Event = "OPENED" }, message.HardwareId, Key, Now));
        Assert.False(DeviceProtocol.Verify(message with { CommandId = Guid.NewGuid() }, message.HardwareId, Key, Now));
        Assert.False(DeviceProtocol.Verify(message with { EventId = Guid.NewGuid() }, message.HardwareId, Key, Now));
        Assert.False(DeviceProtocol.Verify(message, message.HardwareId, Key, Now.AddSeconds(121)));
        Assert.False(DeviceProtocol.Verify(message, message.HardwareId, Key, Now.AddSeconds(-6)));
        Assert.Throws<ArgumentException>(() => DeviceProtocol.Sign(message with { Event = "SENT" }, Key));
        Assert.Throws<ArgumentException>(() => DeviceProtocol.Sign(message with { Event = "HEARTBEAT" }, Key));
        var heartbeat = DeviceProtocol.Sign(message with { Event = "HEARTBEAT", CommandId = Guid.Empty }, Key);
        Assert.True(DeviceProtocol.Verify(heartbeat, heartbeat.HardwareId, Key, Now));
    }
}
