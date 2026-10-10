using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class SplitRoleTaskLifecycleTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly byte[] TestPng =
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1f, 0x15, 0xc4,
        0x89, 0x00, 0x00, 0x00, 0x0a, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9c, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0d, 0x0a, 0x2d, 0xb4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44, 0xae,
        0x42, 0x60, 0x82
    ];

    [Fact]
    public async Task TechnicalIncidentLifecycleAndScopeEnforcement()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var managerId = Guid.NewGuid();
        var techId = Guid.NewGuid();
        var tech2Id = Guid.NewGuid();
        var unassignedManagerId = Guid.NewGuid();
        var kiosk = Guid.NewGuid();
        var slot = Guid.NewGuid();
        var incident = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            INSERT INTO identity.users(id,email,full_name,password_hash,role) VALUES
              (@mgr,@mgr::text||'@example.invalid','Ops Manager','!unprovisioned','OPERATIONS_MANAGER'),
              (@unmgr,@unmgr::text||'@example.invalid','Unassigned Ops Manager','!unprovisioned','OPERATIONS_MANAGER'),
              (@tech,@tech::text||'@example.invalid','Field Tech','!unprovisioned','TECHNICIAN'),
              (@tech2,@tech2::text||'@example.invalid','Field Tech 2','!unprovisioned','TECHNICIAN');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Tech Kiosk','Tech Address','HCM',@kiosk::text,@kiosk::text,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,status)
            VALUES(@slot,@kiosk,'T01',0,'FAULT');
            INSERT INTO ordering.disputes(id,kind,kiosk_id,slot_id,reason)
            VALUES(@incident,'DEVICE_FAULT',@kiosk,@slot,'Door sensor jammed');
            INSERT INTO kiosk_ops.manager_kiosks(manager_id,kiosk_id)
            VALUES(@mgr,@kiosk);
            """);
        setup.Parameters.AddWithValue("mgr", managerId);
        setup.Parameters.AddWithValue("unmgr", unassignedManagerId);
        setup.Parameters.AddWithValue("tech", techId);
        setup.Parameters.AddWithValue("tech2", tech2Id);
        setup.Parameters.AddWithValue("kiosk", kiosk);
        setup.Parameters.AddWithValue("slot", slot);
        setup.Parameters.AddWithValue("incident", incident);
        await setup.ExecuteNonQueryAsync();

        using var manager = factory.CreateClient();
        manager.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));

        using var unassignedManager = factory.CreateClient();
        unassignedManager.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("OPERATIONS_MANAGER", userId: unassignedManagerId));

        using var tech = factory.CreateClient();
        tech.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("TECHNICIAN", userId: techId));

        using var tech2 = factory.CreateClient();
        tech2.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("TECHNICIAN", userId: tech2Id));

        // 1. Manager staff list returns active technicians
        var techList = await manager.GetFromJsonAsync<List<StaffDirectoryItem>>("/api/operations/staff");
        Assert.NotNull(techList);
        Assert.Contains(techList, x => x.Id == techId);

        // 2. Unassigned manager sees empty incidents and cannot assign task (fail closed)
        var unassignedIncidents = await unassignedManager.GetFromJsonAsync<AdminIncidentPage>("/api/operations/incidents");
        Assert.NotNull(unassignedIncidents);
        Assert.Empty(unassignedIncidents.Items);

        var assignTask = new AssignStaffTask(taskId, techId, "INCIDENT", kiosk, null, incident, "Inspect and repair jammed door sensor");
        var unassignedAssign = await unassignedManager.PostAsJsonAsync("/api/operations/staff-tasks", assignTask);
        Assert.Equal(HttpStatusCode.Forbidden, unassignedAssign.StatusCode);

        // 3. Assigned manager sees incident queue and assigns task to technician
        var assignedIncidents = await manager.GetFromJsonAsync<AdminIncidentPage>("/api/operations/incidents");
        Assert.NotNull(assignedIncidents);
        Assert.Contains(assignedIncidents.Items, x => x.Id == incident);

        var assignRes = await manager.PostAsJsonAsync("/api/operations/staff-tasks", assignTask);
        assignRes.EnsureSuccessStatusCode();

        // 4. Technician views assigned task
        var techTasks = await tech.GetFromJsonAsync<StaffTaskPage>("/api/staff/tasks");
        Assert.NotNull(techTasks);
        Assert.Contains(techTasks.Items, x => x.Id == taskId);

        var detail = await tech.GetFromJsonAsync<StaffTaskDetail>($"/api/staff/tasks/{taskId}");
        Assert.NotNull(detail);
        Assert.Equal("ASSIGNED", techTasks.Items.Single(x => x.Id == taskId).Status);

        // 5. Tech accept/start
        var startRes = await tech.PostAsJsonAsync($"/api/staff/tasks/{taskId}/transition",
            new TransitionStaffTask(1, "IN_PROGRESS", "Arrived at kiosk and started door sensor inspection"));
        startRes.EnsureSuccessStatusCode();

        // 6. Technician cannot stock flowers (forbidden at API and SQL)
        var stockRes = await tech.GetAsync($"/api/staff/tasks/{taskId}/stock");
        Assert.Equal(HttpStatusCode.Forbidden, stockRes.StatusCode);

        var stockPost = await tech.PostAsJsonAsync($"/api/staff/tasks/{taskId}/stock",
            new StaffStockInput(Guid.NewGuid(), Guid.NewGuid(), slot, "invalid-tech-qr"));
        Assert.Equal(HttpStatusCode.Forbidden, stockPost.StatusCode);

        // 7. Technician cannot access seller admin or financial endpoints
        var walletRes = await tech.GetAsync("/api/sellers/20000000-0000-0000-0000-000000000001/wallet");
        Assert.True(walletRes.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);

        // 8. Manager has no financial access
        var mgrRefunds = await manager.GetAsync("/api/admin/refunds");
        Assert.Equal(HttpStatusCode.Forbidden, mgrRefunds.StatusCode);

        var mgrWithdrawals = await manager.GetAsync("/api/admin/withdrawals");
        Assert.Equal(HttpStatusCode.Forbidden, mgrWithdrawals.StatusCode);

        // 9. Technician resolves incident
        var resolveRes = await tech.PostAsJsonAsync($"/api/staff/tasks/{taskId}/resolve-incident",
            new StaffIncidentResolutionInput(Guid.NewGuid(), 2, "Sensor cleaned, unjammed, and door cycle verified"));
        resolveRes.EnsureSuccessStatusCode();

        // 10. Operations manager reviews and completes task
        var reviewRes = await manager.PostAsJsonAsync($"/api/operations/staff-tasks/{taskId}/transition",
            new TransitionStaffTask(3, "COMPLETED", "Technical report verified on site; incident closed"));
        reviewRes.EnsureSuccessStatusCode();

        var completedDetail = await manager.GetFromJsonAsync<StaffTaskDetail>($"/api/operations/staff-tasks/{taskId}");
        Assert.NotNull(completedDetail);
        Assert.Equal("COMPLETED", completedDetail.History[0].ToStatus);
    }

    [Fact]
    public async Task DeliveryTaskLifecycleAndTenantScopeEnforcement()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var sellerId = Guid.NewGuid();
        var otherSellerId = Guid.NewGuid();
        var sellerUserId = Guid.NewGuid();
        var otherSellerUserId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var staff2Id = Guid.NewGuid();
        var kiosk = Guid.NewGuid();
        var slot = Guid.NewGuid();
        var product = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at) VALUES
              (@seller,'Delivery Seller 1','0901111111','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30),
              (@otherSeller,'Delivery Seller 2','0902222222','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,full_name,password_hash,role,seller_id) VALUES
              (@sellerUser,@sellerEmail,'Owner 1','!unprovisioned','SELLER',@seller),
              (@otherSellerUser,@otherSellerEmail,'Owner 2','!unprovisioned','SELLER',@otherSeller),
              (@staff,@staff::text||'@example.invalid','Shop Staff 1','!unprovisioned','SELLER_STAFF',@seller),
              (@staff2,@staff2::text||'@example.invalid','Shop Staff 2','!unprovisioned','SELLER_STAFF',@seller);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Delivery Kiosk','Address','HCM',@kiosk::text,@kiosk::text,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel)
            VALUES(@slot,@kiosk,'D01',0);
            SELECT flow.assign_slot(@slot,@seller,'10000000-0000-0000-0000-000000000001'::uuid);
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status)
            VALUES(@product,@seller,'Delivery Rose',250000,48,'ACTIVE');
            """);
        setup.Parameters.AddWithValue("seller", sellerId);
        setup.Parameters.AddWithValue("otherSeller", otherSellerId);
        setup.Parameters.AddWithValue("sellerUser", sellerUserId);
        setup.Parameters.AddWithValue("sellerEmail", $"seller1-{sellerUserId:N}@example.invalid");
        setup.Parameters.AddWithValue("otherSellerUser", otherSellerUserId);
        setup.Parameters.AddWithValue("otherSellerEmail", $"seller2-{otherSellerUserId:N}@example.invalid");
        setup.Parameters.AddWithValue("staff", staffId);
        setup.Parameters.AddWithValue("staff2", staff2Id);
        setup.Parameters.AddWithValue("kiosk", kiosk);
        setup.Parameters.AddWithValue("slot", slot);
        setup.Parameters.AddWithValue("product", product);
        await setup.ExecuteNonQueryAsync();

        using var seller = factory.CreateClient();
        seller.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: sellerUserId, sellerId: sellerId));

        using var otherSellerClient = factory.CreateClient();
        otherSellerClient.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: otherSellerUserId, sellerId: otherSellerId));

        using var staff = factory.CreateClient();
        staff.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER_STAFF", userId: staffId, sellerId: sellerId));

        using var staff2 = factory.CreateClient();
        staff2.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER_STAFF", userId: staff2Id, sellerId: sellerId));

        // 1. Seller lists shop delivery staff
        var staffList = await seller.GetFromJsonAsync<List<StaffDirectoryItem>>($"/api/sellers/{sellerId}/staff");
        Assert.NotNull(staffList);
        Assert.Contains(staffList, x => x.Id == staffId);

        // 2. Seller lists delivery scopes
        var scopes = await seller.GetFromJsonAsync<List<StaffDeliveryScope>>($"/api/sellers/{sellerId}/staff-delivery-scopes");
        Assert.NotNull(scopes);
        Assert.Contains(scopes, x => x.KioskId == kiosk);

        // 3. Cross-tenant isolation: other seller cannot access seller's staff or delivery scopes
        var crossStaff = await otherSellerClient.GetAsync($"/api/sellers/{sellerId}/staff");
        Assert.Equal(HttpStatusCode.NotFound, crossStaff.StatusCode);

        // 4. Seller assigns delivery task to own staff
        var assignInput = new AssignStaffTask(taskId, staffId, "DELIVERY", kiosk, sellerId, null, "Restock slots with Delivery Roses");
        var assignRes = await seller.PostAsJsonAsync($"/api/sellers/{sellerId}/staff-tasks", assignInput);
        assignRes.EnsureSuccessStatusCode();

        // 5. Seller cannot assign a technician
        var techId = Guid.NewGuid();
        await using var addTech = data.CreateCommand("INSERT INTO identity.users(id,email,full_name,password_hash,role) VALUES(@id,@id::text||'@example.invalid','Foreign Tech','!unprovisioned','TECHNICIAN')");
        addTech.Parameters.AddWithValue("id", techId);
        await addTech.ExecuteNonQueryAsync();

        var assignTechRes = await seller.PostAsJsonAsync($"/api/sellers/{sellerId}/staff-tasks",
            assignInput with { Id = Guid.NewGuid(), AssigneeId = techId });
        Assert.Equal(HttpStatusCode.BadRequest, assignTechRes.StatusCode);

        // 6. Seller cannot assign INCIDENT task
        var assignIncidentRes = await seller.PostAsJsonAsync($"/api/sellers/{sellerId}/staff-tasks",
            assignInput with { Id = Guid.NewGuid(), Kind = "INCIDENT", IncidentId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, assignIncidentRes.StatusCode);

        // 7. Seller staff views assigned task
        var taskList = await staff.GetFromJsonAsync<StaffTaskPage>("/api/staff/tasks");
        Assert.NotNull(taskList);
        Assert.Contains(taskList.Items, x => x.Id == taskId);

        // 8. Seller staff cannot resolve incidents (forbidden)
        var resolveRes = await staff.PostAsJsonAsync($"/api/staff/tasks/{taskId}/resolve-incident",
            new StaffIncidentResolutionInput(Guid.NewGuid(), 1, "Attempting forbidden incident resolve"));
        Assert.Equal(HttpStatusCode.Forbidden, resolveRes.StatusCode);

        // 9. Seller staff accepts task
        var startRes = await staff.PostAsJsonAsync($"/api/staff/tasks/{taskId}/transition",
            new TransitionStaffTask(1, "IN_PROGRESS", "Started delivery and flower restocking"));
        startRes.EnsureSuccessStatusCode();

        // 10. Seller staff stocks flowers
        var stockRes = await staff.PostAsJsonAsync($"/api/staff/tasks/{taskId}/stock",
            new StaffStockInput(Guid.NewGuid(), product, slot, $"QR-{Guid.NewGuid():N}"));
        stockRes.EnsureSuccessStatusCode();

        // 11. Stale version rejected
        var staleTransition = await staff.PostAsJsonAsync($"/api/staff/tasks/{taskId}/transition",
            new TransitionStaffTask(1, "SUBMITTED", "Submitting with stale version 1"));
        Assert.Equal(HttpStatusCode.Conflict, staleTransition.StatusCode);

        // 12. Submit delivery report
        var submitRes = await staff.PostAsJsonAsync($"/api/staff/tasks/{taskId}/transition",
            new TransitionStaffTask(2, "SUBMITTED", "Stocking finished and all slots verified"));
        submitRes.EnsureSuccessStatusCode();

        // 13. Reassign during progress / handoff
        // First transition back to IN_PROGRESS by seller to test reassign
        var restartRes = await seller.PostAsJsonAsync($"/api/sellers/{sellerId}/staff-tasks/{taskId}/transition",
            new TransitionStaffTask(3, "IN_PROGRESS", "Requesting additional flowers before approval"));
        restartRes.EnsureSuccessStatusCode();

        var reassignRes = await seller.PostAsJsonAsync($"/api/sellers/{sellerId}/staff-tasks/{taskId}/reassign",
            new ReassignStaffTask(4, staff2Id, "Reassigned to second delivery staff"));
        reassignRes.EnsureSuccessStatusCode();

        // 14. Previous staff loses access immediately
        var oldStaffDetail = await staff.GetAsync($"/api/staff/tasks/{taskId}");
        Assert.Equal(HttpStatusCode.NotFound, oldStaffDetail.StatusCode);

        // 15. New staff has access and completes work
        var newStaffDetail = await staff2.GetFromJsonAsync<StaffTaskDetail>($"/api/staff/tasks/{taskId}");
        Assert.NotNull(newStaffDetail);
        Assert.Equal("STAFF_TASK_REASSIGNED", newStaffDetail.History[0].Action);

        var start2 = await staff2.PostAsJsonAsync($"/api/staff/tasks/{taskId}/transition",
            new TransitionStaffTask(5, "IN_PROGRESS", "Second staff accepts task"));
        start2.EnsureSuccessStatusCode();

        var submit2 = await staff2.PostAsJsonAsync($"/api/staff/tasks/{taskId}/transition",
            new TransitionStaffTask(6, "SUBMITTED", "Second staff completed remaining work"));
        submit2.EnsureSuccessStatusCode();

        var completeRes = await seller.PostAsJsonAsync($"/api/sellers/{sellerId}/staff-tasks/{taskId}/transition",
            new TransitionStaffTask(7, "COMPLETED", "Delivery approved by seller"));
        completeRes.EnsureSuccessStatusCode();

        // 16. Legacy staff cannot be assigned by Admin
        var legacyStaffId = Guid.NewGuid();
        await using var addLegacy = data.CreateCommand("INSERT INTO identity.users(id,email,full_name,password_hash,role) VALUES(@id,@id::text||'@example.invalid','Legacy Staff','!unprovisioned','STAFF')");
        addLegacy.Parameters.AddWithValue("id", legacyStaffId);
        await addLegacy.ExecuteNonQueryAsync();

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));

        var adminAssignLegacyDelivery = await admin.PostAsJsonAsync("/api/admin/staff-tasks",
            new AssignStaffTask(Guid.NewGuid(), legacyStaffId, "DELIVERY", kiosk, sellerId, null, "Try assigning legacy staff"));
        Assert.Equal(HttpStatusCode.BadRequest, adminAssignLegacyDelivery.StatusCode);
    }

    [Fact]
    public async Task ManagerScopeRevokedDeniesReplayAndFailsClosed()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var managerId = Guid.NewGuid();
        var techId = Guid.NewGuid();
        var kiosk = Guid.NewGuid();
        var slot = Guid.NewGuid();
        var incident = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            INSERT INTO identity.users(id,email,full_name,password_hash,role) VALUES
              (@mgr,@mgr::text||'@example.invalid','Scoped Ops Manager','!unprovisioned','OPERATIONS_MANAGER'),
              (@tech,@tech::text||'@example.invalid','Scoped Tech','!unprovisioned','TECHNICIAN');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Scoped Kiosk','Address','HCM',@kiosk::text,@kiosk::text,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,status)
            VALUES(@slot,@kiosk,'S01',0,'FAULT');
            INSERT INTO ordering.disputes(id,kind,kiosk_id,slot_id,reason)
            VALUES(@incident,'DEVICE_FAULT',@kiosk,@slot,'Scope test fault');
            INSERT INTO kiosk_ops.manager_kiosks(manager_id,kiosk_id)
            VALUES(@mgr,@kiosk);
            """);
        setup.Parameters.AddWithValue("mgr", managerId);
        setup.Parameters.AddWithValue("tech", techId);
        setup.Parameters.AddWithValue("kiosk", kiosk);
        setup.Parameters.AddWithValue("slot", slot);
        setup.Parameters.AddWithValue("incident", incident);
        await setup.ExecuteNonQueryAsync();

        using var manager = factory.CreateClient();
        manager.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));

        // 1. When mapped, manager sees active technician pool
        var staffList = await manager.GetFromJsonAsync<List<StaffDirectoryItem>>("/api/operations/staff");
        Assert.NotNull(staffList);
        Assert.Contains(staffList, x => x.Id == techId);

        // 2. Manager assigns task successfully
        var assignInput = new AssignStaffTask(taskId, techId, "INCIDENT", kiosk, null, incident, "Initial scoped assignment");
        var assignRes = await manager.PostAsJsonAsync("/api/operations/staff-tasks", assignInput);
        assignRes.EnsureSuccessStatusCode();

        // 3. Replay with identical payload succeeds while manager still has scope
        var replayWhileScoped = await manager.PostAsJsonAsync("/api/operations/staff-tasks", assignInput);
        replayWhileScoped.EnsureSuccessStatusCode();

        // 4. Revoke manager's kiosk scope
        await using var revoke = data.CreateCommand("DELETE FROM kiosk_ops.manager_kiosks WHERE manager_id=@mgr AND kiosk_id=@kiosk");
        revoke.Parameters.AddWithValue("mgr", managerId);
        revoke.Parameters.AddWithValue("kiosk", kiosk);
        await revoke.ExecuteNonQueryAsync();

        // 5. Manager with no mapped kiosks fails closed on technician pool
        var emptyStaffList = await manager.GetFromJsonAsync<List<StaffDirectoryItem>>("/api/operations/staff");
        Assert.NotNull(emptyStaffList);
        Assert.Empty(emptyStaffList);

        // 6. Security regression: replay of the same task ID is now DENIED because caller kiosk scope is checked before replay return
        var replayAfterRevocation = await manager.PostAsJsonAsync("/api/operations/staff-tasks", assignInput);
        Assert.Equal(HttpStatusCode.Forbidden, replayAfterRevocation.StatusCode);
    }

    [Fact]
    public async Task MigrationPreservesLegacyStaffRowsAndHistoryAndDeniesRuntimeAccess()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var legacyStaffId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var kiosk = Guid.NewGuid();
        var historicIncidentId = Guid.NewGuid();
        var historicTaskId = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            INSERT INTO identity.users(id,email,full_name,password_hash,role,seller_id) VALUES
              (@admin,@admin::text||'@example.invalid','History Admin','!unprovisioned','ADMIN',NULL),
              (@staff,@staff::text||'@example.invalid','Historical Staff Member','!unprovisioned','STAFF',NULL);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Historic Kiosk','Address','HCM',@kiosk::text,@kiosk::text,'ONLINE');
            INSERT INTO ordering.disputes(id,kind,kiosk_id,reason)
            VALUES(@incident,'DEVICE_FAULT',@kiosk,'Historic incident');
            INSERT INTO kiosk_ops.staff_tasks(id,kind,assignee_id,assigned_by,kiosk_id,incident_id,instructions,status,version)
            VALUES(@task,'INCIDENT',@staff,@admin,@kiosk,@incident,'Historical instructions before role split migration','COMPLETED',2);
            """);
        setup.Parameters.AddWithValue("admin", adminId);
        setup.Parameters.AddWithValue("staff", legacyStaffId);
        setup.Parameters.AddWithValue("kiosk", kiosk);
        setup.Parameters.AddWithValue("incident", historicIncidentId);
        setup.Parameters.AddWithValue("task", historicTaskId);
        await setup.ExecuteNonQueryAsync();

        // 1. Historical row is preserved in database with legacy STAFF role and NULL seller_id
        await using var verifyUser = data.CreateCommand("SELECT role, seller_id FROM identity.users WHERE id=@id");
        verifyUser.Parameters.AddWithValue("id", legacyStaffId);
        await using var userReader = await verifyUser.ExecuteReaderAsync();
        Assert.True(await userReader.ReadAsync());
        Assert.Equal("STAFF", userReader.GetString(0));
        Assert.True(userReader.IsDBNull(1));
        await userReader.DisposeAsync();

        // 2. Provisioning rejects legacy STAFF
        await using var tryProvision = data.CreateCommand("SELECT flow.provision_portal_user(@id,'legacy@test.vn','$2b$12$01234567890123456789012345678901234567890123456789012')");
        tryProvision.Parameters.AddWithValue("id", legacyStaffId);
        var pex = await Assert.ThrowsAsync<PostgresException>(() => tryProvision.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, pex.SqlState);

        // 3. Admin can inspect historical task detail and history without errors
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN", userId: adminId));
        var detail = await admin.GetFromJsonAsync<StaffTaskDetail>($"/api/admin/staff-tasks/{historicTaskId}");
        Assert.NotNull(detail);

        // 4. Token with legacy STAFF role is denied at runtime by authentication/policy
        using var legacyStaffClient = factory.CreateClient();
        legacyStaffClient.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("STAFF", userId: legacyStaffId));
        var meRes = await legacyStaffClient.GetAsync("/api/auth/me");
        Assert.True(meRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        var taskRes = await legacyStaffClient.GetAsync("/api/staff/tasks");
        Assert.True(taskRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }
}
