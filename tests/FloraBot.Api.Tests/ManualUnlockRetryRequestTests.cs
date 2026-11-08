using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using DotNetCore.CAP;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Tests;

public sealed class ManualUnlockRetryRequestTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly byte[] DeviceSigningKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private sealed record SellerFixture(
        Guid Seller,
        Guid Actor,
        Guid Kiosk,
        string HardwareId,
        string RawApiKey,
        Guid Slot,
        int Relay,
        Guid Product,
        Guid Bouquet,
        Guid Session,
        Guid Command);

    private sealed record CustomerFixture(
        Guid Seller,
        Guid Customer,
        Guid Kiosk,
        string HardwareId,
        string RawApiKey,
        Guid Slot,
        int Relay,
        Guid Product,
        Guid Bouquet,
        Guid Order,
        string TrackingToken,
        Guid Command);

    private static string HashApiKey(string rawKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

    private async Task<SellerFixture> SetupSellerSessionAsync(
        bool supportsManualRetry = true,
        bool doorVerified = true,
        bool agedPastCooldown = true,
        string slotStatus = "LOCKED",
        string tokenStatus = "ACKED",
        bool doorOpened = false,
        bool tokenExpired = false,
        bool withSnapshots = true,
        bool withAppliedAck = true,
        int relay = 0)
    {
        var kioskId = Guid.NewGuid();
        var rawApiKey = "kiosk_key_" + kioskId.ToString("N")[..16];
        var f = new SellerFixture(
            Guid.NewGuid(),
            Guid.NewGuid(),
            kioskId,
            "HW-" + Guid.NewGuid().ToString("N")[..12],
            rawApiKey,
            Guid.NewGuid(),
            relay,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var issueOffset = agedPastCooldown ? "interval '40 seconds'" : "interval '10 seconds'";
        var expiryOffset = tokenExpired ? "- interval '5 seconds'" : "interval '5 minutes'";
        var attempts = tokenStatus == "SENT" ? 1 : 1;

        await using var seed = data.CreateCommand($"""
            -- 1. Seller & active actor
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
            VALUES(@seller, 'Retry Seller ' || @seller::text, '0901234567', 'ACTIVE', '20000000-0000-0000-0000-00000000000d', CURRENT_DATE + 30);

            INSERT INTO identity.users(id, email, password_hash, full_name, role, seller_id, status)
            VALUES(@actor, @actor::text || '@example.invalid', '!unprovisioned', 'Retry Actor', 'SELLER', @seller, 'ACTIVE');

            -- 2. Shop-owned product & bouquet
            INSERT INTO catalog.flower_products(id, seller_id, name, price, shelf_life_hours, status)
            VALUES(@product, @seller, 'Retry Shop Flower', 250000, 48, 'ACTIVE');

            INSERT INTO kiosk_ops.bouquets(id, product_id, seller_id, qr_code, price_snapshot, status, stocked_at, sellable_until)
            VALUES(@bouquet, @product, @seller, @bouquet::text, 250000, 'STOCKED', public.app_now(), public.app_now() + interval '48 hours');

            -- 3. Kiosk with provisioned API key hash and hardware ID
            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status, seller_id, last_heartbeat_at, api_key_hash)
            VALUES(@kiosk, @kiosk::text, 'Retry Kiosk', '123 Test St', 'HCM', @hardware, @hardware, 'ONLINE', @seller, public.app_now(), @keyHash);

            -- 4. Slot assigned to seller and updated to target status
            INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel, status, current_seller_id, bouquet_id, zone)
            VALUES(@slot, @kiosk, 'W01', @relay, @slotStatus, @seller, @bouquet, 'WALK_IN');

            INSERT INTO kiosk_ops.slot_assignments(slot_id, seller_id, assigned_by, status)
            VALUES(@slot, @seller, '10000000-0000-0000-0000-000000000001', 'ACTIVE');

            -- 5. Capability mapping with manual_retry_hardware_id
            INSERT INTO kiosk_ops.slot_sensor_capabilities(
                slot_id, kiosk_id, relay_channel, enabled, door_verified, presence_verified,
                supports_manual_retry, manual_retry_hardware_id, enrolled_by
            ) VALUES (
                @slot, @kiosk, @relay, true, @doorVerified, true,
                @supportsRetry, @hardware, '10000000-0000-0000-0000-000000000001'
            );

            -- 6. Initial command token
            INSERT INTO kiosk_ops.unlock_tokens(
                id, kiosk_id, slot_id, purpose, issued_to_user_id, token_hash, cmd_id, status, attempts,
                issued_at, expires_at, door_opened_at
            ) VALUES (
                public.uuid_v7(), @kiosk, @slot, 'SELLER_ACCESS', @actor, 'hash_' || @cmd::text, @cmd, @tokenStatus, {attempts},
                public.app_now() - {issueOffset},
                public.app_now() + {expiryOffset},
                CASE WHEN @doorOpened THEN public.app_now() - interval '20 seconds' ELSE NULL END
            );

            -- 7. Dispatch record with 3 snapshot columns (or NULL if testing opt-out)
            INSERT INTO kiosk_ops.mqtt_dispatches(
                cmd_id, delivery_id, attempted_at, next_attempt_at, pubacked_at,
                hardware_id_snapshot, relay_channel_snapshot, slot_id_snapshot
            ) VALUES (
                @cmd, gen_random_uuid(), public.app_now() - {issueOffset},
                public.app_now() - {issueOffset} + interval '2 seconds', public.app_now() - {issueOffset},
                CASE WHEN @withSnapshots THEN @hardware ELSE NULL END,
                CASE WHEN @withSnapshots THEN @relay ELSE NULL END,
                CASE WHEN @withSnapshots THEN @slot ELSE NULL END
            );

            -- 8. Applied ACK in mqtt_inbox if requested
            INSERT INTO kiosk_ops.mqtt_inbox(
                event_id, kiosk_id, cmd_id, event, payload_hash, occurred_at, received_at, processed_at, disposition
            )
            SELECT
                gen_random_uuid(), @kiosk, @cmd, 'ACK', 'mock_hash_' || @cmd::text,
                public.app_now() - {issueOffset} + interval '1 second',
                public.app_now() - {issueOffset} + interval '1 second',
                public.app_now() - {issueOffset} + interval '1 second',
                'APPLIED'
            WHERE @withAppliedAck;

            -- 9. Maintenance session
            INSERT INTO kiosk_ops.slot_maintenance_sessions(
                id, kiosk_id, slot_id, seller_id, actor_id, action, stage, intended_stage, substate,
                previous_slot_status, old_bouquet_id, reason, phase_started_at, expires_at, current_cmd_id
            ) VALUES (
                @session, @kiosk, @slot, @seller, @actor, 'REMOVE', 'AwaitRemoval', 'AwaitRemoval',
                CASE WHEN @doorOpened THEN 'RemovalDoorOpened' ELSE 'Initial' END,
                'STOCKED', @bouquet, 'Session for retry test',
                public.app_now() - {issueOffset}, public.app_now() + interval '15 minutes', @cmd
            );
            """);

        seed.Parameters.AddWithValue("seller", f.Seller);
        seed.Parameters.AddWithValue("actor", f.Actor);
        seed.Parameters.AddWithValue("product", f.Product);
        seed.Parameters.AddWithValue("bouquet", f.Bouquet);
        seed.Parameters.AddWithValue("kiosk", f.Kiosk);
        seed.Parameters.AddWithValue("hardware", f.HardwareId);
        seed.Parameters.AddWithValue("keyHash", HashApiKey(f.RawApiKey));
        seed.Parameters.AddWithValue("slot", f.Slot);
        seed.Parameters.AddWithValue("relay", relay);
        seed.Parameters.AddWithValue("cmd", f.Command);
        seed.Parameters.AddWithValue("session", f.Session);
        seed.Parameters.AddWithValue("slotStatus", slotStatus);
        seed.Parameters.AddWithValue("tokenStatus", tokenStatus);
        seed.Parameters.AddWithValue("doorVerified", doorVerified);
        seed.Parameters.AddWithValue("supportsRetry", supportsManualRetry);
        seed.Parameters.AddWithValue("doorOpened", doorOpened);
        seed.Parameters.AddWithValue("withSnapshots", withSnapshots);
        seed.Parameters.AddWithValue("withAppliedAck", withAppliedAck);
        await seed.ExecuteNonQueryAsync();

        return f;
    }

    private async Task<CustomerFixture> SetupCustomerPickupAsync(
        bool guest = false,
        bool supportsManualRetry = true,
        bool doorVerified = true,
        bool agedPastCooldown = true,
        int relay = 0)
    {
        var kioskId = Guid.NewGuid();
        var rawApiKey = "kiosk_key_" + kioskId.ToString("N")[..16];
        var f = new CustomerFixture(
            Guid.NewGuid(),
            Guid.NewGuid(),
            kioskId,
            "HW-" + Guid.NewGuid().ToString("N")[..12],
            rawApiKey,
            Guid.NewGuid(),
            relay,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "",
            Guid.NewGuid());

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var issueOffset = agedPastCooldown ? "interval '40 seconds'" : "interval '10 seconds'";

        await using var seed = data.CreateCommand($"""
            -- 1. Seller & active seller staff
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
            VALUES(@seller, 'Pickup Seller ' || @seller::text, '0901234567', 'ACTIVE', '20000000-0000-0000-0000-00000000000d', CURRENT_DATE + 30);

            INSERT INTO identity.users(id, email, password_hash, full_name, role, status)
            VALUES(@customer, @customer::text || '@example.invalid', '!unprovisioned', 'Customer Pickup', 'CUSTOMER', 'ACTIVE');

            -- 2. Real shop product & bouquets
            INSERT INTO catalog.flower_products(id, seller_id, name, price, shelf_life_hours, status)
            VALUES(@product, @seller, 'Pickup Real Flower', 300000, 48, 'ACTIVE');

            INSERT INTO kiosk_ops.bouquets(id, product_id, seller_id, qr_code, price_snapshot, status, stocked_at, sellable_until)
            VALUES(@bouquet, @product, @seller, @bouquet::text, 300000, 'STOCKED', public.app_now(), public.app_now() + interval '48 hours');

            -- 3. Kiosk with API key hash
            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status, seller_id, last_heartbeat_at, api_key_hash)
            VALUES(@kiosk, @kiosk::text, 'Pickup Kiosk', '456 Main St', 'HCM', @hardware, @hardware, 'ONLINE', @seller, public.app_now(), @keyHash);

            -- 4. Customer held slot & assignment
            INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel, status, current_seller_id, bouquet_id, zone, hold_order_id, hold_until)
            VALUES(@slot, @kiosk, 'W02', @relay, 'HELD', @seller, @bouquet, 'WALK_IN', @order, public.app_now() + interval '15 minutes');

            INSERT INTO kiosk_ops.slot_assignments(slot_id, seller_id, assigned_by, status)
            VALUES(@slot, @seller, '10000000-0000-0000-0000-000000000001', 'ACTIVE');

            -- 5. Sensor capability: door verified and supports_manual_retry
            INSERT INTO kiosk_ops.slot_sensor_capabilities(
                slot_id, kiosk_id, relay_channel, enabled, door_verified, presence_verified,
                supports_manual_retry, manual_retry_hardware_id, enrolled_by
            ) VALUES (
                @slot, @kiosk, @relay, false, @doorVerified, false,
                @supportsRetry, @hardware, '10000000-0000-0000-0000-000000000001'
            );

            -- 6. Member order & active line item
            INSERT INTO ordering.orders(
                id, order_code, checkout_id, customer_id, seller_id, kiosk_id, status, subtotal, total_amount, tracking_token
            ) VALUES (
                @order, 'ORD-' || @order::text, gen_random_uuid(), @orderCustomer, @seller, @kiosk, 'DISPENSING', 300000, 300000, flow.new_tracking_token()
            );

            INSERT INTO ordering.order_items(order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total)
            VALUES(@order, 'BOUQUET', @bouquet, @slot, 'Pickup Flower', 1, 300000, 300000);

            -- 7. Member unlock command token
            INSERT INTO kiosk_ops.unlock_tokens(
                id, kiosk_id, slot_id, purpose, order_id, token_hash, cmd_id, status, attempts,
                issued_at, expires_at
            ) VALUES (
                public.uuid_v7(), @kiosk, @slot, 'CUSTOMER_PICKUP', @order, 'hash_' || @cmd::text, @cmd, 'ACKED', 1,
                public.app_now() - {issueOffset}, public.app_now() + interval '5 minutes'
            );

            INSERT INTO kiosk_ops.mqtt_dispatches(
                cmd_id, delivery_id, attempted_at, next_attempt_at, pubacked_at,
                hardware_id_snapshot, relay_channel_snapshot, slot_id_snapshot
            ) VALUES (
                @cmd, gen_random_uuid(), public.app_now() - {issueOffset},
                public.app_now() - {issueOffset} + interval '2 seconds', public.app_now() - {issueOffset},
                @hardware, @relay, @slot
            );

            INSERT INTO kiosk_ops.mqtt_inbox(
                event_id, kiosk_id, cmd_id, event, payload_hash, occurred_at, received_at, processed_at, disposition
            ) VALUES (
                gen_random_uuid(), @kiosk, @cmd, 'ACK', 'mock_hash_' || @cmd::text,
                public.app_now() - {issueOffset} + interval '1 second',
                public.app_now() - {issueOffset} + interval '1 second',
                public.app_now() - {issueOffset} + interval '1 second',
                'APPLIED'
            );

            -- Return the generated receipt token
            SELECT tracking_token FROM ordering.orders WHERE id = @order;
            """);

        seed.Parameters.AddWithValue("seller", f.Seller);
        seed.Parameters.AddWithValue("customer", f.Customer);
        seed.Parameters.AddWithValue("product", f.Product);
        seed.Parameters.AddWithValue("bouquet", f.Bouquet);
        seed.Parameters.AddWithValue("kiosk", f.Kiosk);
        seed.Parameters.AddWithValue("hardware", f.HardwareId);
        seed.Parameters.AddWithValue("keyHash", HashApiKey(f.RawApiKey));
        seed.Parameters.AddWithValue("slot", f.Slot);
        seed.Parameters.AddWithValue("relay", relay);
        seed.Parameters.AddWithValue("order", f.Order);
        seed.Parameters.AddWithValue("cmd", f.Command);
        seed.Parameters.AddWithValue("orderCustomer", NpgsqlDbType.Uuid, guest ? DBNull.Value : f.Customer);
        seed.Parameters.AddWithValue("doorVerified", doorVerified);
        seed.Parameters.AddWithValue("supportsRetry", supportsManualRetry);

        var trackingToken = (string)(await seed.ExecuteScalarAsync())!;
        return f with { TrackingToken = trackingToken };
    }

    private HttpClient CreateClient(
        string[]? hardwareIds = null,
        string? role = null,
        Guid? userId = null,
        Guid? sellerId = null,
        string? kioskKey = null,
        string? memberToken = null)
    {
        var clientFactory = hardwareIds is { Length: > 0 }
            ? factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                var dict = hardwareIds.ToDictionary(h => h, _ => DeviceSigningKey);
                services.AddSingleton(new DeviceKeyRing(dict));
            }))
            : factory;

        var client = clientFactory.CreateClient();

        if (memberToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", memberToken);
        }
        else if (role is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role, userId: userId, sellerId: sellerId));
        }

        if (kioskKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Kiosk-Key", kioskKey);
        }

        return client;
    }

    [Fact]
    public async Task SellerRetryUnlock_QueuesFirstRequest_AndIdempotentOnRepeat()
    {
        var f = await SetupSellerSessionAsync();
        using var client = CreateClient([f.HardwareId], role: "SELLER", userId: f.Actor, sellerId: f.Seller);

        var route = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{f.Session}/retry-unlock";
        var requestId = Guid.NewGuid();
        var payload = new { requestId };

        // 1. First attempt -> 200 OK (queued)
        var resp1 = await client.PostAsJsonAsync(route, payload);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.True(resp1.Headers.CacheControl?.NoStore);

        // 2. Immediate repeat with exact same requestId -> 200 OK (idempotent duplicate)
        var resp2 = await client.PostAsJsonAsync(route, payload);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
    }

    [Theory]
    [InlineData("PAST_DUE", "REMOVE", HttpStatusCode.OK)]
    [InlineData("PAST_DUE", "REPLACE", HttpStatusCode.NotFound)]
    [InlineData("SUSPENDED", "REMOVE", HttpStatusCode.NotFound)]
    public async Task SellerRetryUnlock_PastDueShopCanRetryOnlyRemoval(
        string shopStatus, string action, HttpStatusCode expected)
    {
        var f = await SetupSellerSessionAsync();
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var update = data.CreateCommand("""
            UPDATE identity.sellers
            SET status = @status, package_expires_at = CURRENT_DATE - 1
            WHERE id = @seller;
            UPDATE kiosk_ops.slot_maintenance_sessions SET action = @action WHERE id = @session;
            """);
        update.Parameters.AddWithValue("status", shopStatus);
        update.Parameters.AddWithValue("seller", f.Seller);
        update.Parameters.AddWithValue("action", action);
        update.Parameters.AddWithValue("session", f.Session);
        await update.ExecuteNonQueryAsync();

        using var client = CreateClient([f.HardwareId], role: "SELLER", userId: f.Actor, sellerId: f.Seller);
        var route = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{f.Session}/retry-unlock";
        var response = await client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() });
        Assert.Equal(expected, response.StatusCode);
    }


    [Fact]
    public async Task SellerRetryUnlock_RejectsSecondRequestIdForSameSession()
    {
        var f = await SetupSellerSessionAsync();
        using var client = CreateClient([f.HardwareId], role: "SELLER", userId: f.Actor, sellerId: f.Seller);

        var route = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{f.Session}/retry-unlock";

        // First requestId accepted
        var req1 = new { requestId = Guid.NewGuid() };
        var resp1 = await client.PostAsJsonAsync(route, req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // Distinct second requestId for same session -> 409 Conflict (second ID denial)
        var req2 = new { requestId = Guid.NewGuid() };
        var resp2 = await client.PostAsJsonAsync(route, req2);
        Assert.Equal(HttpStatusCode.Conflict, resp2.StatusCode);
    }

    [Fact]
    public async Task SellerRetryUnlock_RejectsWhenCooldownActive_AndAssertsRetryAfter()
    {
        // Session and command issued only 10s ago (within 30s cooldown)
        var f = await SetupSellerSessionAsync(agedPastCooldown: false);
        using var client = CreateClient([f.HardwareId], role: "SELLER", userId: f.Actor, sellerId: f.Seller);

        var route = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{f.Session}/retry-unlock";
        var resp = await client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.True(resp.Headers.Contains("Retry-After") || resp.Headers.RetryAfter != null);
    }

    [Fact]
    public async Task SellerRetryUnlock_RejectsWhenCommandStillPending()
    {
        // Command in flight with status = 'SENT' and no applied ACK
        var f = await SetupSellerSessionAsync(tokenStatus: "SENT", withAppliedAck: false);
        using var client = CreateClient([f.HardwareId], role: "SELLER", userId: f.Actor, sellerId: f.Seller);

        var route = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{f.Session}/retry-unlock";
        var resp = await client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task SellerRetryUnlock_RejectsWhenDoorOpened()
    {
        // Door already opened in stage -> 409 Conflict
        var fOpened = await SetupSellerSessionAsync(doorOpened: true);
        using var clientOpened = CreateClient([fOpened.HardwareId], role: "SELLER", userId: fOpened.Actor, sellerId: fOpened.Seller);

        var route = $"/api/me/kiosks/{fOpened.Kiosk}/slots/{fOpened.Slot}/maintenance-sessions/{fOpened.Session}/retry-unlock";
        var respOpened = await clientOpened.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Conflict, respOpened.StatusCode);
    }

    [Fact]
    public async Task SellerRetryUnlock_RejectsWhenTokenExpired()
    {
        // Token already expired -> 409 Conflict
        var fExpired = await SetupSellerSessionAsync(tokenExpired: true);
        using var clientExpired = CreateClient([fExpired.HardwareId], role: "SELLER", userId: fExpired.Actor, sellerId: fExpired.Seller);

        var route = $"/api/me/kiosks/{fExpired.Kiosk}/slots/{fExpired.Slot}/maintenance-sessions/{fExpired.Session}/retry-unlock";
        var respExpired = await clientExpired.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Conflict, respExpired.StatusCode);
    }

    [Fact]
    public async Task SellerRetryUnlock_EnforcesAuthorizationAndRejectsWrongSeller()
    {
        // Two distinct sellers with different kiosks
        var fA = await SetupSellerSessionAsync();
        var fB = await SetupSellerSessionAsync();

        var routeA = $"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{fA.Session}/retry-unlock";
        var payload = new { requestId = Guid.NewGuid() };

        // 1. Seller B calling retry-unlock on Seller A's kiosk/session -> 404 Not Found
        using var clientB = CreateClient([fA.HardwareId, fB.HardwareId], role: "SELLER", userId: fB.Actor, sellerId: fB.Seller);
        var respWrongSeller = await clientB.PostAsJsonAsync(routeA, payload);
        Assert.Equal(HttpStatusCode.NotFound, respWrongSeller.StatusCode);

        // 2. Anonymous caller -> 401 Unauthorized
        using var clientAnon = CreateClient([fA.HardwareId]);
        var respAnon = await clientAnon.PostAsJsonAsync(routeA, payload);
        Assert.Equal(HttpStatusCode.Unauthorized, respAnon.StatusCode);

        // 3. Customer caller with valid customer identity on seller route -> 403 Forbidden
        var customerId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            await using var custCmd = data.CreateCommand("""
                INSERT INTO identity.users(id, email, password_hash, full_name, role, status)
                VALUES(@id, @id::text || '@example.invalid', '!unprovisioned', 'Customer Caller', 'CUSTOMER', 'ACTIVE');
                """);
            custCmd.Parameters.AddWithValue("id", customerId);
            await custCmd.ExecuteNonQueryAsync();
        }
        using var clientCust = CreateClient([fA.HardwareId], role: "CUSTOMER", userId: customerId);
        var respCust = await clientCust.PostAsJsonAsync(routeA, payload);
        Assert.Equal(HttpStatusCode.Forbidden, respCust.StatusCode);
    }

    [Fact]
    public async Task SellerRetryUnlock_RejectsWhenHardwareKeyMissingFromKeyRing()
    {
        var f = await SetupSellerSessionAsync();
        // Hardware key missing from KeyRing (provisioned with unrelated key) -> 503 Service Unavailable
        using var clientMissingKey = CreateClient(["OTHER_PROVISIONED_HW"], role: "SELLER", userId: f.Actor, sellerId: f.Seller);

        var route = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{f.Session}/retry-unlock";
        var resp = await clientMissingKey.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task SellerRetryUnlock_RejectsWhenCapabilityDisabled()
    {
        // Capability supports_manual_retry = false -> 409 Conflict
        var f = await SetupSellerSessionAsync(supportsManualRetry: false);
        using var client = CreateClient([f.HardwareId], role: "SELLER", userId: f.Actor, sellerId: f.Seller);

        var route = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{f.Session}/retry-unlock";
        var resp = await client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task SellerRetryUnlock_RejectsLegacySnapshotOptOut()
    {
        // Legacy unhardened dispatch row where snapshot columns are NULL -> 409 Conflict
        var f = await SetupSellerSessionAsync(withSnapshots: false);
        using var client = CreateClient([f.HardwareId], role: "SELLER", userId: f.Actor, sellerId: f.Seller);

        var route = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{f.Session}/retry-unlock";
        var resp = await client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task SellerRetryUnlock_RejectsWhenSlotNotLocked()
    {
        // Slot status is STOCKED instead of LOCKED -> 409 Conflict
        var f = await SetupSellerSessionAsync(slotStatus: "STOCKED");
        using var client = CreateClient([f.HardwareId], role: "SELLER", userId: f.Actor, sellerId: f.Seller);

        var route = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{f.Session}/retry-unlock";
        var resp = await client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task CustomerRetryUnlock_QueuedForActiveCustomerMember()
    {
        var f = await SetupCustomerPickupAsync(supportsManualRetry: true, doorVerified: true, agedPastCooldown: true);
        var route = $"/api/kiosks/{f.Kiosk}/pickup/orders/{f.Order}/commands/{f.Command}/retry-unlock";

        // Issue customer member token carrying kiosk_id claim via TokenService
        string memberToken;
        using (var scope = factory.Services.CreateScope())
        {
            var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();
            memberToken = tokenService.Issue(
                new User { Id = f.Customer, Role = "CUSTOMER", FullName = "Customer Member" },
                kioskId: f.Kiosk.ToString());
        }

        using var client = CreateClient([f.HardwareId], memberToken: memberToken);
        var req = new { requestId = Guid.NewGuid(), trackingToken = (string?)null };

        // 1. Initial attempt -> 200 OK
        var resp1 = await client.PostAsJsonAsync(route, req);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.True(resp1.Headers.CacheControl?.NoStore);

        // 2. Repeat with same requestId -> 200 OK (idempotent)
        var resp2 = await client.PostAsJsonAsync(route, req);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
    }

    [Fact]
    public async Task CustomerRetryUnlock_WrongAuthenticatedMemberInSameKioskReturnsNotFound()
    {
        var f = await SetupCustomerPickupAsync();
        var route = $"/api/kiosks/{f.Kiosk}/pickup/orders/{f.Order}/commands/{f.Command}/retry-unlock";

        // Seed a separate valid customer in DB
        var otherCustomerId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            await using var seedCmd = data.CreateCommand("""
                INSERT INTO identity.users(id, email, password_hash, full_name, role, status)
                VALUES(@id, @id::text || '@example.invalid', '!unprovisioned', 'Other Customer', 'CUSTOMER', 'ACTIVE');
                """);
            seedCmd.Parameters.AddWithValue("id", otherCustomerId);
            await seedCmd.ExecuteNonQueryAsync();
        }

        // Issue member token for other customer on the same kiosk
        string otherMemberToken;
        using (var scope = factory.Services.CreateScope())
        {
            var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();
            otherMemberToken = tokenService.Issue(
                new User { Id = otherCustomerId, Role = "CUSTOMER", FullName = "Other Customer" },
                kioskId: f.Kiosk.ToString());
        }

        using var client = CreateClient([f.HardwareId], memberToken: otherMemberToken);
        var resp = await client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid(), trackingToken = (string?)null });

        // Order does not belong to otherCustomer -> exact 404 Not Found
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task CustomerRetryUnlock_KioskGuestFlowWithExactReceiptWrongReceiptAndAnonymous()
    {
        var f = await SetupCustomerPickupAsync(guest: true);
        var route = $"/api/kiosks/{f.Kiosk}/pickup/orders/{f.Order}/commands/{f.Command}/retry-unlock";

        // 1. Kiosk guest with valid X-Kiosk-Key and exact 8-character receipt token -> strict 200 OK
        using var guestClient = CreateClient([f.HardwareId], kioskKey: f.RawApiKey);
        var reqExact = new { requestId = Guid.NewGuid(), trackingToken = (string?)f.TrackingToken };
        var respExact = await guestClient.PostAsJsonAsync(route, reqExact);
        Assert.Equal(HttpStatusCode.OK, respExact.StatusCode);
        Assert.True(respExact.Headers.CacheControl?.NoStore);

        // 2. Kiosk guest with valid X-Kiosk-Key but wrong receipt token -> strict 404 Not Found
        var reqWrong = new { requestId = Guid.NewGuid(), trackingToken = "WRONGREC" };
        var respWrong = await guestClient.PostAsJsonAsync(route, reqWrong);
        Assert.Equal(HttpStatusCode.NotFound, respWrong.StatusCode);

        // 3. Anonymous caller without X-Kiosk-Key header and without bearer token -> strict 401 Unauthorized
        using var anonClient = CreateClient([f.HardwareId]);
        var respAnon = await anonClient.PostAsJsonAsync(route, reqExact);
        Assert.Equal(HttpStatusCode.Unauthorized, respAnon.StatusCode);
    }

    [Fact]
    public async Task DeliveryRetriesIdenticalSignedPayloadAndPublishesOnce()
    {
        var f = await SetupSellerSessionAsync();
        using var client = CreateClient([f.HardwareId], role: "SELLER", userId: f.Actor, sellerId: f.Seller);
        var requestId = Guid.NewGuid();
        var route = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{f.Session}/retry-unlock";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(route, new { requestId })).StatusCode);

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var publisher = scope.ServiceProvider.GetRequiredService<ICapPublisher>();
        var keys = new DeviceKeyRing(new Dictionary<string, byte[]> { [f.HardwareId] = DeviceSigningKey });
        var dispatcher = new ManualUnlockRetryDispatcher(data, publisher, keys, new ManualUnlockRetryService(data, publisher, keys));

        Assert.Contains(requestId, await dispatcher.PendingAsync(CancellationToken.None));
        var first = Assert.IsType<PreparedUnlockRetry>(await dispatcher.PrepareAsync(requestId, CancellationToken.None));
        Assert.True(DeviceRetryProtocol.Verify(first.Command, f.HardwareId, DeviceSigningKey, DateTimeOffset.UtcNow));
        Assert.Equal(f.Command, first.Command.CommandId);
        Assert.Equal(requestId, first.Command.RequestId);

        await Assert.ThrowsAsync<IOException>(() => dispatcher.DeliverAsync(first,
            (_, _) => Task.FromException(new IOException("Simulated broker rejection")), CancellationToken.None));
        Assert.Null(await dispatcher.PrepareAsync(requestId, CancellationToken.None));
        await using (var due = data.CreateCommand("""
            UPDATE kiosk_ops.manual_unlock_retries SET next_attempt_at = CURRENT_TIMESTAMP WHERE request_id = @request
            """))
        {
            due.Parameters.AddWithValue("request", requestId);
            await due.ExecuteNonQueryAsync();
        }

        var second = Assert.IsType<PreparedUnlockRetry>(await dispatcher.PrepareAsync(requestId, CancellationToken.None));
        Assert.Equal(first.Command, second.Command);
        Assert.NotEqual(first.DeliveryId, second.DeliveryId);

        var publishes = 0;
        Task Publish(UnlockRetryCommand message, CancellationToken _)
        {
            Assert.Equal(second.Command, message);
            publishes++;
            return Task.CompletedTask;
        }
        Assert.False(await dispatcher.DeliverAsync(first, Publish, CancellationToken.None));
        Assert.True(await dispatcher.DeliverAsync(second, Publish, CancellationToken.None));
        Assert.False(await dispatcher.DeliverAsync(second, Publish, CancellationToken.None));
        Assert.Equal(1, publishes);

        await using var verify = data.CreateCommand("""
            SELECT r.state = 'PUBLISHED' AND r.published_at IS NOT NULL AND t.status = 'ACKED' AND t.attempts = 1
            FROM kiosk_ops.manual_unlock_retries r JOIN kiosk_ops.unlock_tokens t ON t.cmd_id = r.parent_cmd_id
            WHERE r.request_id = @request
            """);
        verify.Parameters.AddWithValue("request", requestId);
        Assert.Equal(true, await verify.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("CAPABILITY")]
    [InlineData("OPENED")]
    [InlineData("TARGET")]
    public async Task ChangeAfterPreparationPreventsPublish(string change)
    {
        var f = await SetupSellerSessionAsync();
        using var client = CreateClient([f.HardwareId], role: "SELLER", userId: f.Actor, sellerId: f.Seller);
        var requestId = Guid.NewGuid();
        var route = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{f.Session}/retry-unlock";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(route, new { requestId })).StatusCode);

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var publisher = scope.ServiceProvider.GetRequiredService<ICapPublisher>();
        var keys = new DeviceKeyRing(new Dictionary<string, byte[]> { [f.HardwareId] = DeviceSigningKey });
        var dispatcher = new ManualUnlockRetryDispatcher(data, publisher, keys, new ManualUnlockRetryService(data, publisher, keys));
        var prepared = Assert.IsType<PreparedUnlockRetry>(await dispatcher.PrepareAsync(requestId, CancellationToken.None));

        var sql = change switch
        {
            "CAPABILITY" => "UPDATE kiosk_ops.slot_sensor_capabilities SET supports_manual_retry = false WHERE slot_id = @slot",
            "TARGET" => "UPDATE kiosk_ops.manual_unlock_retries SET relay_channel_snapshot = 1 WHERE request_id = @request",
            _ => """
                INSERT INTO kiosk_ops.mqtt_inbox(event_id, kiosk_id, cmd_id, event, payload_hash, occurred_at)
                VALUES(gen_random_uuid(), @kiosk, @cmd, 'OPENED', 'synthetic-opened', CURRENT_TIMESTAMP)
                """
        };
        await using var mutate = data.CreateCommand(sql);
        mutate.Parameters.AddWithValue("slot", f.Slot);
        mutate.Parameters.AddWithValue("request", requestId);
        mutate.Parameters.AddWithValue("kiosk", f.Kiosk);
        mutate.Parameters.AddWithValue("cmd", f.Command);
        await mutate.ExecuteNonQueryAsync();

        Assert.False(await dispatcher.DeliverAsync(prepared,
            (_, _) => throw new InvalidOperationException("A revoked retry must never publish"), CancellationToken.None));
    }
}
