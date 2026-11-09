using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Tests;

public sealed class SellerSlotMaintenanceIntegrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record Fixture(Guid Seller, Guid Actor, Guid Kiosk, Guid Slot, Guid Product, Guid Bouquet);

    private async Task<Fixture> SetupAsync(
        string slotStatus = "STOCKED",
        string? zone = "WALK_IN",
        bool capabilityEnabled = true,
        bool isLeasePaid = true,
        int relay = 0)
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // 1. Insert seller, user, kiosk, and slot with FREE status
        await using var seedSeller = data.CreateCommand("""
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
            VALUES(@seller, 'Maintenance Shop ' || @seller::text, '0901234567', 'ACTIVE', '20000000-0000-0000-0000-00000000000d',
                   CASE WHEN @isLeasePaid THEN CURRENT_DATE + 30 ELSE CURRENT_DATE - 1 END);

            INSERT INTO identity.users(id, email, password_hash, full_name, role, seller_id, status)
            VALUES(@actor, @actor::text || '@example.invalid', '!unprovisioned', 'Maintenance Actor', 'SELLER', @seller, 'ACTIVE');

            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status, seller_id, last_heartbeat_at)
            VALUES(@kiosk, @kiosk::text, 'Maintenance Kiosk', '123 Test St', 'HCM', @kiosk::text, @kiosk::text, 'ONLINE', @seller, public.app_now());

            INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel, status, current_seller_id, bouquet_id, zone)
            VALUES(@slot, @kiosk, 'W01', @relay, 'FREE', NULL, NULL, @zone);
            """);

        seedSeller.Parameters.AddWithValue("seller", f.Seller);
        seedSeller.Parameters.AddWithValue("actor", f.Actor);
        seedSeller.Parameters.AddWithValue("kiosk", f.Kiosk);
        seedSeller.Parameters.AddWithValue("slot", f.Slot);
        seedSeller.Parameters.AddWithValue("relay", relay);
        seedSeller.Parameters.AddWithValue("zone", NpgsqlDbType.Text, (object?)zone ?? DBNull.Value);
        seedSeller.Parameters.AddWithValue("isLeasePaid", isLeasePaid);
        await seedSeller.ExecuteNonQueryAsync();

        // 2. Assign kiosk while slot is FREE
        if (isLeasePaid)
        {
            await CurrentMainKioskFixture.AssignKioskWithLeaseAsync(data, f.Kiosk, f.Seller,
                Guid.Parse("20000000-0000-0000-0000-00000000000d"));
        }
        else
        {
            await using var assignCmd = data.CreateCommand("""
                INSERT INTO kiosk_ops.slot_assignments(slot_id, seller_id, assigned_by, status)
                VALUES(@slot, @seller, '10000000-0000-0000-0000-000000000001', 'ACTIVE');
                UPDATE kiosk_ops.slots SET current_seller_id = @seller, status = 'RENTED_EMPTY' WHERE id = @slot;
                """);
            assignCmd.Parameters.AddWithValue("slot", f.Slot);
            assignCmd.Parameters.AddWithValue("seller", f.Seller);
            await assignCmd.ExecuteNonQueryAsync();
        }

        // 3. Insert product, bouquet and update slot to target status
        await using var seedProduct = data.CreateCommand("""
            INSERT INTO catalog.flower_products(id, seller_id, name, price, shelf_life_hours, status, quantity)
            VALUES(@product, @seller, 'Maintenance Flower', 250000, 48, 'ACTIVE', 1);

            INSERT INTO kiosk_ops.bouquets(id, product_id, seller_id, qr_code, price_snapshot, status, stocked_at, sellable_until)
            VALUES(@bouquet, @product, @seller, @bouquet::text, 250000,
                   CASE WHEN @status = 'PENDING_REMOVAL' THEN 'SOLD' ELSE 'STOCKED' END,
                   public.app_now(), public.app_now() + interval '48 hours');

            UPDATE kiosk_ops.slots
            SET status = @status, bouquet_id = CASE WHEN @status = 'RENTED_EMPTY' THEN NULL ELSE @bouquet END
            WHERE id = @slot;
            """);
        seedProduct.Parameters.AddWithValue("product", f.Product);
        seedProduct.Parameters.AddWithValue("seller", f.Seller);
        seedProduct.Parameters.AddWithValue("bouquet", f.Bouquet);
        seedProduct.Parameters.AddWithValue("slot", f.Slot);
        seedProduct.Parameters.AddWithValue("status", slotStatus);
        await seedProduct.ExecuteNonQueryAsync();

        // 4. Register sensor capability if enabled
        if (capabilityEnabled)
        {
            await using var capCmd = data.CreateCommand("""
                INSERT INTO kiosk_ops.slot_sensor_capabilities(slot_id, kiosk_id, relay_channel, enabled, door_verified, presence_verified)
                VALUES(@slot, @kiosk, @relay, true, true, true)
                ON CONFLICT (slot_id) DO UPDATE SET enabled=true, door_verified=true, presence_verified=true;
                """);
            capCmd.Parameters.AddWithValue("slot", f.Slot);
            capCmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            capCmd.Parameters.AddWithValue("relay", relay);
            await capCmd.ExecuteNonQueryAsync();
        }

        return f;
    }

    private HttpClient Client(Fixture f, string role = "SELLER")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role, userId: f.Actor, sellerId: f.Seller));
        return client;
    }

    [Theory]
    [InlineData("LOAD")]
    [InlineData("REPLACE")]
    public async Task ExhaustedLoadingRemainsRetryableAndDoesNotBlockOtherSensorSessions(string action)
    {
        var f = await SetupAsync(slotStatus: action == "LOAD" ? "RENTED_EMPTY" : "STOCKED");
        using var client = Client(f);
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var session = Guid.NewGuid();
        var otherSession = Guid.NewGuid();
        var otherSlot = Guid.NewGuid();
        var otherProduct = Guid.NewGuid();
        var root = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions";

        async Task<object?> Sql(string sql, params (string Name, object Value)[] extra)
        {
            await using var cmd = data.CreateCommand(sql);
            foreach (var (name, value) in new[]
            {
                ("seller", f.Seller), ("actor", f.Actor), ("kiosk", f.Kiosk), ("slot", f.Slot),
                ("product", f.Product), ("session", session), ("otherSession", otherSession),
                ("otherSlot", otherSlot), ("otherProduct", otherProduct)
            }) cmd.Parameters.AddWithValue(name, value);
            foreach (var (name, value) in extra) cmd.Parameters.AddWithValue(name, value);
            return await cmd.ExecuteScalarAsync();
        }

        const string presence = """
            INSERT INTO kiosk_ops.flower_presence(kiosk_id,event_id,observed_at,states,received_at)
            VALUES(@kiosk,gen_random_uuid(),(extract(epoch from clock_timestamp())*1000)::bigint+10,
              ARRAY[@firstState,@secondState,-1,-1],clock_timestamp())
            ON CONFLICT(kiosk_id) DO UPDATE SET states=excluded.states,
              observed_at=excluded.observed_at,received_at=excluded.received_at;
            """;
        async Task DoorCycle(Guid command, int firstState, int secondState)
        {
            await Sql("""
                SELECT flow.reset_clock();
                SELECT flow.device_event(@cmd,'SENT');
                SELECT flow.device_event(@cmd,'ACK');
                SELECT flow.device_event(@cmd,'OPENED');
                SELECT flow.device_event(@cmd,'CLOSED');
                """ + presence, ("cmd", command), ("firstState", firstState), ("secondState", secondState));
        }

        await Sql(presence, ("firstState", 0), ("secondState", 0));
        var start = await client.PostAsJsonAsync(root,
            new StartSlotMaintenanceRequest(session, action, action == "REPLACE" ? f.Bouquet : null,
                f.Product, "Load with a concurrent stock adjustment"));
        start.EnsureSuccessStatusCode();
        var active = (await start.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>())!;
        if (action == "REPLACE")
        {
            await DoorCycle(active.CurrentCmdId!.Value, 0, 0);
            (await client.PostAsync($"{root}/{session}/check", null)).EnsureSuccessStatusCode();
            var open = await client.PostAsync($"{root}/{session}/open-next", null);
            open.EnsureSuccessStatusCode();
            active = (await open.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>())!;
        }

        await Sql("""
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status,quantity)
            VALUES(@otherProduct,@seller,'Other maintenance flower',250000,48,'ACTIVE',1);
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,status,current_seller_id,zone)
            VALUES(@otherSlot,@kiosk,'W02',1,'RENTED_EMPTY',@seller,'WALK_IN');
            INSERT INTO kiosk_ops.slot_assignments(slot_id,seller_id,assigned_by)
            VALUES(@otherSlot,@seller,@actor);
            INSERT INTO kiosk_ops.slot_sensor_capabilities(slot_id,kiosk_id,relay_channel,enabled,door_verified,presence_verified)
            VALUES(@otherSlot,@kiosk,1,true,true,true);
            """);
        await Sql(presence, ("firstState", 0), ("secondState", 0));
        var otherStart = await client.PostAsJsonAsync(
            $"/api/me/kiosks/{f.Kiosk}/slots/{otherSlot}/maintenance-sessions",
            new StartSlotMaintenanceRequest(otherSession, "LOAD", null, otherProduct, "Load another available product"));
        otherStart.EnsureSuccessStatusCode();
        var other = (await otherStart.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>())!;

        // Simulate another operator reconciling the shop count after doors were
        // opened. Final confirmation must still enforce the latest quantity.
        await Sql("UPDATE catalog.flower_products SET quantity=0 WHERE id=@product");
        await DoorCycle(active.CurrentCmdId!.Value, 1, 1);
        await DoorCycle(other.CurrentCmdId!.Value, 1, 1);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"{root}/{session}/check", null)).StatusCode);
        Assert.Equal("LOCKED", await Sql("SELECT status FROM kiosk_ops.slots WHERE id=@slot"));
        Assert.Equal("AwaitLoading", await Sql("SELECT stage FROM kiosk_ops.slot_maintenance_sessions WHERE id=@session"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM kiosk_ops.product_quantity_deductions WHERE product_id=@product"));

        await scope.ServiceProvider.GetRequiredService<SlotMaintenanceService>()
            .SyncActiveSessionsForKioskAsync(f.Kiosk, default);
        Assert.Equal("AwaitLoading", await Sql("SELECT stage FROM kiosk_ops.slot_maintenance_sessions WHERE id=@session"));
        Assert.Equal("Completed", await Sql("SELECT stage FROM kiosk_ops.slot_maintenance_sessions WHERE id=@otherSession"));
        Assert.Equal(0, await Sql("SELECT quantity FROM catalog.flower_products WHERE id=@otherProduct"));

        // After an explicit shop correction the same closed-door evidence and
        // session can complete; retrying completion cannot deduct again.
        await Sql("UPDATE catalog.flower_products SET quantity=1 WHERE id=@product");
        var retry = await client.PostAsync($"{root}/{session}/check", null);
        retry.EnsureSuccessStatusCode();
        Assert.Equal("Completed", (await retry.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>())!.Stage);
        (await client.PostAsync($"{root}/{session}/check", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, await Sql("SELECT quantity FROM catalog.flower_products WHERE id=@product"));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM kiosk_ops.product_quantity_deductions WHERE product_id=@product"));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM kiosk_ops.inventory_logs WHERE slot_id=@slot AND movement_type='STOCK_IN'"));
    }

    [Fact]
    public async Task PastDueSellerCanStartAndCompleteRemovalWithTelemetryButCannotLoadOrReplace()
    {
        var f = await SetupAsync(isLeasePaid: false);
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using (var updateSeller = data.CreateCommand("UPDATE identity.sellers SET status = 'PAST_DUE' WHERE id = @seller"))
        {
            updateSeller.Parameters.AddWithValue("seller", f.Seller);
            await updateSeller.ExecuteNonQueryAsync();
        }

        using var client = Client(f);

        // Cannot LOAD (403 Forbidden for PAST_DUE)
        var loadReq = new StartSlotMaintenanceRequest(Guid.NewGuid(), "LOAD", null, f.Product, "Past due load attempt");
        var loadResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", loadReq);
        Assert.Equal(HttpStatusCode.Forbidden, loadResp.StatusCode);

        // Cannot REPLACE (403 Forbidden for PAST_DUE)
        var replaceReq = new StartSlotMaintenanceRequest(Guid.NewGuid(), "REPLACE", f.Bouquet, f.Product, "Past due replace attempt");
        var replaceResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", replaceReq);
        Assert.Equal(HttpStatusCode.Forbidden, replaceResp.StatusCode);

        // Can start REMOVE
        var sessionId = Guid.NewGuid();
        var removeReq = new StartSlotMaintenanceRequest(sessionId, "REMOVE", f.Bouquet, null, "Past due remove bouquet");
        var removeResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", removeReq);
        Assert.Equal(HttpStatusCode.OK, removeResp.StatusCode);

        var session = await removeResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.NotNull(session);
        Assert.Equal("AwaitRemoval", session.Stage);
        Assert.NotNull(session.CurrentCmdId);

        // Cross-tenant guard: foreign seller cannot check this session -> 404
        var foreignFixture = await SetupAsync(isLeasePaid: false);
        using var foreignClient = Client(foreignFixture);
        var crossTenantResp = await foreignClient.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.NotFound, crossTenantResp.StatusCode);

        // Revocation guard: CLOSED shop cannot start REMOVE -> 403 Forbidden
        var closedFixture = await SetupAsync(isLeasePaid: false);
        await using (var closeCmd = data.CreateCommand("UPDATE identity.sellers SET status = 'CLOSED' WHERE id = @seller"))
        {
            closeCmd.Parameters.AddWithValue("seller", closedFixture.Seller);
            await closeCmd.ExecuteNonQueryAsync();
        }
        using var closedClient = Client(closedFixture);
        var closedRemoveReq = new StartSlotMaintenanceRequest(Guid.NewGuid(), "REMOVE", closedFixture.Bouquet, null, "Closed shop remove");
        var closedResp = await closedClient.PostAsJsonAsync($"/api/me/kiosks/{closedFixture.Kiosk}/slots/{closedFixture.Slot}/maintenance-sessions", closedRemoveReq);
        Assert.Equal(HttpStatusCode.Forbidden, closedResp.StatusCode);

        // Exercise the real dispatcher with a test signing key and an in-process publisher.
        var keys = new DeviceKeyRing(new Dictionary<string, byte[]> { [f.Kiosk.ToString()] = new byte[32] });
        var dispatcher = new DeviceCommandDispatcher(data, scope.ServiceProvider.GetRequiredService<DotNetCore.CAP.ICapPublisher>(), keys);
        var prepared = Assert.IsType<PreparedUnlock>(await dispatcher.PrepareAsync(session.CurrentCmdId.Value, CancellationToken.None));
        var delivered = await dispatcher.DeliverAsync(prepared, (command, _) =>
        {
            Assert.Equal(session.CurrentCmdId.Value, command.CommandId);
            Assert.True(keys.Verify(command, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.True(delivered);

        // Simulate ACK -> OPENED -> CLOSED and fresh empty sensor.
        await using (var doorCmd = data.CreateCommand("""
            SELECT flow.reset_clock();
            SELECT flow.device_event(@cmd, 'ACK');
            SELECT flow.device_event(@cmd, 'OPENED');
            SELECT flow.device_event(@cmd, 'CLOSED');

            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), (extract(epoch from clock_timestamp()) * 1000)::bigint + 10, ARRAY[0,-1,-1,-1], clock_timestamp())
            ON CONFLICT (kiosk_id) DO UPDATE SET states=excluded.states, observed_at=excluded.observed_at, received_at=excluded.received_at;
            """))
        {
            doorCmd.Parameters.AddWithValue("cmd", session.CurrentCmdId.Value);
            doorCmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            await doorCmd.ExecuteNonQueryAsync();
        }

        // Check -> completes REMOVE
        var checkResp = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.OK, checkResp.StatusCode);
        var completedSession = await checkResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.NotNull(completedSession);
        Assert.Equal("Completed", completedSession.Stage);

        // Verify slot is RENTED_EMPTY and bouquet is RETURNED
        await using (var verifyCmd = data.CreateCommand("""
            SELECT s.status, b.status,
                   (SELECT count(*) FROM kiosk_ops.inventory_logs WHERE slot_id = @slot AND movement_type = 'RETURN_SELLER')
            FROM kiosk_ops.slots s CROSS JOIN kiosk_ops.bouquets b
            WHERE s.id = @slot AND b.id = @bouquet;
            """))
        {
            verifyCmd.Parameters.AddWithValue("slot", f.Slot);
            verifyCmd.Parameters.AddWithValue("bouquet", f.Bouquet);
            await using var reader = await verifyCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("RENTED_EMPTY", reader.GetString(0));
            Assert.Equal("RETURNED", reader.GetString(1));
            Assert.Equal(1L, reader.GetInt64(2));
        }
    }

    [Fact]
    public async Task ContractExportsMaintenanceSessionEndpoints()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");

        var paths = document.GetProperty("paths");
        var sessionPath = "/api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions";
        Assert.True(paths.TryGetProperty(sessionPath, out var sessionItem));
        Assert.True(sessionItem.TryGetProperty("post", out _));

        var sessionDetailPath = "/api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions/{sessionId}";
        Assert.True(paths.TryGetProperty(sessionDetailPath, out var detailItem));
        Assert.True(detailItem.TryGetProperty("get", out _));

        var checkPath = "/api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions/{sessionId}/check";
        Assert.True(paths.TryGetProperty(checkPath, out _));

        var openNextPath = "/api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions/{sessionId}/open-next";
        Assert.True(paths.TryGetProperty(openNextPath, out _));

        var reportIssuePath = "/api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions/{sessionId}/report-issue";
        Assert.True(paths.TryGetProperty(reportIssuePath, out _));

        var openForCheckPath = "/api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions/{sessionId}/open-for-check";
        Assert.True(paths.TryGetProperty(openForCheckPath, out _));

        var adminCapPath = "/api/admin/kiosks/{kioskId}/slots/{slotId}/sensor-capabilities";
        Assert.True(paths.TryGetProperty(adminCapPath, out var capItem));
        Assert.True(capItem.TryGetProperty("put", out _));

        var exportPath = Environment.GetEnvironmentVariable("EXPORT_OPENAPI_PATH");
        if (!string.IsNullOrEmpty(exportPath))
            await File.WriteAllTextAsync(exportPath, document.GetRawText());
    }

    [Fact]
    public async Task BeginSessionEnforcesRoleAndTenant()
    {
        var f = await SetupAsync();
        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var request = new StartSlotMaintenanceRequest(sessionId, "REMOVE", f.Bouquet, null, "Thu hoi hoa test");

        // Anonymous -> 401
        using var anon = factory.CreateClient();
        var anonResp = await anon.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // Mismatched token (SELLER in DB but token claims CUSTOMER) -> 401
        using var mismatchedClient = Client(f, "CUSTOMER");
        var mismatchedResp = await mismatchedClient.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", request);
        Assert.Equal(HttpStatusCode.Unauthorized, mismatchedResp.StatusCode);

        // Legitimate separate active CUSTOMER user in DB -> 403 Forbidden
        var customerId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            await using var custCmd = data.CreateCommand("""
                INSERT INTO identity.users(id, email, password_hash, full_name, role, status)
                VALUES(@id, @id::text || '@example.invalid', '!unprovisioned', 'Legit Customer', 'CUSTOMER', 'ACTIVE');
                """);
            custCmd.Parameters.AddWithValue("id", customerId);
            await custCmd.ExecuteNonQueryAsync();
        }
        using var legitCustomerClient = factory.CreateClient();
        legitCustomerClient.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("CUSTOMER", userId: customerId));
        var custResp = await legitCustomerClient.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", request);
        Assert.Equal(HttpStatusCode.Forbidden, custResp.StatusCode);

        // Foreign kiosk -> 404
        var foreignKiosk = Guid.NewGuid();
        var foreignResp = await client.PostAsJsonAsync($"/api/me/kiosks/{foreignKiosk}/slots/{f.Slot}/maintenance-sessions", request);
        Assert.Equal(HttpStatusCode.NotFound, foreignResp.StatusCode);
    }

    [Fact]
    public async Task CrossShopReadAndSyncMutationBlocked()
    {
        var fA = await SetupAsync();
        var fB = await SetupAsync();
        using var clientA = Client(fA);
        using var clientB = Client(fB);

        var sessionAId = Guid.NewGuid();
        var startReq = new StartSlotMaintenanceRequest(sessionAId, "REMOVE", fA.Bouquet, null, "Bao tri shop A");
        var startResp = await clientA.PostAsJsonAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions", startReq);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);

        // Seller B cannot GET Seller A's session -> 404
        var getByB = await clientB.GetAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionAId}");
        Assert.Equal(HttpStatusCode.NotFound, getByB.StatusCode);

        // Seller B cannot Check Seller A's session -> 404
        var checkByB = await clientB.PostAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionAId}/check", null);
        Assert.Equal(HttpStatusCode.NotFound, checkByB.StatusCode);

        // Seller B cannot OpenNext Seller A's session -> 404
        var openNextByB = await clientB.PostAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionAId}/open-next", null);
        Assert.Equal(HttpStatusCode.NotFound, openNextByB.StatusCode);

        // Seller B cannot ReportIssue on Seller A's session -> 404
        var reportByB = await clientB.PostAsJsonAsync(
            $"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionAId}/report-issue",
            new ReportIssueRequest("Loi gia mao"));
        Assert.Equal(HttpStatusCode.NotFound, reportByB.StatusCode);

        // Seller B cannot reuse Seller A's session ID on B's kiosk without authorization -> 404
        var spoofStart = new StartSlotMaintenanceRequest(sessionAId, "REMOVE", fB.Bouquet, null, "Spoof session id");
        var spoofResp = await clientB.PostAsJsonAsync($"/api/me/kiosks/{fB.Kiosk}/slots/{fB.Slot}/maintenance-sessions", spoofStart);
        Assert.Equal(HttpStatusCode.NotFound, spoofResp.StatusCode);

        // Verify Seller A session and Seller B slot are completely unchanged
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using (var verifyCmd = data.CreateCommand("""
            SELECT (SELECT stage FROM kiosk_ops.slot_maintenance_sessions WHERE id = @sessionA) = 'AwaitRemoval',
                   (SELECT status FROM kiosk_ops.slots WHERE id = @slotB) = 'STOCKED';
            """))
        {
            verifyCmd.Parameters.AddWithValue("sessionA", sessionAId);
            verifyCmd.Parameters.AddWithValue("slotB", fB.Slot);
            await using var reader = await verifyCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        }
    }

    [Fact]
    public async Task CapabilityDisabledBlocksSessionStart()
    {
        var f = await SetupAsync(capabilityEnabled: false);
        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var request = new StartSlotMaintenanceRequest(sessionId, "REMOVE", f.Bouquet, null, "Thu hoi hoa test");

        var resp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", request);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task IdempotentBeginReturnsSameSessionOrConflictOnChangedPayload()
    {
        var f = await SetupAsync();
        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var request = new StartSlotMaintenanceRequest(sessionId, "REMOVE", f.Bouquet, null, "Thu hoi hoa test");

        var resp1 = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", request);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.True(resp1.Headers.CacheControl?.NoStore);
        var body1 = await resp1.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal(sessionId, body1!.Id);
        Assert.Equal("AwaitRemoval", body1.Stage);

        // Repeat identical -> 200 OK
        var resp2 = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", request);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal(sessionId, body2!.Id);

        // Changed payload with same ID -> 409 Conflict
        var changedRequest = request with { Reason = "Ly do khac hoan toan" };
        var resp3 = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", changedRequest);
        Assert.Equal(HttpStatusCode.Conflict, resp3.StatusCode);
    }

    [Fact]
    public async Task ReusedSlotWithCompletedOldBouquetHistoryAllowsLoad()
    {
        var f = await SetupAsync(slotStatus: "RENTED_EMPTY");
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // Insert historical completed order referencing old bouquet on this slot
        var oldOrderId = Guid.NewGuid();
        var oldBouquetId = Guid.NewGuid();
        await using (var histCmd = data.CreateCommand("""
            INSERT INTO kiosk_ops.bouquets(id, product_id, seller_id, qr_code, price_snapshot, status, stocked_at, sellable_until)
            VALUES(@oldBouquet, @product, @seller, @oldBouquet::text, 250000, 'PICKED_UP', public.app_now()-interval '10 days', public.app_now()-interval '8 days');

            INSERT INTO ordering.orders(id, order_code, checkout_id, customer_id, seller_id, kiosk_id, status, subtotal, total_amount, tracking_token, completed_at)
            VALUES(@order, @order::text, @order, NULL, @seller, @kiosk, 'COMPLETED', 250000, 250000, @order::text, public.app_now()-interval '9 days');

            INSERT INTO ordering.order_items(order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total)
            VALUES(@order, 'BOUQUET', @oldBouquet, @slot, 'Historical Flower', 1, 250000, 250000);

            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), extract(epoch from public.app_now())*1000, ARRAY[0,-1,-1,-1], public.app_now())
            ON CONFLICT (kiosk_id) DO UPDATE SET states=excluded.states, observed_at=excluded.observed_at, received_at=excluded.received_at;
            """))
        {
            histCmd.Parameters.AddWithValue("oldBouquet", oldBouquetId);
            histCmd.Parameters.AddWithValue("product", f.Product);
            histCmd.Parameters.AddWithValue("seller", f.Seller);
            histCmd.Parameters.AddWithValue("order", oldOrderId);
            histCmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            histCmd.Parameters.AddWithValue("slot", f.Slot);
            await histCmd.ExecuteNonQueryAsync();
        }

        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var loadReq = new StartSlotMaintenanceRequest(sessionId, "LOAD", null, f.Product, "Nap hoa moi vao o tung co lich su don");
        var loadResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", loadReq);
        Assert.Equal(HttpStatusCode.OK, loadResp.StatusCode);
        var body = await loadResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("AwaitLoading", body!.Stage);
    }

    [Fact]
    public async Task LoadRequiresFreshClosedEmptyBaseline()
    {
        var f = await SetupAsync(slotStatus: "RENTED_EMPTY");
        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var request = new StartSlotMaintenanceRequest(sessionId, "LOAD", null, f.Product, "Nap hoa moi vao o");

        // Without flower presence snapshot -> 409 Conflict
        var respNoPresence = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", request);
        Assert.Equal(HttpStatusCode.Conflict, respNoPresence.StatusCode);

        // Seed flower presence with flower detected (states[0] = 1) -> 409 Conflict (not empty)
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using (var cmd = data.CreateCommand("""
            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), extract(epoch from public.app_now())*1000, ARRAY[1,-1,-1,-1], public.app_now())
            ON CONFLICT (kiosk_id) DO UPDATE SET states = excluded.states, observed_at = excluded.observed_at, received_at = excluded.received_at;
            """))
        {
            cmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            await cmd.ExecuteNonQueryAsync();
        }

        var respNotEmpty = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", request);
        Assert.Equal(HttpStatusCode.Conflict, respNotEmpty.StatusCode);

        // Update flower presence to empty (states[0] = 0) -> 200 OK
        await using (var cmd = data.CreateCommand("""
            UPDATE kiosk_ops.flower_presence
            SET states = ARRAY[0,-1,-1,-1], observed_at = extract(epoch from public.app_now())*1000, received_at = public.app_now()
            WHERE kiosk_id = @kiosk;
            """))
        {
            cmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            await cmd.ExecuteNonQueryAsync();
        }

        var respSuccess = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", request);
        Assert.Equal(HttpStatusCode.OK, respSuccess.StatusCode);
        var body = await respSuccess.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("AwaitLoading", body!.Stage);
    }

    [Fact]
    public async Task ExpiredLeaseBlocksLoadAndReplaceAllowsRemoval()
    {
        var f = await SetupAsync(isLeasePaid: false);
        using var client = Client(f);

        // Load blocked
        var loadReq = new StartSlotMaintenanceRequest(Guid.NewGuid(), "LOAD", null, f.Product, "Nap hoa het han");
        var loadResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", loadReq);
        Assert.Equal(HttpStatusCode.Conflict, loadResp.StatusCode);

        // Replace blocked
        var repReq = new StartSlotMaintenanceRequest(Guid.NewGuid(), "REPLACE", f.Bouquet, f.Product, "Doi hoa het han");
        var repResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", repReq);
        Assert.Equal(HttpStatusCode.Conflict, repResp.StatusCode);

        // Remove allowed
        var remReq = new StartSlotMaintenanceRequest(Guid.NewGuid(), "REMOVE", f.Bouquet, null, "Thu hoi hoa het han");
        var remResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", remReq);
        Assert.Equal(HttpStatusCode.OK, remResp.StatusCode);
    }

    [Fact]
    public async Task LegacyRoutesBlockedWhenCapabilityEnabled()
    {
        var f = await SetupAsync(capabilityEnabled: true);
        using var client = Client(f);

        // Direct delete route should be rejected with 409
        var delResp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/bouquet")
        {
            Content = JsonContent.Create(new RemoveSlotBouquetRequest(f.Bouquet, "Thu hoi hoa direct", true))
        });
        Assert.Equal(HttpStatusCode.Conflict, delResp.StatusCode);

        // Legacy return_to_seller flow should be rejected with 409
        var flowResp = await client.PostAsJsonAsync($"/api/sellers/{f.Seller}/flows/return_to_seller", new
        {
            p_slot = f.Slot,
            p_reason = "Thu hoi hoa legacy flow",
            p_photo_url = "https://example.invalid/proof.jpg",
            p_damaged = false
        });
        Assert.Equal(HttpStatusCode.Conflict, flowResp.StatusCode);
    }

    [Fact]
    public async Task ForeignDeleteSlotBouquetReturnsNotFound()
    {
        var fA = await SetupAsync(capabilityEnabled: true);
        var fB = await SetupAsync(capabilityEnabled: true);
        using var clientA = Client(fA);

        // Seller A calling DELETE on Seller B's slot returns 404 (not 409)
        var delResp = await clientA.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"/api/me/kiosks/{fA.Kiosk}/slots/{fB.Slot}/bouquet")
        {
            Content = JsonContent.Create(new RemoveSlotBouquetRequest(fB.Bouquet, "Thu hoi foreign slot", true))
        });
        Assert.Equal(HttpStatusCode.NotFound, delResp.StatusCode);
    }

    [Fact]
    public async Task ReportIssueTransitionsToNeedsInspection()
    {
        var f = await SetupAsync();
        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var startReq = new StartSlotMaintenanceRequest(sessionId, "REMOVE", f.Bouquet, null, "Thu hoi hoa test");
        var startResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", startReq);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);

        var reportReq = new ReportIssueRequest("Cua bi ket khong the mo duoc");
        var reportResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/report-issue", reportReq);
        Assert.Equal(HttpStatusCode.OK, reportResp.StatusCode);
        var reportBody = await reportResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("NeedsInspection", reportBody!.Stage);
        Assert.Equal("Cua bi ket khong the mo duoc", reportBody.InspectionReason);
    }

    [Fact]
    public async Task ActualHappyRemoveCompletion()
    {
        var f = await SetupAsync();
        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var startReq = new StartSlotMaintenanceRequest(sessionId, "REMOVE", f.Bouquet, null, "Thu hoi hoa thanh cong");
        var startResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", startReq);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        var session = await startResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.NotNull(session!.CurrentCmdId);

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // Simulate legitimate device cycle: SENT -> ACK -> OPENED -> CLOSED
        await using (var doorCmd = data.CreateCommand("""
            SELECT flow.reset_clock();
            SELECT flow.device_event(@cmd, 'SENT');
            SELECT flow.device_event(@cmd, 'ACK');
            SELECT flow.device_event(@cmd, 'OPENED');
            SELECT flow.device_event(@cmd, 'CLOSED');

            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), (extract(epoch from clock_timestamp()) * 1000)::bigint + 10, ARRAY[0,-1,-1,-1], clock_timestamp())
            ON CONFLICT (kiosk_id) DO UPDATE SET states=excluded.states, observed_at=excluded.observed_at, received_at=excluded.received_at;
            """))
        {
            doorCmd.Parameters.AddWithValue("cmd", session.CurrentCmdId.Value);
            doorCmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            await doorCmd.ExecuteNonQueryAsync();
        }

        // Call check -> completes removal
        var checkResp = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.OK, checkResp.StatusCode);
        var completedSession = await checkResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("Completed", completedSession!.Stage);

        // Verify slot is RENTED_EMPTY and bouquet is RETURNED
        await using (var verifyCmd = data.CreateCommand("""
            SELECT s.status, b.status,
                   (SELECT count(*) FROM kiosk_ops.inventory_logs WHERE slot_id = @slot AND movement_type = 'RETURN_SELLER')
            FROM kiosk_ops.slots s CROSS JOIN kiosk_ops.bouquets b
            WHERE s.id = @slot AND b.id = @bouquet;
            """))
        {
            verifyCmd.Parameters.AddWithValue("slot", f.Slot);
            verifyCmd.Parameters.AddWithValue("bouquet", f.Bouquet);
            await using var reader = await verifyCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("RENTED_EMPTY", reader.GetString(0));
            Assert.Equal("RETURNED", reader.GetString(1));
            Assert.Equal(1L, reader.GetInt64(2));
        }

        // Duplicate check is idempotent
        var dupCheck = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.OK, dupCheck.StatusCode);
    }

    [Fact]
    public async Task ActualHappyTwoPhaseReplace()
    {
        var f = await SetupAsync();
        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var startReq = new StartSlotMaintenanceRequest(sessionId, "REPLACE", f.Bouquet, f.Product, "Doi hoa hai pha");
        var startResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", startReq);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        var session = await startResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        var removalCmdId = session!.CurrentCmdId!.Value;

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // Phase 1: Removal door cycle completes with empty observation
        await using (var p1Cmd = data.CreateCommand("""
            SELECT flow.reset_clock();
            SELECT flow.device_event(@cmd, 'SENT');
            SELECT flow.device_event(@cmd, 'ACK');
            SELECT flow.device_event(@cmd, 'OPENED');
            SELECT flow.device_event(@cmd, 'CLOSED');

            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), (extract(epoch from clock_timestamp()) * 1000)::bigint + 10, ARRAY[0,-1,-1,-1], clock_timestamp())
            ON CONFLICT (kiosk_id) DO UPDATE SET states=excluded.states, observed_at=excluded.observed_at, received_at=excluded.received_at;
            """))
        {
            p1Cmd.Parameters.AddWithValue("cmd", removalCmdId);
            p1Cmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            await p1Cmd.ExecuteNonQueryAsync();
        }

        var checkP1 = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.OK, checkP1.StatusCode);
        var p1Session = await checkP1.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("AwaitLoading", p1Session!.Stage);
        Assert.Equal("RemovalClosedEmptyConfirmed", p1Session.Substate);
        Assert.Null(p1Session.CurrentCmdId);

        // Phase 2: Call open-next to issue loading command
        var openNextResp = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/open-next", null);
        Assert.Equal(HttpStatusCode.OK, openNextResp.StatusCode);
        var p2Session = await openNextResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        var loadingCmdId = p2Session!.CurrentCmdId!.Value;
        Assert.NotEqual(removalCmdId, loadingCmdId);

        // Loading door cycle completes with flower present (states[0] = 1)
        await using (var p2Cmd = data.CreateCommand("""
            SELECT flow.reset_clock();
            SELECT flow.device_event(@cmd, 'SENT');
            SELECT flow.device_event(@cmd, 'ACK');
            SELECT flow.device_event(@cmd, 'OPENED');
            SELECT flow.device_event(@cmd, 'CLOSED');

            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), (extract(epoch from clock_timestamp()) * 1000)::bigint + 10, ARRAY[1,-1,-1,-1], clock_timestamp())
            ON CONFLICT (kiosk_id) DO UPDATE SET states=excluded.states, observed_at=excluded.observed_at, received_at=excluded.received_at;
            """))
        {
            p2Cmd.Parameters.AddWithValue("cmd", loadingCmdId);
            p2Cmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            await p2Cmd.ExecuteNonQueryAsync();
        }

        var checkP2 = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.OK, checkP2.StatusCode);
        var completed = await checkP2.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("Completed", completed!.Stage);
        Assert.NotNull(completed.NewBouquetId);

        // Verify slot is STOCKED with new bouquet
        await using (var verifyCmd = data.CreateCommand("""
            SELECT s.status, s.bouquet_id,
                   (SELECT count(*) FROM kiosk_ops.inventory_logs WHERE slot_id = @slot AND movement_type = 'STOCK_IN')
            FROM kiosk_ops.slots s WHERE s.id = @slot;
            """))
        {
            verifyCmd.Parameters.AddWithValue("slot", f.Slot);
            await using var reader = await verifyCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("STOCKED", reader.GetString(0));
            Assert.Equal(completed.NewBouquetId, reader.GetGuid(1));
            Assert.Equal(1L, reader.GetInt64(2));
        }
    }

    [Fact]
    public async Task InspectionRecoveryLoadingFlow()
    {
        var f = await SetupAsync(slotStatus: "RENTED_EMPTY");
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // Baseline empty observation
        await using (var baseCmd = data.CreateCommand("""
            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), (extract(epoch from public.app_now()) - 10) * 1000, ARRAY[0,-1,-1,-1], public.app_now() - interval '10 seconds')
            ON CONFLICT (kiosk_id) DO UPDATE SET states=excluded.states, observed_at=excluded.observed_at, received_at=excluded.received_at;
            """))
        {
            baseCmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            await baseCmd.ExecuteNonQueryAsync();
        }

        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var startReq = new StartSlotMaintenanceRequest(sessionId, "LOAD", null, f.Product, "Nap hoa inspection recovery");
        var startResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", startReq);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        var session = await startResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();

        // Door closes but sensor still shows 0 (empty, forgot to put flower)
        await using (var failDoor = data.CreateCommand("""
            SELECT flow.reset_clock();
            SELECT flow.device_event(@cmd, 'SENT');
            SELECT flow.device_event(@cmd, 'ACK');
            SELECT flow.device_event(@cmd, 'OPENED');
            SELECT flow.device_event(@cmd, 'CLOSED');

            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), (extract(epoch from clock_timestamp()) * 1000)::bigint + 10, ARRAY[0,-1,-1,-1], clock_timestamp())
            ON CONFLICT (kiosk_id) DO UPDATE SET states=excluded.states, observed_at=excluded.observed_at, received_at=excluded.received_at;
            """))
        {
            failDoor.Parameters.AddWithValue("cmd", session!.CurrentCmdId!.Value);
            failDoor.Parameters.AddWithValue("kiosk", f.Kiosk);
            await failDoor.ExecuteNonQueryAsync();
        }

        var check1 = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.OK, check1.StatusCode);
        var inspSession = await check1.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("NeedsInspection", inspSession!.Stage);
        Assert.Equal("SlotExpectedFlowerButEmpty", inspSession.InspectionReason);

        // Newer observation arrives with flower present (states[0] = 1)
        await using (var recoverObs = data.CreateCommand("""
            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), (extract(epoch from clock_timestamp()) * 1000)::bigint + 20, ARRAY[1,-1,-1,-1], public.app_now())
            ON CONFLICT (kiosk_id) DO UPDATE SET states=excluded.states, observed_at=excluded.observed_at, received_at=excluded.received_at;
            """))
        {
            recoverObs.Parameters.AddWithValue("kiosk", f.Kiosk);
            await recoverObs.ExecuteNonQueryAsync();
        }

        var checkRecover = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.OK, checkRecover.StatusCode);
        var recovered = await checkRecover.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("Completed", recovered!.Stage);
        Assert.NotNull(recovered.NewBouquetId);
    }

    [Fact]
    public async Task UnknownSensorReadingTransitionsToNeedsInspection()
    {
        var f = await SetupAsync();
        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var startReq = new StartSlotMaintenanceRequest(sessionId, "REMOVE", f.Bouquet, null, "Kiem tra sensor loi");
        var startResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", startReq);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        var session = await startResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // Door closes, but observation has states[0] = -1 (unsettled / open)
        await using (var doorCmd = data.CreateCommand("""
            SELECT flow.reset_clock();
            SELECT flow.device_event(@cmd, 'SENT');
            SELECT flow.device_event(@cmd, 'ACK');
            SELECT flow.device_event(@cmd, 'OPENED');
            SELECT flow.device_event(@cmd, 'CLOSED');

            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), (extract(epoch from clock_timestamp()) * 1000)::bigint + 10, ARRAY[-1,-1,-1,-1], clock_timestamp())
            ON CONFLICT (kiosk_id) DO UPDATE SET states=excluded.states, observed_at=excluded.observed_at, received_at=excluded.received_at;
            """))
        {
            doorCmd.Parameters.AddWithValue("cmd", session!.CurrentCmdId!.Value);
            doorCmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            await doorCmd.ExecuteNonQueryAsync();
        }

        var checkResp = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.OK, checkResp.StatusCode);
        var inspSession = await checkResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("NeedsInspection", inspSession!.Stage);
        Assert.Equal("SensorReadingUnsettledOrDoorOpen", inspSession.InspectionReason);
    }

    [Fact]
    public async Task OfflineKioskTransitionsToNeedsInspection()
    {
        var f = await SetupAsync();
        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var startReq = new StartSlotMaintenanceRequest(sessionId, "REMOVE", f.Bouquet, null, "Kiem tra kiosk offline");
        var startResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", startReq);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // Set kiosk offline
        await using (var offCmd = data.CreateCommand("UPDATE kiosk_ops.kiosks SET status = 'OFFLINE' WHERE id = @kiosk"))
        {
            offCmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            await offCmd.ExecuteNonQueryAsync();
        }

        var checkResp = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.OK, checkResp.StatusCode);
        var inspSession = await checkResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("NeedsInspection", inspSession!.Stage);
        Assert.Equal("KioskOffline", inspSession.InspectionReason);
    }

    [Fact]
    public async Task StaleHeartbeatTransitionsToNeedsInspection()
    {
        var f = await SetupAsync();
        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var startReq = new StartSlotMaintenanceRequest(sessionId, "REMOVE", f.Bouquet, null, "Kiem tra heartbeat cu");
        var startResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", startReq);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // Set kiosk heartbeat stale (> 45s)
        await using (var staleCmd = data.CreateCommand("UPDATE kiosk_ops.kiosks SET last_heartbeat_at = public.app_now() - interval '60 seconds' WHERE id = @kiosk"))
        {
            staleCmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            await staleCmd.ExecuteNonQueryAsync();
        }

        var checkResp = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.OK, checkResp.StatusCode);
        var inspSession = await checkResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("NeedsInspection", inspSession!.Stage);
        Assert.Equal("KioskOffline", inspSession.InspectionReason);
    }

    [Fact]
    public async Task OpenForCheckRecoveryMismatchToCompletion()
    {
        var f = await SetupAsync();
        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var startReq = new StartSlotMaintenanceRequest(sessionId, "REMOVE", f.Bouquet, null, "Thu hoi hoa co obstruction");
        var startResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions", startReq);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        var session = await startResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        var firstCmdId = session!.CurrentCmdId!.Value;

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // 1. Initial door cycle closed, but sensor mismatch (states[0] = 1, still detected flower)
        await using (var doorCmd = data.CreateCommand("""
            SELECT flow.reset_clock();
            SELECT flow.device_event(@cmd, 'SENT');
            SELECT flow.device_event(@cmd, 'ACK');
            SELECT flow.device_event(@cmd, 'OPENED');
            SELECT flow.device_event(@cmd, 'CLOSED');

            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), (extract(epoch from clock_timestamp()) * 1000)::bigint + 10, ARRAY[1,-1,-1,-1], clock_timestamp())
            ON CONFLICT (kiosk_id) DO UPDATE SET states=excluded.states, observed_at=excluded.observed_at, received_at=excluded.received_at;
            """))
        {
            doorCmd.Parameters.AddWithValue("cmd", firstCmdId);
            doorCmd.Parameters.AddWithValue("kiosk", f.Kiosk);
            await doorCmd.ExecuteNonQueryAsync();
        }

        var check1 = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.OK, check1.StatusCode);
        var inspSession = await check1.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("NeedsInspection", inspSession!.Stage);
        Assert.Equal("SlotExpectedEmptyButDetected", inspSession.InspectionReason);
        Assert.Contains("open-for-check", inspSession.AvailableActions);

        // 2. Call open-for-check to reopen and clear obstruction
        var openCheckReq = new OpenForCheckRequest("Mo cua lai de lay bo hoa bi ket");
        var openCheckResp = await client.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/open-for-check", openCheckReq);
        Assert.Equal(HttpStatusCode.OK, openCheckResp.StatusCode);
        var openCheckBody = await openCheckResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("NeedsInspection", openCheckBody!.Stage);
        var newCmdId = openCheckBody.CurrentCmdId!.Value;
        Assert.NotEqual(firstCmdId, newCmdId);

        var hardware = f.Kiosk.ToString();
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var keys = new DeviceKeyRing(new Dictionary<string, byte[]> { [hardware] = key });
        var dispatcher = new DeviceCommandDispatcher(data, scope.ServiceProvider.GetRequiredService<DotNetCore.CAP.ICapPublisher>(), keys);
        var prepared = Assert.IsType<PreparedUnlock>(await dispatcher.PrepareAsync(newCmdId, CancellationToken.None));
        Assert.True(await dispatcher.DeliverAsync(prepared, (command, _) =>
        {
            Assert.Equal(newCmdId, command.CommandId);
            Assert.True(DeviceProtocol.Verify(command, hardware, key, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }, CancellationToken.None));

        // 3. Simulate a new closed door cycle followed by a fresh empty observation.
        await using (var doorCmd2 = data.CreateCommand("""
            SELECT flow.reset_clock();
            SELECT flow.device_event(@cmd, 'ACK');
            SELECT flow.device_event(@cmd, 'OPENED');
            SELECT flow.device_event(@cmd, 'CLOSED');

            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), (extract(epoch from clock_timestamp()) * 1000)::bigint + 20, ARRAY[0,-1,-1,-1], clock_timestamp())
            ON CONFLICT (kiosk_id) DO UPDATE SET states=excluded.states, observed_at=excluded.observed_at, received_at=excluded.received_at;
            """))
        {
            doorCmd2.Parameters.AddWithValue("cmd", newCmdId);
            doorCmd2.Parameters.AddWithValue("kiosk", f.Kiosk);
            await doorCmd2.ExecuteNonQueryAsync();
        }

        // 4. Check -> completes removal!
        var check2 = await client.PostAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{sessionId}/check", null);
        Assert.Equal(HttpStatusCode.OK, check2.StatusCode);
        var completed = await check2.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal("Completed", completed!.Stage);
    }

    [Fact]
    public async Task OpenForCheckGuardsAndLimits()
    {
        var fA = await SetupAsync();
        var fB = await SetupAsync();
        using var clientA = Client(fA);
        using var clientB = Client(fB);

        var sessionId = Guid.NewGuid();
        var startReq = new StartSlotMaintenanceRequest(sessionId, "REMOVE", fA.Bouquet, null, "Thu hoi hoa guards");
        var startResp = await clientA.PostAsJsonAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions", startReq);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        var session = await startResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        var cmdId = session!.CurrentCmdId!.Value;

        // Guard 1: Not in NeedsInspection yet -> 409
        var openCheckReq = new OpenForCheckRequest("Mo kiem tra khi chua inspect");
        var premature = await clientA.PostAsJsonAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionId}/open-for-check", openCheckReq);
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);

        // Put into NeedsInspection via report-issue, but door is still ISSUED (not closed) -> 409
        await clientA.PostAsJsonAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionId}/report-issue", new ReportIssueRequest("Bao loi cua"));
        var notClosedResp = await clientA.PostAsJsonAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionId}/open-for-check", openCheckReq);
        Assert.Equal(HttpStatusCode.Conflict, notClosedResp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // Advance door to CLOSED with valid 0/1 snapshot
        await using (var doorCmd = data.CreateCommand("""
            SELECT flow.reset_clock();
            SELECT flow.device_event(@cmd, 'SENT');
            SELECT flow.device_event(@cmd, 'ACK');
            SELECT flow.device_event(@cmd, 'OPENED');
            SELECT flow.device_event(@cmd, 'CLOSED');

            INSERT INTO kiosk_ops.flower_presence(kiosk_id, event_id, observed_at, states, received_at)
            VALUES(@kiosk, gen_random_uuid(), (extract(epoch from clock_timestamp()) * 1000)::bigint + 10, ARRAY[1,-1,-1,-1], clock_timestamp())
            ON CONFLICT (kiosk_id) DO UPDATE SET states=excluded.states, observed_at=excluded.observed_at, received_at=excluded.received_at;
            """))
        {
            doorCmd.Parameters.AddWithValue("cmd", cmdId);
            doorCmd.Parameters.AddWithValue("kiosk", fA.Kiosk);
            await doorCmd.ExecuteNonQueryAsync();
        }

        // Guard 2: Cross-shop caller B -> 404
        var crossShopResp = await clientB.PostAsJsonAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionId}/open-for-check", openCheckReq);
        Assert.Equal(HttpStatusCode.NotFound, crossShopResp.StatusCode);

        // Guard 3: Capability revoked -> 409
        await using (var capCmd = data.CreateCommand("UPDATE kiosk_ops.slot_sensor_capabilities SET enabled = false WHERE slot_id = @slot"))
        {
            capCmd.Parameters.AddWithValue("slot", fA.Slot);
            await capCmd.ExecuteNonQueryAsync();
        }
        var capRevokedResp = await clientA.PostAsJsonAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionId}/open-for-check", openCheckReq);
        Assert.Equal(HttpStatusCode.Conflict, capRevokedResp.StatusCode);

        // Restore capability
        await using (var capCmd = data.CreateCommand("UPDATE kiosk_ops.slot_sensor_capabilities SET enabled = true WHERE slot_id = @slot"))
        {
            capCmd.Parameters.AddWithValue("slot", fA.Slot);
            await capCmd.ExecuteNonQueryAsync();
        }

        // Call 1 succeeds
        var open1 = await clientA.PostAsJsonAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionId}/open-for-check", openCheckReq);
        Assert.Equal(HttpStatusCode.OK, open1.StatusCode);

        // Guard 4: Immediate second open (< 30s) -> 409
        var rapidOpen = await clientA.PostAsJsonAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionId}/open-for-check", openCheckReq);
        Assert.Equal(HttpStatusCode.Conflict, rapidOpen.StatusCode);

        // Guard 5: Max 3 limit exceeded -> 409
        await using (var limitCmd = data.CreateCommand("UPDATE kiosk_ops.slot_maintenance_sessions SET inspection_open_count = 3, last_inspection_open_at = public.app_now() - interval '60 seconds' WHERE id = @id"))
        {
            limitCmd.Parameters.AddWithValue("id", sessionId);
            await limitCmd.ExecuteNonQueryAsync();
        }
        var limitExceeded = await clientA.PostAsJsonAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionId}/open-for-check", openCheckReq);
        Assert.Equal(HttpStatusCode.Conflict, limitExceeded.StatusCode);
    }
}
