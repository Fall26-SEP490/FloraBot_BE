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
        var start = await PostMaintenanceAsync(client, f.Kiosk, f.Slot,
            new StartSlotMaintenanceRequest(session, action, action == "REPLACE" ? f.Bouquet : null,
                f.Product, "Load with a concurrent stock adjustment"));
        start.EnsureSuccessStatusCode();
        var active = (await start.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>())!;
        if (action == "REPLACE")
        {
            await DoorCycle(active.CurrentCmdId!.Value, 0, 0);
            (await client.PostAsync($"{root}/{session}/check", null)).EnsureSuccessStatusCode();
            var open = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, session, "OPEN_NEXT");
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
        var otherStart = await PostMaintenanceAsync(
            client, f.Kiosk, otherSlot,
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
    public async Task MaintenanceOtpCanOnlyOpenFromBoundKioskAndIsConsumedOnce()
    {
        var fixture = await SetupAsync();
        var other = await SetupAsync();
        using var seller = Client(fixture);
        using var device = await AuthenticateKioskAsync(fixture);
        using var foreignDevice = await AuthenticateKioskAsync(other);
        var sessionId = Guid.NewGuid();
        var input = new
        {
            kioskId = fixture.Kiosk,
            slotId = fixture.Slot,
            sessionId,
            operation = "START",
            action = "REMOVE",
            expectedBouquetId = fixture.Bouquet,
            reason = "Lấy hoa kiểm thử OTP",
        };
        var legacyUrl = $"/api/me/kiosks/{fixture.Kiosk}/slots/{fixture.Slot}/maintenance-sessions";
        using var bypass = await seller.PostAsJsonAsync(legacyUrl, new StartSlotMaintenanceRequest(
            sessionId, "REMOVE", fixture.Bouquet, null, input.reason));
        Assert.Equal(HttpStatusCode.Conflict, bypass.StatusCode);

        using var issue = await seller.PostAsJsonAsync("/api/me/maintenance-access", input);
        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        Assert.True(issue.Headers.CacheControl?.NoStore);
        var issued = await issue.Content.ReadFromJsonAsync<JsonElement>();
        var code = issued.GetProperty("code").GetString()!;
        var id = issued.GetProperty("id").GetGuid();
        Assert.Matches("^[0-9]{6}$", code);

        var pendingUrl = $"/api/kiosks/{fixture.Kiosk}/maintenance-access/pending";
        using var otherRead = await foreignDevice.GetAsync(pendingUrl);
        Assert.Equal(HttpStatusCode.Forbidden, otherRead.StatusCode);
        using var ownRead = await device.GetAsync(pendingUrl);
        Assert.Equal(HttpStatusCode.OK, ownRead.StatusCode);
        var pending = Assert.Single((await ownRead.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(id, pending.GetProperty("id").GetGuid());
        Assert.False(pending.TryGetProperty("code", out _));

        var confirmUrl = $"/api/kiosks/{fixture.Kiosk}/maintenance-access/{id}/confirm";
        using var wrongKiosk = await foreignDevice.PostAsJsonAsync(confirmUrl, new { code });
        Assert.Equal(HttpStatusCode.Forbidden, wrongKiosk.StatusCode);
        using var wrongCode = await device.PostAsJsonAsync(confirmUrl, new { code = code == "000000" ? "000001" : "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCode.StatusCode);
        using var before = await seller.GetAsync($"{legacyUrl}/{sessionId}");
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);

        var confirmations = await Task.WhenAll(
            device.PostAsJsonAsync(confirmUrl, new { code }),
            device.PostAsJsonAsync(confirmUrl, new { code }));
        try
        {
            Assert.Single(confirmations, result => result.StatusCode == HttpStatusCode.OK);
            Assert.Single(confirmations, result => result.StatusCode == HttpStatusCode.NotFound);
        }
        finally
        {
            foreach (var response in confirmations) response.Dispose();
        }
        var session = await seller.GetFromJsonAsync<SlotMaintenanceSessionResponse>($"{legacyUrl}/{sessionId}");
        Assert.NotNull(session?.CurrentCmdId);
        Assert.Equal("AwaitRemoval", session.Stage);
    }

    [Fact]
    public async Task InventoryTaskOtpFollowsAssigneeAndCompletesWithSensorEvidence()
    {
        var f = await SetupAsync();
        var staffId = Guid.NewGuid();
        var anotherId = Guid.NewGuid();
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.users(id,email,full_name,password_hash,role,seller_id,status)
            VALUES(@staff,@staff::text||'@example.invalid','Inventory staff','!test','SELLER_STAFF',@seller,'ACTIVE'),
              (@other,@other::text||'@example.invalid','Other staff','!test','SELLER_STAFF',@seller,'ACTIVE')
            """);
        setup.Parameters.AddWithValue("staff", staffId);
        setup.Parameters.AddWithValue("other", anotherId);
        setup.Parameters.AddWithValue("seller", f.Seller);
        await setup.ExecuteNonQueryAsync();

        using var seller = Client(f);
        using var staff = Client(f with { Actor = staffId }, "SELLER_STAFF");
        using var other = Client(f with { Actor = anotherId }, "SELLER_STAFF");
        using var kiosk = await AuthenticateKioskAsync(f);
        var taskId = Guid.NewGuid();
        using var created = await seller.PostAsJsonAsync("/api/me/inventory-tasks", new
        {
            id = taskId,
            assigneeId = staffId,
            kioskId = f.Kiosk,
            slotId = f.Slot,
            action = "REMOVE",
            expectedBouquetId = f.Bouquet,
            instructions = "Thu hồi bó hoa được giao",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var taskPath = $"/api/staff/tasks/{taskId}";
        var accessPath = $"{taskPath}/inventory-maintenance/access";
        using var preorderRead = await staff.GetAsync($"{taskPath}/preorders");
        Assert.Equal(HttpStatusCode.NotFound, preorderRead.StatusCode);
        using var preorderWrite = await staff.PostAsJsonAsync($"{taskPath}/preorders/{Guid.NewGuid()}/fulfill",
            new { slotId = f.Slot, qrCode = "inventory-task-cannot-fulfill-preorder" });
        Assert.Equal(HttpStatusCode.NotFound, preorderWrite.StatusCode);
        using var early = await staff.PostAsJsonAsync(accessPath, new { operation = "START" });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        using var start = await staff.PostAsJsonAsync($"{taskPath}/transition",
            new TransitionStaffTask(1, "IN_PROGRESS", "Đã bắt đầu ca thu hồi"));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var denied = await other.PostAsJsonAsync(accessPath, new { operation = "START" });
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        using var issued = await staff.PostAsJsonAsync(accessPath, new { operation = "START" });
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var credential = await issued.Content.ReadFromJsonAsync<JsonElement>();
        using var confirmed = await ConfirmIssuedAsync(kiosk, f.Kiosk, credential);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var sessionPath = $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{taskId}";
        var session = await seller.GetFromJsonAsync<SlotMaintenanceSessionResponse>(sessionPath);
        Assert.NotNull(session?.CurrentCmdId);
        await SimulateClosedDoorAsync(f.Kiosk, session.CurrentCmdId.Value, 0);

        using var check = await seller.PostAsync($"{sessionPath}/check", null);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal("Completed", (await check.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>())?.Stage);
        using var submit = await staff.PostAsJsonAsync($"{taskPath}/transition",
            new TransitionStaffTask(2, "SUBMITTED", "Đã lấy hoa và đóng cửa"));
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
    }

    [Fact]
    public async Task TechnicianNeedsSellerApprovalAndManagerRestoresOnlyAfterEvidence()
    {
        var f = await SetupAsync();
        var technicianId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.users(id,email,full_name,password_hash,role,status)
            VALUES(@tech,@tech::text||'@example.invalid','Repair technician','!test','TECHNICIAN','ACTIVE'),
              (@manager,@manager::text||'@example.invalid','Repair manager','!test','OPERATIONS_MANAGER','ACTIVE');
            INSERT INTO kiosk_ops.manager_kiosks(manager_id,kiosk_id) VALUES(@manager,@kiosk);
            """);
        setup.Parameters.AddWithValue("tech", technicianId);
        setup.Parameters.AddWithValue("manager", managerId);
        setup.Parameters.AddWithValue("kiosk", f.Kiosk);
        await setup.ExecuteNonQueryAsync();

        using var seller = Client(f);
        using var technician = factory.CreateClient();
        technician.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("TECHNICIAN", userId: technicianId));
        using var manager = factory.CreateClient();
        manager.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));
        using var device = await AuthenticateKioskAsync(f);

        await using var report = data.CreateCommand("""
            SELECT flow.report_device_fault(@kiosk,@slot,@seller,'Lock needs repair','https://example.invalid/fault.png')
            """);
        report.Parameters.AddWithValue("kiosk", f.Kiosk);
        report.Parameters.AddWithValue("slot", f.Slot);
        report.Parameters.AddWithValue("seller", f.Actor);
        var incident = (Guid)(await report.ExecuteScalarAsync())!;

        var taskId = Guid.NewGuid();
        using var assigned = await manager.PostAsJsonAsync("/api/operations/staff-tasks",
            new AssignStaffTask(taskId, technicianId, "INCIDENT", f.Kiosk, null, incident, "Kiểm tra và thử đóng khóa"));
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var taskPath = $"/api/staff/tasks/{taskId}";
        using var start = await technician.PostAsJsonAsync($"{taskPath}/transition",
            new TransitionStaffTask(1, "IN_PROGRESS", "Bắt đầu ca sửa đã giao"));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var before = await technician.PostAsJsonAsync($"{taskPath}/repair-access", new { });
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);

        var approvePath = $"/api/me/technical-repairs/{taskId}/approve";
        await using var capability = data.CreateCommand(
            "UPDATE kiosk_ops.slot_sensor_capabilities SET enabled=@enabled WHERE slot_id=@slot");
        capability.Parameters.AddWithValue("enabled", false);
        capability.Parameters.AddWithValue("slot", f.Slot);
        await capability.ExecuteNonQueryAsync();
        using var unverified = await seller.PostAsJsonAsync(approvePath, new { });
        Assert.False(unverified.IsSuccessStatusCode);

        capability.Parameters["enabled"].Value = true;
        await capability.ExecuteNonQueryAsync();
        using var approved = await seller.PostAsJsonAsync(approvePath, new { });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        await using var expire = data.CreateCommand(
            "UPDATE kiosk_ops.technical_repair_access SET expires_at=clock_timestamp()-interval '1 second' WHERE task_id=@task");
        expire.Parameters.AddWithValue("task", taskId);
        await expire.ExecuteNonQueryAsync();
        using var expired = await technician.PostAsJsonAsync($"{taskPath}/repair-access", new { });
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        var repairs = await seller.GetFromJsonAsync<JsonElement>("/api/me/technical-repairs");
        var expiredRepair = Assert.Single(repairs.EnumerateArray(), row => row.GetProperty("id").GetGuid() == taskId);
        Assert.Equal(JsonValueKind.Null, expiredRepair.GetProperty("expiresAt").ValueKind);
        using var renewed = await seller.PostAsJsonAsync(approvePath, new { });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        using var issued = await technician.PostAsJsonAsync($"{taskPath}/repair-access", new { });
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        using var opened = await ConfirmIssuedAsync(device, f.Kiosk, await issued.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

        var input = new StaffIncidentResolutionInput(Guid.NewGuid(), 2, "Đã kiểm tra khóa và cảm biến");
        using var premature = await technician.PostAsJsonAsync($"{taskPath}/resolve-incident", input);
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
        await using var command = data.CreateCommand("SELECT latest_cmd_id FROM kiosk_ops.technical_repair_access WHERE task_id=@task");
        command.Parameters.AddWithValue("task", taskId);
        var commandId = Assert.IsType<Guid>(await command.ExecuteScalarAsync());
        await SimulateClosedDoorAsync(f.Kiosk, commandId, 1);
        using var submitted = await technician.PostAsJsonAsync($"{taskPath}/resolve-incident", input);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var revoked = await technician.PostAsJsonAsync($"{taskPath}/repair-access", new { });
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        await using var slot = data.CreateCommand("SELECT status FROM kiosk_ops.slots WHERE id=@slot");
        slot.Parameters.AddWithValue("slot", f.Slot);
        Assert.Equal("FAULT", await slot.ExecuteScalarAsync());
        using var replay = await technician.PostAsJsonAsync($"{taskPath}/resolve-incident", input);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(3, (await replay.Content.ReadFromJsonAsync<StaffIncidentResolutionResult>())?.Version);
        using var reviewed = await manager.PostAsJsonAsync($"/api/operations/staff-tasks/{taskId}/transition",
            new TransitionStaffTask(3, "COMPLETED", "Đã xác nhận kết quả kiểm tra"));
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        Assert.Equal("STOCKED", await slot.ExecuteScalarAsync());
    }

    private static Task<HttpResponseMessage> ConfirmIssuedAsync(HttpClient device, Guid kiosk, JsonElement issued) =>
        device.PostAsJsonAsync($"/api/kiosks/{kiosk}/maintenance-access/{issued.GetProperty("id").GetGuid()}/confirm",
            new { code = issued.GetProperty("code").GetString() });

    private async Task SimulateClosedDoorAsync(Guid kioskId, Guid commandId, int presence)
    {
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var command = data.CreateCommand("""
            SELECT flow.reset_clock();
            SELECT flow.device_event(@cmd, 'SENT');
            SELECT flow.device_event(@cmd, 'ACK');
            SELECT flow.device_event(@cmd, 'OPENED');
            SELECT flow.device_event(@cmd, 'CLOSED');
            INSERT INTO kiosk_ops.mqtt_inbox(event_id,kiosk_id,cmd_id,event,payload_hash,occurred_at,processed_at,disposition)
            VALUES(gen_random_uuid(),@kiosk,@cmd,'CLOSED','simulated-integration-test',clock_timestamp(),clock_timestamp(),'APPLIED');
            INSERT INTO kiosk_ops.flower_presence(kiosk_id,event_id,observed_at,states,received_at)
            VALUES(@kiosk,gen_random_uuid(),(extract(epoch FROM clock_timestamp())*1000)::bigint+10,ARRAY[@presence,-1,-1,-1],clock_timestamp())
            ON CONFLICT(kiosk_id) DO UPDATE SET states=excluded.states,observed_at=excluded.observed_at,received_at=excluded.received_at;
            """);
        command.Parameters.AddWithValue("cmd", commandId);
        command.Parameters.AddWithValue("kiosk", kioskId);
        command.Parameters.AddWithValue("presence", presence);
        await command.ExecuteNonQueryAsync();
    }

    private Task<HttpResponseMessage> PostMaintenanceAsync(HttpClient seller, Guid kioskId, Guid slotId,
        StartSlotMaintenanceRequest input) =>
        PostMaintenanceAsync(seller, kioskId, slotId, input.Id, "START", input.Action,
            input.ExpectedBouquetId, input.ProductId, input.Reason);

    private async Task<HttpResponseMessage> PostMaintenanceAsync(HttpClient seller, Guid kioskId, Guid slotId,
        Guid sessionId, string operation, string? action = null, Guid? expectedBouquetId = null,
        Guid? productId = null, string? reason = null)
    {
        var issued = await seller.PostAsJsonAsync("/api/me/maintenance-access", new
        {
            kioskId,
            slotId,
            sessionId,
            operation,
            action,
            expectedBouquetId,
            productId,
            reason,
        });
        if (!issued.IsSuccessStatusCode) return issued;
        using (issued)
        using (var kiosk = await AuthenticateKioskAsync(kioskId))
        {
            var code = await issued.Content.ReadFromJsonAsync<JsonElement>();
            var confirmation = await ConfirmIssuedAsync(kiosk, kioskId, code);
            if (!confirmation.IsSuccessStatusCode) return confirmation;
            confirmation.Dispose();
            return await seller.GetAsync($"/api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions/{sessionId}");
        }
    }

    private Task<HttpClient> AuthenticateKioskAsync(Fixture fixture) => AuthenticateKioskAsync(fixture.Kiosk);

    private async Task<HttpClient> AuthenticateKioskAsync(Guid kioskId)
    {
        var key = $"test-only-maintenance-{kioskId:N}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var command = data.CreateCommand("UPDATE kiosk_ops.kiosks SET api_key_hash=@hash WHERE id=@id");
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("id", kioskId);
        await command.ExecuteNonQueryAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Kiosk-Key", key);
        return client;
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
        var loadResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, loadReq);
        Assert.Equal(HttpStatusCode.Forbidden, loadResp.StatusCode);

        // Cannot REPLACE (403 Forbidden for PAST_DUE)
        var replaceReq = new StartSlotMaintenanceRequest(Guid.NewGuid(), "REPLACE", f.Bouquet, f.Product, "Past due replace attempt");
        var replaceResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, replaceReq);
        Assert.Equal(HttpStatusCode.Forbidden, replaceResp.StatusCode);

        // Can start REMOVE
        var sessionId = Guid.NewGuid();
        var removeReq = new StartSlotMaintenanceRequest(sessionId, "REMOVE", f.Bouquet, null, "Past due remove bouquet");
        var removeResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, removeReq);
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
        var closedResp = await PostMaintenanceAsync(closedClient, closedFixture.Kiosk, closedFixture.Slot, closedRemoveReq);
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
        var anonResp = await PostMaintenanceAsync(anon, f.Kiosk, f.Slot, request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // Mismatched token (SELLER in DB but token claims CUSTOMER) -> 401
        using var mismatchedClient = Client(f, "CUSTOMER");
        var mismatchedResp = await PostMaintenanceAsync(mismatchedClient, f.Kiosk, f.Slot, request);
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
        var custResp = await PostMaintenanceAsync(legitCustomerClient, f.Kiosk, f.Slot, request);
        Assert.Equal(HttpStatusCode.Forbidden, custResp.StatusCode);

        // Foreign kiosk -> 404
        var foreignKiosk = Guid.NewGuid();
        var foreignResp = await PostMaintenanceAsync(client, foreignKiosk, f.Slot, request);
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
        var startResp = await PostMaintenanceAsync(clientA, fA.Kiosk, fA.Slot, startReq);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);

        // Seller B cannot GET Seller A's session -> 404
        var getByB = await clientB.GetAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionAId}");
        Assert.Equal(HttpStatusCode.NotFound, getByB.StatusCode);

        // Seller B cannot Check Seller A's session -> 404
        var checkByB = await clientB.PostAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionAId}/check", null);
        Assert.Equal(HttpStatusCode.NotFound, checkByB.StatusCode);

        // Seller B cannot OpenNext Seller A's session -> 404
        var openNextByB = await PostMaintenanceAsync(clientB, fA.Kiosk, fA.Slot, sessionAId, "OPEN_NEXT");
        Assert.Equal(HttpStatusCode.NotFound, openNextByB.StatusCode);

        // Seller B cannot ReportIssue on Seller A's session -> 404
        var reportByB = await clientB.PostAsJsonAsync(
            $"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionAId}/report-issue",
            new ReportIssueRequest("Loi gia mao"));
        Assert.Equal(HttpStatusCode.NotFound, reportByB.StatusCode);

        // Seller B cannot reuse Seller A's session ID on B's kiosk without authorization -> 404
        var spoofStart = new StartSlotMaintenanceRequest(sessionAId, "REMOVE", fB.Bouquet, null, "Spoof session id");
        var spoofResp = await PostMaintenanceAsync(clientB, fB.Kiosk, fB.Slot, spoofStart);
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

        var resp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, request);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task IdempotentBeginReturnsSameSessionOrConflictOnChangedPayload()
    {
        var f = await SetupAsync();
        using var client = Client(f);
        var sessionId = Guid.NewGuid();
        var request = new StartSlotMaintenanceRequest(sessionId, "REMOVE", f.Bouquet, null, "Thu hoi hoa test");

        var resp1 = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, request);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.True(resp1.Headers.CacheControl?.NoStore);
        var body1 = await resp1.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal(sessionId, body1!.Id);
        Assert.Equal("AwaitRemoval", body1.Stage);

        // Repeat identical -> 200 OK
        var resp2 = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, request);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        Assert.Equal(sessionId, body2!.Id);

        // Changed payload with same ID -> 409 Conflict
        var changedRequest = request with { Reason = "Ly do khac hoan toan" };
        var resp3 = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, changedRequest);
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
        var loadResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, loadReq);
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
        var respNoPresence = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, request);
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

        var respNotEmpty = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, request);
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

        var respSuccess = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, request);
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
        var loadResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, loadReq);
        Assert.Equal(HttpStatusCode.Conflict, loadResp.StatusCode);

        // Replace blocked
        var repReq = new StartSlotMaintenanceRequest(Guid.NewGuid(), "REPLACE", f.Bouquet, f.Product, "Doi hoa het han");
        var repResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, repReq);
        Assert.Equal(HttpStatusCode.Conflict, repResp.StatusCode);

        // Remove allowed
        var remReq = new StartSlotMaintenanceRequest(Guid.NewGuid(), "REMOVE", f.Bouquet, null, "Thu hoi hoa het han");
        var remResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, remReq);
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
        var startResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, startReq);
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
        var startResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, startReq);
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
        var startResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, startReq);
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
        var openNextResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, sessionId, "OPEN_NEXT");
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
        var startResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, startReq);
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
        var startResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, startReq);
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
        var startResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, startReq);
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
        var startResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, startReq);
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
        var startResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, startReq);
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
        var openCheckResp = await PostMaintenanceAsync(client, f.Kiosk, f.Slot, sessionId, "OPEN_FOR_CHECK", reason: openCheckReq.Reason);
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
        var startResp = await PostMaintenanceAsync(clientA, fA.Kiosk, fA.Slot, startReq);
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        var session = await startResp.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>();
        var cmdId = session!.CurrentCmdId!.Value;

        // Guard 1: Not in NeedsInspection yet -> 409
        var openCheckReq = new OpenForCheckRequest("Mo kiem tra khi chua inspect");
        var premature = await PostMaintenanceAsync(clientA, fA.Kiosk, fA.Slot, sessionId, "OPEN_FOR_CHECK", reason: openCheckReq.Reason);
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);

        // Put into NeedsInspection via report-issue, but door is still ISSUED (not closed) -> 409
        await clientA.PostAsJsonAsync($"/api/me/kiosks/{fA.Kiosk}/slots/{fA.Slot}/maintenance-sessions/{sessionId}/report-issue", new ReportIssueRequest("Bao loi cua"));
        var notClosedResp = await PostMaintenanceAsync(clientA, fA.Kiosk, fA.Slot, sessionId, "OPEN_FOR_CHECK", reason: openCheckReq.Reason);
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
        var crossShopResp = await PostMaintenanceAsync(clientB, fA.Kiosk, fA.Slot, sessionId, "OPEN_FOR_CHECK", reason: openCheckReq.Reason);
        Assert.Equal(HttpStatusCode.NotFound, crossShopResp.StatusCode);

        // Guard 3: Capability revoked -> 409
        await using (var capCmd = data.CreateCommand("UPDATE kiosk_ops.slot_sensor_capabilities SET enabled = false WHERE slot_id = @slot"))
        {
            capCmd.Parameters.AddWithValue("slot", fA.Slot);
            await capCmd.ExecuteNonQueryAsync();
        }
        var capRevokedResp = await PostMaintenanceAsync(clientA, fA.Kiosk, fA.Slot, sessionId, "OPEN_FOR_CHECK", reason: openCheckReq.Reason);
        Assert.Equal(HttpStatusCode.Conflict, capRevokedResp.StatusCode);

        // Restore capability
        await using (var capCmd = data.CreateCommand("UPDATE kiosk_ops.slot_sensor_capabilities SET enabled = true WHERE slot_id = @slot"))
        {
            capCmd.Parameters.AddWithValue("slot", fA.Slot);
            await capCmd.ExecuteNonQueryAsync();
        }

        // Call 1 succeeds
        var open1 = await PostMaintenanceAsync(clientA, fA.Kiosk, fA.Slot, sessionId, "OPEN_FOR_CHECK", reason: openCheckReq.Reason);
        Assert.Equal(HttpStatusCode.OK, open1.StatusCode);

        // Guard 4: Immediate second open (< 30s) -> 409
        var rapidOpen = await PostMaintenanceAsync(clientA, fA.Kiosk, fA.Slot, sessionId, "OPEN_FOR_CHECK", reason: openCheckReq.Reason);
        Assert.Equal(HttpStatusCode.Conflict, rapidOpen.StatusCode);

        // Guard 5: Max 3 limit exceeded -> 409
        await using (var limitCmd = data.CreateCommand("UPDATE kiosk_ops.slot_maintenance_sessions SET inspection_open_count = 3, last_inspection_open_at = public.app_now() - interval '60 seconds' WHERE id = @id"))
        {
            limitCmd.Parameters.AddWithValue("id", sessionId);
            await limitCmd.ExecuteNonQueryAsync();
        }
        var limitExceeded = await PostMaintenanceAsync(clientA, fA.Kiosk, fA.Slot, sessionId, "OPEN_FOR_CHECK", reason: openCheckReq.Reason);
        Assert.Equal(HttpStatusCode.Conflict, limitExceeded.StatusCode);
    }
}
