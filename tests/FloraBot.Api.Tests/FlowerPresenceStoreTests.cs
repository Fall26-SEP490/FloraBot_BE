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
    public async Task AssignedTechnicianSeesOnlyTheIncidentSlotAndFreshSignedReadings()
    {
        var f = await Create();
        var assigned = Guid.NewGuid(); var other = Guid.NewGuid(); var admin = Guid.NewGuid();
        var task = Guid.NewGuid(); var incident = Guid.NewGuid();
        await using (var setup = Data.CreateCommand("""
            INSERT INTO identity.users(id,email,full_name,password_hash,role) VALUES
              (@assigned,@assigned::text||'@example.invalid','Assigned technician','!unprovisioned','TECHNICIAN'),
              (@other,@other::text||'@example.invalid','Other technician','!unprovisioned','TECHNICIAN'),
              (@admin,@admin::text||'@example.invalid','Assigning admin','!unprovisioned','ADMIN');
            INSERT INTO ordering.disputes(id,kind,kiosk_id,slot_id,reason)
            SELECT @incident,'DEVICE_FAULT',@kiosk,id,'Door did not open'
            FROM kiosk_ops.slots WHERE kiosk_id=@kiosk AND relay_channel=0;
            INSERT INTO kiosk_ops.staff_tasks(id,kind,assignee_id,assigned_by,kiosk_id,incident_id,instructions,status)
            VALUES(@task,'INCIDENT',@assigned,@admin,@kiosk,@incident,'Check compartment A01','ASSIGNED');
            """))
        {
            setup.Parameters.AddWithValue("assigned", assigned); setup.Parameters.AddWithValue("other", other);
            setup.Parameters.AddWithValue("admin", admin); setup.Parameters.AddWithValue("kiosk", f.Kiosk);
            setup.Parameters.AddWithValue("task", task); setup.Parameters.AddWithValue("incident", incident);
            await setup.ExecuteNonQueryAsync();
        }
        using var tech = factory.CreateClient();
        tech.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("TECHNICIAN", userId: assigned));
        using var unassigned = factory.CreateClient();
        unassigned.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("TECHNICIAN", userId: other));
        var endpoint = $"/api/staff/tasks/{task}/iot-health";
        Assert.Equal(HttpStatusCode.NotFound, (await unassigned.GetAsync(endpoint)).StatusCode);

        Assert.True(await f.Receiver.ReceiveAsync(f.Message.HardwareId, f.Message, default));
        var legacy = (await tech.GetFromJsonAsync<TechnicianTaskTelemetry>(endpoint))!;
        Assert.True(legacy.IsFresh);
        var legacySlot = Assert.Single(legacy.Slots);
        Assert.Equal("A01", legacySlot.SlotCode);
        Assert.True(legacySlot.FlowerDetected);
        Assert.Null(legacySlot.DoorClosed);

        var at = (f.Message.OccurredAt + 1) / 1000;
        var newMessage = FlowerPresenceProtocol.Sign(f.Message with
        {
            Version = 2,
            EventId = Guid.NewGuid(),
            OccurredAt = f.Message.OccurredAt + 1,
            DoorStates = [1, 0, 1, 0],
            DoorCycles = [4, 0, 0, 0],
            DoorCycleTimes = [at, 0, 0, 0]
        }, Key);
        Assert.True(await f.Receiver.ReceiveAsync(newMessage.HardwareId, newMessage, default));
        var current = (await tech.GetFromJsonAsync<TechnicianTaskTelemetry>(endpoint))!;
        var slot = Assert.Single(current.Slots);
        Assert.True(slot.DoorClosed);
        Assert.Equal("OPEN_TIMEOUT", slot.LastCycle);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(at), slot.LastCycleAt);

        await using var stale = Data.CreateCommand("UPDATE kiosk_ops.flower_presence SET received_at=CURRENT_TIMESTAMP-interval '46 seconds' WHERE kiosk_id=@id");
        stale.Parameters.AddWithValue("id", f.Kiosk); await stale.ExecuteNonQueryAsync();
        var expired = (await tech.GetFromJsonAsync<TechnicianTaskTelemetry>(endpoint))!;
        Assert.False(expired.IsFresh);
        var expiredSlot = Assert.Single(expired.Slots);
        Assert.Null(expiredSlot.FlowerDetected);
        Assert.Null(expiredSlot.DoorClosed);
        Assert.Null(expiredSlot.LastCycle);
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
        var foreignSeller = Guid.NewGuid();
        var foreignUser = Guid.NewGuid();
        await using (var foreignSetup = Data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Foreign Telemetry Seller','0909999888','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@user,@user::text||'@example.invalid','!unprovisioned','Foreign Telemetry User','SELLER',@seller,'ACTIVE');
            """))
        {
            foreignSetup.Parameters.AddWithValue("seller", foreignSeller);
            foreignSetup.Parameters.AddWithValue("user", foreignUser);
            await foreignSetup.ExecuteNonQueryAsync();
        }
        foreign.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: foreignUser, sellerId: foreignSeller));
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
