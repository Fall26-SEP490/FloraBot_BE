using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

// Requires the existing florabot_api_tests database with runtime migrations applied.
public sealed class FlowerPresenceStoreTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private NpgsqlDataSource Data => factory.Services.GetRequiredService<NpgsqlDataSource>();

    private async Task<(Guid Kiosk, SignedFlowerPresence Message, FlowerPresenceReceiver Receiver)> Create()
    {
        var kiosk = Guid.NewGuid(); var hardware = "FLOWER-" + kiosk;
        await using var command = Data.CreateCommand("""
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@id,@hardware,'Flower telemetry test','Test','HCM',@hardware,@hardware,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel)
            SELECT gen_random_uuid(),@id,'A0'||(r+1),r FROM generate_series(0,3) r;
            """);
        command.Parameters.AddWithValue("id", kiosk); command.Parameters.AddWithValue("hardware", hardware);
        await command.ExecuteNonQueryAsync();
        var message = FlowerPresenceProtocol.Sign(new(1, hardware, Guid.NewGuid(), FlowerPresenceProtocol.Event,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000, [1, 0, -1, 1], ""), Key);
        return (kiosk, message, new FlowerPresenceReceiver(Data, new DeviceKeyRing(new Dictionary<string, byte[]> { [hardware] = Key })));
    }

    [Fact]
    public async Task RedeliveryAndOlderTelemetryCannotOverwriteNewerObservationOrChangeOrders()
    {
        var f = await Create();
        Assert.True(await f.Receiver.ReceiveAsync(f.Message.HardwareId, f.Message, default));
        Assert.True(await f.Receiver.ReceiveAsync(f.Message.HardwareId, f.Message, default));
        var older = FlowerPresenceProtocol.Sign(f.Message with { EventId = Guid.NewGuid(), OccurredAt = f.Message.OccurredAt - 100, States = [0, 0, 0, 0] }, Key);
        Assert.True(await f.Receiver.ReceiveAsync(older.HardwareId, older, default));
        Assert.False(await f.Receiver.ReceiveAsync(f.Message.HardwareId, f.Message with { States = [0, 0, 0, 0] }, default));
        await using var read = Data.CreateCommand("""
            SELECT observed_at,states,(SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE kiosk_id=@id)
            FROM kiosk_ops.flower_presence WHERE kiosk_id=@id
            """);
        read.Parameters.AddWithValue("id", f.Kiosk);
        await using var rows = await read.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal(f.Message.OccurredAt, rows.GetInt64(0));
        Assert.Equal(f.Message.States, rows.GetFieldValue<int[]>(1));
        Assert.Equal(0, rows.GetInt64(2));
    }

    [Fact]
    public async Task ReadApiSeparatesUnknownSignalsDeniesOtherTenantsAndHidesStaleReadings()
    {
        var f = await Create();
        await f.Receiver.ReceiveAsync(f.Message.HardwareId, f.Message, default);
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var route = $"/api/kiosks/{f.Kiosk}/flower-presence";
        var response = await admin.GetAsync(route);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var view = (await response.Content.ReadFromJsonAsync<FlowerPresenceResponse>())!;
        Assert.True(view.IsFresh);
        Assert.Equal(new bool?[] { true, false, null, true }, view.Slots.Select(s => s.HasFlowers));
        using var foreign = factory.CreateClient();
        foreign.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", sellerId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(route)).StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        await using var stale = Data.CreateCommand("UPDATE kiosk_ops.flower_presence SET received_at=CURRENT_TIMESTAMP-interval '46 seconds' WHERE kiosk_id=@id");
        stale.Parameters.AddWithValue("id", f.Kiosk); await stale.ExecuteNonQueryAsync();
        var staleView = (await admin.GetFromJsonAsync<FlowerPresenceResponse>(route))!;
        Assert.False(staleView.IsFresh);
        Assert.All(staleView.Slots, s => Assert.Null(s.HasFlowers));
    }
}
