using FloraBot.Api.Modules.KioskOps;

namespace FloraBot.Api.Tests;

public sealed class FlowerPresenceProtocolTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1800000001000);
    private static SignedFlowerPresence Message => new(1, "ESP32-A1B2C3",
        Guid.Parse("11111111-1111-4111-8111-111111111111"), "FLOWER_PRESENCE", 1800000001000, [1, 0, -1, 1], "");

    [Fact]
    public void CanonicalIsSharedWithFirmwareAndAllFourStatesAreAuthenticated()
    {
        Assert.Equal("florabot.flower.v1\nESP32-A1B2C3\n11111111-1111-4111-8111-111111111111\n1800000001000\n1\n0\n-1\n1",
            FlowerPresenceProtocol.Canonical(Message));
        var signed = FlowerPresenceProtocol.Sign(Message, Key);
        Assert.Equal("8cdf0154af95d8939045e581ac7b7a7b0395a1e56119d7414315810a19a177bf", signed.Signature);
        Assert.True(FlowerPresenceProtocol.Verify(signed, signed.HardwareId, Key, Now));
        for (var i = 0; i < 4; i++)
        {
            var states = signed.States.ToArray(); states[i] = states[i] == 1 ? 0 : 1;
            Assert.False(FlowerPresenceProtocol.Verify(signed with { States = states }, signed.HardwareId, Key, Now));
        }
        Assert.False(FlowerPresenceProtocol.Verify(signed with { OccurredAt = signed.OccurredAt + 1 }, signed.HardwareId, Key, Now));
        Assert.False(FlowerPresenceProtocol.Verify(signed with { EventId = Guid.NewGuid() }, signed.HardwareId, Key, Now));
        Assert.False(FlowerPresenceProtocol.Verify(signed, "OTHER", Key, Now));
        Assert.False(FlowerPresenceProtocol.Verify(signed, signed.HardwareId, new byte[32], Now));
    }

    [Fact]
    public void RejectsStaleFutureMalformedAndOrderEventSubstitution()
    {
        var signed = FlowerPresenceProtocol.Sign(Message, Key);
        Assert.False(FlowerPresenceProtocol.Verify(signed, signed.HardwareId, Key, Now.AddMilliseconds(120001)));
        Assert.False(FlowerPresenceProtocol.Verify(signed, signed.HardwareId, Key, Now.AddMilliseconds(-5001)));
        Assert.False(FlowerPresenceProtocol.Verify(signed with { States = null! }, signed.HardwareId, Key, Now));
        Assert.False(FlowerPresenceProtocol.Verify(signed with { States = [1, 0, 1] }, signed.HardwareId, Key, Now));
        Assert.False(FlowerPresenceProtocol.Verify(signed with { States = [1, 0, 2, 1] }, signed.HardwareId, Key, Now));
        Assert.False(FlowerPresenceProtocol.Verify(signed with { Signature = new string('z', 64) }, signed.HardwareId, Key, Now));
        Assert.False(FlowerPresenceProtocol.Verify(signed with { Event = "CLOSED" }, signed.HardwareId, Key, Now));
        var keys = new DeviceKeyRing(new Dictionary<string, byte[]> { [signed.HardwareId] = Key });
        Assert.False(keys.Verify(signed with { HardwareId = "UNKNOWN" }, "UNKNOWN", Now));
    }
}
