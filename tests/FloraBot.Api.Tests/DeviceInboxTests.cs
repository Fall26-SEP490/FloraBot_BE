using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class DeviceInboxTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private NpgsqlDataSource Data => factory.Services.GetRequiredService<NpgsqlDataSource>();
    private sealed record Fixture(Guid Kiosk, Guid Command, string Hardware, DeviceKeyRing Keys);

    private async Task<Fixture> CreateAsync(bool expired = false)
    {
        var kiosk = Guid.NewGuid(); var slot = Guid.NewGuid(); var cmd = Guid.NewGuid();
        var hardware = "INBOX-" + kiosk;
        await using var setup = Data.CreateCommand("""
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Inbox fixture','Test','HCM',@hardware,@hardware,'OFFLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel) VALUES(@slot,@kiosk,'A01',0);
            INSERT INTO kiosk_ops.unlock_tokens(kiosk_id,slot_id,purpose,issued_to_user_id,token_hash,cmd_id,status,attempts,issued_at,expires_at)
            VALUES(@kiosk,@slot,'SELLER_ACCESS','10000000-0000-0000-0000-000000000001',@cmd::text,@cmd,'SENT',1,
              CURRENT_TIMESTAMP-interval '2 minutes',CURRENT_TIMESTAMP+CASE WHEN @expired THEN interval '-1 minute' ELSE interval '1 minute' END)
            """);
        setup.Parameters.AddWithValue("kiosk", kiosk); setup.Parameters.AddWithValue("slot", slot);
        setup.Parameters.AddWithValue("hardware", hardware); setup.Parameters.AddWithValue("cmd", cmd);
        setup.Parameters.AddWithValue("expired", expired);
        await setup.ExecuteNonQueryAsync();
        return new(kiosk, cmd, hardware, new DeviceKeyRing(new Dictionary<string, byte[]> { [hardware] = Key }));
    }

    private static SignedDeviceEvent Event(Fixture fixture, string kind, int age = 0) => DeviceProtocol.Sign(
        new SignedDeviceEvent(1, fixture.Hardware, Guid.NewGuid(), kind == "HEARTBEAT" ? Guid.Empty : fixture.Command,
            kind, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - age, ""), Key);

    private async Task<DeviceReceipt> Receive(Fixture fixture, SignedDeviceEvent message, string? topic = null)
    {
        using var scope = factory.Services.CreateScope();
        return await new DeviceEventInbox(Data, scope.ServiceProvider.GetRequiredService<ICapPublisher>(), fixture.Keys,
            scope.ServiceProvider.GetRequiredService<FloraBot.Api.Realtime.PortalNotifier>())
            .ReceiveAsync(topic ?? fixture.Hardware, message, CancellationToken.None);
    }

    private async Task<bool> Process(Fixture fixture, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await new DeviceEventInbox(Data, scope.ServiceProvider.GetRequiredService<ICapPublisher>(), fixture.Keys,
            scope.ServiceProvider.GetRequiredService<FloraBot.Api.Realtime.PortalNotifier>())
            .ProcessAsync(id, CancellationToken.None);
    }

    private async Task<string> Status(Fixture fixture)
    {
        await using var command = Data.CreateCommand("SELECT status FROM kiosk_ops.unlock_tokens WHERE cmd_id=@id");
        command.Parameters.AddWithValue("id", fixture.Command);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ConcurrentRedeliveryAndWorkersApplyEachTransitionOnce()
    {
        var fixture = await CreateAsync();
        foreach (var kind in new[] { "ACK", "OPENED", "CLOSED" })
        {
            var message = Event(fixture, kind);
            var receipts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Receive(fixture, message)));
            Assert.Single(receipts, r => r == DeviceReceipt.Stored);
            Assert.Equal(2, receipts.Count(r => r == DeviceReceipt.Duplicate));
            Assert.Single(await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Process(fixture, message.EventId))), r => r);
            await using var audit = Data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE action=@action AND payload->>'cmd_id'=@id");
            audit.Parameters.AddWithValue("action", "DEVICE_" + kind); audit.Parameters.AddWithValue("id", fixture.Command.ToString());
            Assert.Equal(1L, await audit.ExecuteScalarAsync());
        }
        Assert.Equal("CLOSED", await Status(fixture));
        var repeated = Event(fixture, "CLOSED");
        Assert.Equal(DeviceReceipt.Stored, await Receive(fixture, repeated));
        Assert.True(await Process(fixture, repeated.EventId));
        await using var result = Data.CreateCommand("SELECT disposition FROM kiosk_ops.mqtt_inbox WHERE event_id=@id");
        result.Parameters.AddWithValue("id", repeated.EventId);
        Assert.Equal("IGNORED", await result.ExecuteScalarAsync());
    }

    [Fact]
    public async Task OutOfOrderEventsRemainDurableAcrossHandlerInstances()
    {
        var fixture = await CreateAsync();
        var closed = Event(fixture, "CLOSED"); var opened = Event(fixture, "OPENED"); var ack = Event(fixture, "ACK");
        Assert.Equal(DeviceReceipt.Stored, await Receive(fixture, closed));
        Assert.False(await Process(fixture, closed.EventId));
        Assert.Equal(DeviceReceipt.Stored, await Receive(fixture, opened));
        Assert.False(await Process(fixture, opened.EventId));
        Assert.Equal(DeviceReceipt.Stored, await Receive(fixture, ack));
        Assert.True(await Process(fixture, ack.EventId));
        Assert.True(await Process(fixture, opened.EventId));
        Assert.True(await Process(fixture, closed.EventId));
        Assert.Equal("CLOSED", await Status(fixture));
    }

    [Fact]
    public async Task InvalidSignaturesOwnershipAndEventIdCollisionsAreRejected()
    {
        var fixture = await CreateAsync(); var other = await CreateAsync();
        var valid = Event(fixture, "ACK");
        Assert.Equal(DeviceReceipt.Rejected, await Receive(fixture, valid with { Signature = "bad" }));
        Assert.Equal(DeviceReceipt.Rejected, await Receive(fixture, valid, other.Hardware));
        Assert.Equal(DeviceReceipt.Rejected, await Receive(fixture, Event(fixture, "ACK", 121)));
        var foreign = DeviceProtocol.Sign(valid with { CommandId = other.Command }, Key);
        Assert.Equal(DeviceReceipt.Rejected, await Receive(fixture, foreign));
        Assert.Equal(DeviceReceipt.Stored, await Receive(fixture, valid));
        var collision = DeviceProtocol.Sign(valid with { Event = "CLOSED" }, Key);
        Assert.Equal(DeviceReceipt.Rejected, await Receive(fixture, collision));
        Assert.True(await Process(fixture, valid.EventId));
        Assert.Equal("ACKED", await Status(fixture));
    }

    [Fact]
    public async Task ExpiredTokenCannotOpenAndHeartbeatUsesServerTime()
    {
        var fixture = await CreateAsync(expired: true);
        var ack = Event(fixture, "ACK");
        await Receive(fixture, ack); await Process(fixture, ack.EventId);
        Assert.Equal("EXPIRED", await Status(fixture));
        var opened = Event(fixture, "OPENED");
        await Receive(fixture, opened); await Process(fixture, opened.EventId);
        Assert.Equal("EXPIRED", await Status(fixture));
        var stale = Event(fixture, "HEARTBEAT", 100);
        Assert.Equal(DeviceReceipt.Stored, await Receive(fixture, stale));
        await Process(fixture, stale.EventId);
        await using var offline = Data.CreateCommand("SELECT status FROM kiosk_ops.kiosks WHERE id=@id");
        offline.Parameters.AddWithValue("id", fixture.Kiosk);
        Assert.Equal("OFFLINE", await offline.ExecuteScalarAsync());
        var fresh = Event(fixture, "HEARTBEAT", -4);
        await Receive(fixture, fresh); await Process(fixture, fresh.EventId);
        await using var online = Data.CreateCommand("SELECT status='ONLINE' AND last_heartbeat_at<=CURRENT_TIMESTAMP AND last_heartbeat_at>CURRENT_TIMESTAMP-interval '10 seconds' FROM kiosk_ops.kiosks WHERE id=@id");
        online.Parameters.AddWithValue("id", fixture.Kiosk);
        Assert.Equal(true, await online.ExecuteScalarAsync());
    }
}
