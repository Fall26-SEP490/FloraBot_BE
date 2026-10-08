using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class DeviceDispatchTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private NpgsqlDataSource Data => factory.Services.GetRequiredService<NpgsqlDataSource>();
    private sealed record Fixture(Guid Kiosk, Guid Slot, Guid Command, string Hardware, DeviceKeyRing Keys);

    private async Task<Fixture> CreateAsync()
    {
        var kiosk = Guid.NewGuid(); var slot = Guid.NewGuid(); var cmd = Guid.NewGuid();
        var hardware = "DISPATCH-" + kiosk;
        await using var setup = Data.CreateCommand("""
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,last_heartbeat_at)
            VALUES(@kiosk,@kiosk::text,'Dispatch fixture','Test','HCM',@hardware,@hardware,'ONLINE',CURRENT_TIMESTAMP);
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel) VALUES(@slot,@kiosk,'A01',7);
            INSERT INTO kiosk_ops.unlock_tokens(kiosk_id,slot_id,purpose,issued_to_user_id,token_hash,cmd_id,status,issued_at,expires_at)
            VALUES(@kiosk,@slot,'SELLER_ACCESS','10000000-0000-0000-0000-000000000001',@cmd::text,@cmd,'ISSUED',
              CURRENT_TIMESTAMP-interval '1 minute',CURRENT_TIMESTAMP+interval '1 minute')
            """);
        setup.Parameters.AddWithValue("kiosk", kiosk); setup.Parameters.AddWithValue("slot", slot);
        setup.Parameters.AddWithValue("hardware", hardware); setup.Parameters.AddWithValue("cmd", cmd);
        await setup.ExecuteNonQueryAsync();
        return new(kiosk, slot, cmd, hardware, new DeviceKeyRing(new Dictionary<string, byte[]> { [hardware] = Key }));
    }

    private async Task<PreparedUnlock?> Prepare(Fixture fixture)
    {
        using var scope = factory.Services.CreateScope();
        return await new DeviceCommandDispatcher(Data, scope.ServiceProvider.GetRequiredService<ICapPublisher>(), fixture.Keys)
            .PrepareAsync(fixture.Command, CancellationToken.None);
    }

    private async Task<bool> Deliver(Fixture fixture, PreparedUnlock message, Func<UnlockCommand, CancellationToken, Task> publish)
    {
        using var scope = factory.Services.CreateScope();
        return await new DeviceCommandDispatcher(Data, scope.ServiceProvider.GetRequiredService<ICapPublisher>(), fixture.Keys)
            .DeliverAsync(message, publish, CancellationToken.None);
    }

    private async Task MakeRetryDue(Fixture fixture)
    {
        await using var command = Data.CreateCommand("UPDATE kiosk_ops.mqtt_dispatches SET attempted_at=CURRENT_TIMESTAMP-interval '20 seconds',next_attempt_at=CURRENT_TIMESTAMP-interval '10 seconds' WHERE cmd_id=@id");
        command.Parameters.AddWithValue("id", fixture.Command);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ConcurrentDispatchClaimsOnceAndAllowsDurableSensorReceiptBeforePuback()
    {
        var fixture = await CreateAsync();
        var prepared = Assert.Single((await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Prepare(fixture)))).OfType<PreparedUnlock>());
        Assert.Equal(fixture.Command, prepared.Command.CommandId);
        Assert.True(DeviceProtocol.Verify(prepared.Command, fixture.Hardware, Key, DateTimeOffset.UtcNow));
        Assert.Null(await Prepare(fixture));
        var received = DeviceProtocol.Sign(new SignedDeviceEvent(1, fixture.Hardware, Guid.NewGuid(), fixture.Command,
            "ACK", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ""), Key);
        var calls = 0;
        async Task Publish(UnlockCommand message, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            Assert.Equal(7, message.RelayChannel);
            using var scope = factory.Services.CreateScope();
            var inbox = new DeviceEventInbox(Data, scope.ServiceProvider.GetRequiredService<ICapPublisher>(), fixture.Keys,
                scope.ServiceProvider.GetRequiredService<PortalNotifier>());
            Assert.Equal(DeviceReceipt.Stored, await inbox.ReceiveAsync(fixture.Hardware, received, ct));
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Deliver(fixture, prepared, Publish)));
        Assert.Single(results, value => value); Assert.Equal(1, calls);
        using var processing = factory.Services.CreateScope();
        var receiver = new DeviceEventInbox(Data, processing.ServiceProvider.GetRequiredService<ICapPublisher>(), fixture.Keys,
            processing.ServiceProvider.GetRequiredService<PortalNotifier>());
        Assert.True(await receiver.ProcessAsync(received.EventId, CancellationToken.None));
        await MakeRetryDue(fixture);
        Assert.Null(await Prepare(fixture));
    }

    [Fact]
    public async Task FailedTransportRetriesSameCommandThenUsesSourceFailureFlowOnce()
    {
        var fixture = await CreateAsync();
        var first = (await Prepare(fixture))!;
        await Assert.ThrowsAsync<IOException>(() => Deliver(fixture, first, (_, _) => Task.FromException(new IOException("simulated disconnect"))));
        Assert.Null(await Prepare(fixture));
        await MakeRetryDue(fixture);
        var second = (await Prepare(fixture))!;
        Assert.Equal(first.Command, second.Command);
        Assert.NotEqual(first.DeliveryId, second.DeliveryId);
        Assert.False(await Deliver(fixture, first, (_, _) => throw new InvalidOperationException("Stale delivery must not publish")));
        Assert.True(await Deliver(fixture, second, (_, _) => Task.CompletedTask));
        await MakeRetryDue(fixture);
        Assert.Null(await Prepare(fixture));
        Assert.Null(await Prepare(fixture));
        await using var result = Data.CreateCommand("SELECT status,attempts FROM kiosk_ops.unlock_tokens WHERE cmd_id=@id");
        result.Parameters.AddWithValue("id", fixture.Command);
        await using var reader = await result.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync()); Assert.Equal("FAILED", reader.GetString(0)); Assert.Equal(2, reader.GetInt32(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSensorEvidenceAndMissingDeviceKeysPreventDispatch(bool pendingSensor)
    {
        var fixture = await CreateAsync();
        using var scope = factory.Services.CreateScope();
        if (!pendingSensor)
        {
            var unknownKeys = new DeviceKeyRing(new Dictionary<string, byte[]> { ["OTHER"] = Key });
            var dispatcher = new DeviceCommandDispatcher(Data, scope.ServiceProvider.GetRequiredService<ICapPublisher>(), unknownKeys);
            Assert.Null(await dispatcher.PrepareAsync(fixture.Command, CancellationToken.None));
            return;
        }
        var prepared = (await Prepare(fixture))!;
        var inbox = new DeviceEventInbox(Data, scope.ServiceProvider.GetRequiredService<ICapPublisher>(), fixture.Keys,
            scope.ServiceProvider.GetRequiredService<PortalNotifier>());
        var message = DeviceProtocol.Sign(new SignedDeviceEvent(1, fixture.Hardware, Guid.NewGuid(), fixture.Command,
            "OPENED", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ""), Key);
        Assert.Equal(DeviceReceipt.Stored, await inbox.ReceiveAsync(fixture.Hardware, message, CancellationToken.None));
        Assert.False(await Deliver(fixture, prepared, (_, _) => throw new InvalidOperationException("Pending sensor evidence takes priority")));
        await MakeRetryDue(fixture);
        Assert.Null(await Prepare(fixture));
    }

    [Theory]
    [InlineData("REVOKED")]
    [InlineData("ACKED")]
    [InlineData("OPENED")]
    [InlineData("CLOSED")]
    [InlineData("EXPIRED")]
    [InlineData("OFFLINE")]
    [InlineData("STALE_HEARTBEAT")]
    [InlineData("RELAY_CHANGED")]
    public async Task ChangedAuthorizationNeverPublishesPreparedCommand(string change)
    {
        var fixture = await CreateAsync();
        var prepared = (await Prepare(fixture))!;
        var sql = change switch
        {
            "OFFLINE" => "UPDATE kiosk_ops.kiosks SET status='OFFLINE' WHERE id=@id",
            "STALE_HEARTBEAT" => "UPDATE kiosk_ops.kiosks SET last_heartbeat_at=CURRENT_TIMESTAMP-interval '5 minutes' WHERE id=@id",
            "RELAY_CHANGED" => "UPDATE kiosk_ops.slots SET relay_channel=8 WHERE id=@id",
            "EXPIRED" => "UPDATE kiosk_ops.unlock_tokens SET expires_at=CURRENT_TIMESTAMP-interval '1 second' WHERE cmd_id=@id",
            "OPENED" => "SELECT flow.device_event(@id,'ACK'); SELECT flow.device_event(@id,'OPENED')",
            "CLOSED" => "SELECT flow.device_event(@id,'ACK'); SELECT flow.device_event(@id,'OPENED'); SELECT flow.device_event(@id,'CLOSED')",
            _ => "UPDATE kiosk_ops.unlock_tokens SET status=@status WHERE cmd_id=@id"
        };
        await using var alter = Data.CreateCommand(sql);
        alter.Parameters.AddWithValue("id", change is "OFFLINE" or "STALE_HEARTBEAT" ? fixture.Kiosk : change == "RELAY_CHANGED" ? fixture.Slot : fixture.Command);
        if (change is "REVOKED" or "ACKED") alter.Parameters.AddWithValue("status", change);
        await alter.ExecuteNonQueryAsync();
        Assert.False(await Deliver(fixture, prepared, (_, _) => throw new InvalidOperationException("Changed authorization must not publish")));
    }
}
