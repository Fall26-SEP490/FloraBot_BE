using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Tests;

public sealed class ManualUnlockRetryEnrollmentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record Fixture(Guid Admin, Guid Seller, Guid Kiosk, Guid Slot, int Relay);

    private async Task<Fixture> SetupAsync(
        bool kioskOnline = true,
        bool freshHeartbeat = true,
        bool doorVerified = true,
        bool presenceVerified = false,
        bool capEnabled = false,
        bool supportsRetry = false,
        int relay = 0)
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), relay);
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var seed = data.CreateCommand("""
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
            VALUES(@seller, 'Retry Enrollment Shop', '0901234567', 'ACTIVE', '20000000-0000-0000-0000-00000000000d', CURRENT_DATE + 30);

            INSERT INTO identity.users(id, email, password_hash, full_name, role, status)
            VALUES(@admin, @admin::text || '@example.invalid', '!unprovisioned', 'Retry Admin', 'ADMIN', 'ACTIVE');

            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status, seller_id, last_heartbeat_at)
            VALUES(@kiosk, @kiosk::text, 'Retry Kiosk', '123 Test St', 'HCM', @kiosk::text, @kiosk::text,
                   @kioskStatus, @seller,
                   CASE WHEN @freshHeartbeat THEN public.app_now() ELSE public.app_now() - interval '60 seconds' END);

            INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel, status, current_seller_id, bouquet_id, zone)
            VALUES(@slot, @kiosk, 'W01', @relay, 'RENTED_EMPTY', @seller, NULL, 'WALK_IN');

            INSERT INTO kiosk_ops.slot_sensor_capabilities(
                slot_id, kiosk_id, relay_channel, enabled, door_verified, presence_verified, supports_manual_retry, enrolled_by
            ) VALUES (
                @slot, @kiosk, @relay, @capEnabled, @doorVerified, @presenceVerified, @supportsRetry, @admin
            )
            ON CONFLICT (slot_id) DO UPDATE SET
                kiosk_id = excluded.kiosk_id,
                relay_channel = excluded.relay_channel,
                enabled = excluded.enabled,
                door_verified = excluded.door_verified,
                presence_verified = excluded.presence_verified,
                supports_manual_retry = excluded.supports_manual_retry;
            """);

        seed.Parameters.AddWithValue("admin", f.Admin);
        seed.Parameters.AddWithValue("seller", f.Seller);
        seed.Parameters.AddWithValue("kiosk", f.Kiosk);
        seed.Parameters.AddWithValue("slot", f.Slot);
        seed.Parameters.AddWithValue("relay", relay);
        seed.Parameters.AddWithValue("kioskStatus", kioskOnline ? "ONLINE" : "OFFLINE");
        seed.Parameters.AddWithValue("freshHeartbeat", freshHeartbeat);
        seed.Parameters.AddWithValue("doorVerified", doorVerified);
        seed.Parameters.AddWithValue("presenceVerified", presenceVerified);
        seed.Parameters.AddWithValue("capEnabled", capEnabled);
        seed.Parameters.AddWithValue("supportsRetry", supportsRetry);
        await seed.ExecuteNonQueryAsync();

        return f;
    }

    private HttpClient AdminClient(Fixture f, bool provisioned = true)
    {
        var client = provisioned
            ? factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton(new DeviceKeyRing(new Dictionary<string, byte[]>
                {
                    [f.Kiosk.ToString()] = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()
                })))).CreateClient()
            : factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN", userId: f.Admin));
        return client;
    }

    [Fact]
    public async Task ContractExportsManualUnlockRetryEnrollmentEndpoint()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");

        var paths = document.GetProperty("paths");
        var route = "/api/admin/kiosks/{kioskId}/slots/{slotId}/manual-unlock-retry";
        Assert.True(paths.TryGetProperty(route, out var pathItem));
        Assert.True(pathItem.TryGetProperty("put", out var putOp));
        Assert.True(putOp.GetProperty("requestBody").GetProperty("required").GetBoolean());
    }

    [Fact]
    public async Task AdminAuthorizationEnforced()
    {
        var f = await SetupAsync();
        var route = $"/api/admin/kiosks/{f.Kiosk}/slots/{f.Slot}/manual-unlock-retry";
        var request = new ManualUnlockRetryEnrollmentRequest(true, true);

        // Anonymous -> 401
        using var anon = factory.CreateClient();
        var anonResp = await anon.PutAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // Seller -> 403
        using var sellerClient = factory.CreateClient();
        sellerClient.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER"));
        var sellerResp = await sellerClient.PutAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.Forbidden, sellerResp.StatusCode);

        // The authentication middleware rejects a locked admin.
        var revokedAdminId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            await using var inactiveCmd = data.CreateCommand("""
                INSERT INTO identity.users(id, email, password_hash, full_name, role, status)
                VALUES(@id, @id::text || '@example.invalid', '!unprovisioned', 'Inactive Admin', 'ADMIN', 'LOCKED');
                """);
            inactiveCmd.Parameters.AddWithValue("id", revokedAdminId);
            await inactiveCmd.ExecuteNonQueryAsync();
        }
        using var blockedClient = factory.CreateClient();
        blockedClient.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN", userId: revokedAdminId));
        var blockedResp = await blockedClient.PutAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.Unauthorized, blockedResp.StatusCode);
    }

    [Fact]
    public async Task FirmwareVerificationRequiredToEnable()
    {
        var f = await SetupAsync();
        using var client = AdminClient(f);
        var route = $"/api/admin/kiosks/{f.Kiosk}/slots/{f.Slot}/manual-unlock-retry";

        var unconfirmedResp = await client.PutAsJsonAsync(route, new ManualUnlockRetryEnrollmentRequest(Enabled: true, FirmwareVerified: false));
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmedResp.StatusCode);
    }

    [Fact]
    public async Task ValidationRejectsOfflineKioskStaleHeartbeatAndUnverifiedDoor()
    {
        // 1. Offline kiosk -> 409
        var fOffline = await SetupAsync(kioskOnline: false);
        using var clientOffline = AdminClient(fOffline);
        var offlineResp = await clientOffline.PutAsJsonAsync(
            $"/api/admin/kiosks/{fOffline.Kiosk}/slots/{fOffline.Slot}/manual-unlock-retry",
            new ManualUnlockRetryEnrollmentRequest(true, true));
        Assert.Equal(HttpStatusCode.Conflict, offlineResp.StatusCode);

        // 2. Stale heartbeat -> 409
        var fStale = await SetupAsync(freshHeartbeat: false);
        using var clientStale = AdminClient(fStale);
        var staleResp = await clientStale.PutAsJsonAsync(
            $"/api/admin/kiosks/{fStale.Kiosk}/slots/{fStale.Slot}/manual-unlock-retry",
            new ManualUnlockRetryEnrollmentRequest(true, true));
        Assert.Equal(HttpStatusCode.Conflict, staleResp.StatusCode);

        // 3. Door unverified -> 409
        var fDoorUnverified = await SetupAsync(doorVerified: false);
        using var clientDoor = AdminClient(fDoorUnverified);
        var doorResp = await clientDoor.PutAsJsonAsync(
            $"/api/admin/kiosks/{fDoorUnverified.Kiosk}/slots/{fDoorUnverified.Slot}/manual-unlock-retry",
            new ManualUnlockRetryEnrollmentRequest(true, true));
        Assert.Equal(HttpStatusCode.Conflict, doorResp.StatusCode);
    }

    [Fact]
    public async Task EnableSucceedsWithoutPresenceVerification()
    {
        // Setup with door_verified = true, presence_verified = false, enabled = false
        var f = await SetupAsync(doorVerified: true, presenceVerified: false, capEnabled: false, supportsRetry: false);
        using var client = AdminClient(f);
        var route = $"/api/admin/kiosks/{f.Kiosk}/slots/{f.Slot}/manual-unlock-retry";

        using var unprovisionedClient = AdminClient(f, provisioned: false);
        var input = new ManualUnlockRetryEnrollmentRequest(Enabled: true, FirmwareVerified: true);
        Assert.Equal(HttpStatusCode.Conflict, (await unprovisionedClient.PutAsJsonAsync(route, input)).StatusCode);

        var resp = await client.PutAsJsonAsync(route, input);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.CacheControl?.NoStore);

        var body = await resp.Content.ReadFromJsonAsync<SlotSensorCapabilityResponse>();
        Assert.NotNull(body);
        Assert.True(body.SupportsManualRetry);
        Assert.True(body.DoorVerified);
        Assert.False(body.PresenceVerified); // Does NOT require presence verified
        Assert.False(body.Enabled);          // Does NOT require sensor cap enabled

        // Verify in DB
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var verifyCmd = data.CreateCommand("""
            SELECT supports_manual_retry AND manual_retry_hardware_id = @hardware
            FROM kiosk_ops.slot_sensor_capabilities WHERE slot_id = @slot;
            """);
        verifyCmd.Parameters.AddWithValue("slot", f.Slot);
        verifyCmd.Parameters.AddWithValue("hardware", f.Kiosk.ToString());
        Assert.Equal(true, await verifyCmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RevocationAllowedRegardlessOfKioskOrFirmwareStatus()
    {
        // Offline kiosk, supportsRetry currently true
        var f = await SetupAsync(kioskOnline: false, supportsRetry: true);
        using var client = AdminClient(f);
        var route = $"/api/admin/kiosks/{f.Kiosk}/slots/{f.Slot}/manual-unlock-retry";

        // Revoking (Enabled = false) does NOT require firmwareVerified or online kiosk
        var resp = await client.PutAsJsonAsync(route, new ManualUnlockRetryEnrollmentRequest(Enabled: false, FirmwareVerified: false));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<SlotSensorCapabilityResponse>();
        Assert.NotNull(body);
        Assert.False(body.SupportsManualRetry);

        // Verify in DB
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var verifyCmd = data.CreateCommand("""
            SELECT supports_manual_retry FROM kiosk_ops.slot_sensor_capabilities WHERE slot_id = @slot;
            """);
        verifyCmd.Parameters.AddWithValue("slot", f.Slot);
        Assert.Equal(false, await verifyCmd.ExecuteScalarAsync());
    }
}
