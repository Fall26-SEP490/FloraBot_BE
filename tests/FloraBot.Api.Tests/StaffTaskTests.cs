using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class StaffTaskTests
{
    [Fact]
    public async Task DeliveryAssignmentRequiresActiveSellerAtTheSelectedKiosk()
    {
        await using var app = new ApiFactory();
        using var scope = app.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        var adminId = Guid.NewGuid(); var staffId = Guid.NewGuid(); var seller = Guid.NewGuid(); var kiosk = Guid.NewGuid(); var slot = Guid.NewGuid();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Delivery shop','0901112233','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,full_name,password_hash,role,seller_id) VALUES
              (@admin,@admin::text||'@example.invalid','Delivery admin','!unprovisioned','ADMIN',NULL),
              (@staff,@staff::text||'@example.invalid','Delivery staff','!unprovisioned','SELLER_STAFF',@seller);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Delivery kiosk','Delivery address','HCM',@kiosk::text,@kiosk::text,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel) VALUES(@slot,@kiosk,'A01',0);
            SELECT flow.assign_slot(@slot,@seller,@admin);
            """);
        foreach (var pair in new[] { ("admin", adminId), ("staff", staffId), ("seller", seller), ("kiosk", kiosk), ("slot", slot) }) setup.Parameters.AddWithValue(pair.Item1, pair.Item2);
        await setup.ExecuteNonQueryAsync();
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = adminId, Role = "ADMIN", FullName = "Admin", PasswordHash = "!unprovisioned" }));
        var scopes = await admin.GetFromJsonAsync<List<StaffDeliveryScope>>("/api/admin/staff-delivery-scopes");
        Assert.Contains(scopes!, x => x.SellerId == seller && x.KioskId == kiosk);
        var input = new AssignStaffTask(Guid.NewGuid(), staffId, "DELIVERY", kiosk, seller, null, "Deliver flowers to the assigned kiosk");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/staff-tasks", input with { KioskId = Guid.Parse("40000000-0000-0000-0000-000000000001") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/staff-tasks", input with { AssigneeId = adminId })).StatusCode);
        (await admin.PostAsJsonAsync("/api/admin/staff-tasks", input)).EnsureSuccessStatusCode();
        var product = Guid.NewGuid(); var spareSlot = Guid.NewGuid();
        await using var inventory = data.CreateCommand("""
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status)
            VALUES(@product,@seller,'Staff delivery bouquet',350000,48,'ACTIVE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel) VALUES(@spare,@kiosk,'A02',1);
            SELECT flow.assign_slot(@spare,@seller,@admin);
            """);
        inventory.Parameters.AddWithValue("product", product); inventory.Parameters.AddWithValue("seller", seller);
        inventory.Parameters.AddWithValue("spare", spareSlot); inventory.Parameters.AddWithValue("kiosk", kiosk); inventory.Parameters.AddWithValue("admin", adminId);
        await inventory.ExecuteNonQueryAsync();
        using var staff = app.CreateClient();
        staff.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = staffId, Role = "SELLER_STAFF", SellerId = seller, FullName = "Staff", PasswordHash = "!unprovisioned" }, "ACTIVE"));
        var stockPath = $"/api/staff/tasks/{input.Id}/stock";
        var stock = new StaffStockInput(Guid.NewGuid(), product, slot, $"staff-{Guid.NewGuid():N}");
        var otherStaffId = Guid.NewGuid();
        await using var otherSetup = data.CreateCommand("INSERT INTO identity.users(id,email,full_name,password_hash,role,seller_id) VALUES(@id,@id::text||'@example.invalid','Other delivery staff','!unprovisioned','SELLER_STAFF',@seller)");
        otherSetup.Parameters.AddWithValue("id", otherStaffId);
        otherSetup.Parameters.AddWithValue("seller", seller);
        await otherSetup.ExecuteNonQueryAsync();
        using var otherStaff = app.CreateClient();
        otherStaff.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = otherStaffId, Role = "SELLER_STAFF", SellerId = seller, FullName = "Other staff", PasswordHash = "!unprovisioned" }, "ACTIVE"));
        Assert.Equal(HttpStatusCode.NotFound, (await otherStaff.GetAsync(stockPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherStaff.PostAsJsonAsync(stockPath, stock)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(stockPath, stock)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(stockPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/staff/tasks/{Guid.NewGuid()}/stock")).StatusCode);
        (await staff.PostAsJsonAsync($"/api/staff/tasks/{input.Id}/transition", new TransitionStaffTask(1, "IN_PROGRESS", "Started the assigned delivery"))).EnsureSuccessStatusCode();
        var options = await staff.GetFromJsonAsync<StaffStockOptions>(stockPath);
        Assert.Contains(options!.Products, x => x.Id == product); Assert.Equal(2, options.Slots.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(stockPath, stock with { ProductId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(stockPath, stock with { SlotId = Guid.NewGuid() })).StatusCode);
        await using var maintenance = data.CreateCommand("UPDATE kiosk_ops.kiosks SET status='MAINTENANCE' WHERE id=@id");
        maintenance.Parameters.AddWithValue("id", kiosk);
        await maintenance.ExecuteNonQueryAsync();
        Assert.Empty((await staff.GetFromJsonAsync<StaffStockOptions>(stockPath))!.Slots);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(stockPath, stock)).StatusCode);
        maintenance.CommandText = "UPDATE kiosk_ops.kiosks SET status='ONLINE' WHERE id=@id";
        await maintenance.ExecuteNonQueryAsync();
        await using var package = data.CreateCommand("UPDATE identity.sellers SET package_expires_at=CURRENT_DATE-1 WHERE id=@id");
        package.Parameters.AddWithValue("id", seller);
        await package.ExecuteNonQueryAsync();
        Assert.Empty((await staff.GetFromJsonAsync<StaffStockOptions>(stockPath))!.Slots);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(stockPath, stock)).StatusCode);
        package.CommandText = "UPDATE identity.sellers SET package_expires_at=CURRENT_DATE+30 WHERE id=@id";
        await package.ExecuteNonQueryAsync();
        var results = await Task.WhenAll(staff.PostAsJsonAsync(stockPath, stock), staff.PostAsJsonAsync(stockPath, stock));
        foreach (var response in results) response.EnsureSuccessStatusCode();
        var first = (await results[0].Content.ReadFromJsonAsync<StaffStockResult>())!;
        Assert.Equal(first.BouquetId, (await results[1].Content.ReadFromJsonAsync<StaffStockResult>())!.BouquetId);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(stockPath, stock with { QrCode = "changed-qr" })).StatusCode);
        options = await staff.GetFromJsonAsync<StaffStockOptions>(stockPath);
        Assert.Single(options!.Actions); Assert.Single(options.Slots);
        await using var log = data.CreateCommand("SELECT count(*) FROM kiosk_ops.inventory_logs WHERE bouquet_id=@id AND performed_by=@staff");
        log.Parameters.AddWithValue("id", first.BouquetId); log.Parameters.AddWithValue("staff", staffId);
        Assert.Equal(1L, await log.ExecuteScalarAsync());
        (await staff.PostAsJsonAsync($"/api/staff/tasks/{input.Id}/transition", new TransitionStaffTask(2, "SUBMITTED", "Delivery recorded and ready for review"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(stockPath, stock with { Id = Guid.NewGuid(), SlotId = spareSlot, QrCode = "new-qr-after-submit" })).StatusCode);
        // Recovery of an already committed operation remains safe after the report is submitted.
        (await staff.PostAsJsonAsync(stockPath, stock)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/admin/staff-tasks/{input.Id}/transition", new TransitionStaffTask(3, "IN_PROGRESS", "Check remaining flowers before closing"))).EnsureSuccessStatusCode();
        await using var suspend = data.CreateCommand("UPDATE identity.sellers SET status='SUSPENDED' WHERE id=@id");
        suspend.Parameters.AddWithValue("id", seller); await suspend.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(stockPath, stock with { Id = Guid.NewGuid(), SlotId = spareSlot, QrCode = "suspended-shop-flower" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/staff-tasks", input with { Id = Guid.NewGuid() })).StatusCode);
        scopes = await admin.GetFromJsonAsync<List<StaffDeliveryScope>>("/api/admin/staff-delivery-scopes");
        Assert.DoesNotContain(scopes!, x => x.SellerId == seller);
        var handoffPath = $"/api/admin/staff-tasks/{input.Id}/reassign";
        var handoff = new ReassignStaffTask(4, otherStaffId, "Transfer remaining work to the next shift");
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync(handoffPath, handoff)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(handoffPath, handoff with { AssigneeId = staffId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(handoffPath, handoff with { AssigneeId = adminId })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(handoffPath, handoff with { Version = 3 })).StatusCode);
        var handoffs = await Task.WhenAll(admin.PostAsJsonAsync(handoffPath, handoff), admin.PostAsJsonAsync(handoffPath, handoff));
        Assert.Single(handoffs, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(handoffs, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync(stockPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync(stockPath, stock)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/staff/tasks/{input.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/staff/tasks/{input.Id}/transition", new TransitionStaffTask(5, "IN_PROGRESS", "Old assignee must not restart the task"))).StatusCode);
        var transferred = Assert.Single((await otherStaff.GetFromJsonAsync<StaffTaskPage>("/api/staff/tasks"))!.Items);
        Assert.Equal("ASSIGNED", transferred.Status); Assert.Equal(5, transferred.Version);
        Assert.Single((await otherStaff.GetFromJsonAsync<StaffStockOptions>(stockPath))!.Actions);
        var detail = (await otherStaff.GetFromJsonAsync<StaffTaskDetail>($"/api/staff/tasks/{input.Id}"))!;
        Assert.Equal("STAFF_TASK_REASSIGNED", detail.History[0].Action);
        Assert.Equal(handoff.Reason, detail.History[0].Report);
        (await otherStaff.PostAsJsonAsync($"/api/staff/tasks/{input.Id}/transition", new TransitionStaffTask(5, "IN_PROGRESS", "New assignee accepts the handed-over task"))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task IncidentTaskEnforcesAssigneeVersionReviewAndRevocation()
    {
        await using var app = new ApiFactory();
        using var scope = app.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        var adminId = Guid.NewGuid(); var staffId = Guid.NewGuid(); var otherId = Guid.NewGuid(); var incident = Guid.NewGuid(); var taskId = Guid.NewGuid();
        var kiosk = Guid.Parse("40000000-0000-0000-0000-000000000001");
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.users(id,email,full_name,password_hash,role) VALUES
              (@admin,@admin::text||'@example.invalid','Task admin','!unprovisioned','ADMIN'),
              (@staff,@staff::text||'@example.invalid','Task staff','!unprovisioned','TECHNICIAN'),
              (@other,@other::text||'@example.invalid','Other staff','!unprovisioned','TECHNICIAN');
            INSERT INTO ordering.disputes(id,kind,kiosk_id,reason) VALUES(@incident,'DEVICE_FAULT',@kiosk,'Door stuck test');
            """);
        setup.Parameters.AddWithValue("admin", adminId); setup.Parameters.AddWithValue("staff", staffId);
        setup.Parameters.AddWithValue("other", otherId); setup.Parameters.AddWithValue("incident", incident); setup.Parameters.AddWithValue("kiosk", kiosk);
        await setup.ExecuteNonQueryAsync();
        HttpClient Client(Guid id, string role)
        {
            var client = app.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = id, Role = role, FullName = "Task test", PasswordHash = "!unprovisioned" }));
            return client;
        }
        using var admin = Client(adminId, "ADMIN"); using var staff = Client(staffId, "TECHNICIAN"); using var other = Client(otherId, "TECHNICIAN");
        var input = new AssignStaffTask(taskId, staffId, "INCIDENT", kiosk, null, incident, "Inspect the door and report findings");
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync("/api/admin/staff-tasks", input)).StatusCode);
        (await admin.PostAsJsonAsync("/api/admin/staff-tasks", input)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/admin/staff-tasks", input)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/staff-tasks", input with { Instructions = "Different task instructions" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/staff-tasks", input with { Id = Guid.NewGuid() })).StatusCode);
        Assert.Single((await staff.GetFromJsonAsync<StaffTaskPage>("/api/staff/tasks"))!.Items);
        Assert.Empty((await other.GetFromJsonAsync<StaffTaskPage>("/api/staff/tasks"))!.Items);
        var detailsPath = $"/api/staff/tasks/{taskId}";
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(detailsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.GetAsync(detailsPath + "?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/staff/tasks/{Guid.NewGuid()}")).StatusCode);
        var initial = (await staff.GetFromJsonAsync<StaffTaskDetail>(detailsPath))!;
        Assert.Equal("Door stuck test", initial.Incident!.Reason);
        Assert.Equal("OPEN", initial.Incident.Status);
        Assert.Equal("STAFF_TASK_ASSIGNED", Assert.Single(initial.History).Action);
        Assert.False(initial.HasMore);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync($"/api/admin/staff-tasks/{taskId}")).StatusCode);
        var path = $"/api/staff/tasks/{taskId}/transition";
        var start = new TransitionStaffTask(1, "IN_PROGRESS", "Started checking door assembly");
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(path, start)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(path, start with { Status = "COMPLETED" })).StatusCode);
        (await staff.PostAsJsonAsync(path, start)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(path, start)).StatusCode);
        (await staff.PostAsJsonAsync(path, new TransitionStaffTask(2, "SUBMITTED", "Door inspection finished; awaiting review"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(path, new TransitionStaffTask(3, "COMPLETED", "Approve own work is forbidden"))).StatusCode);
        (await admin.PostAsJsonAsync($"/api/admin/staff-tasks/{taskId}/transition", new TransitionStaffTask(3, "COMPLETED", "Report reviewed by administrator"))).EnsureSuccessStatusCode();
        Assert.Equal("COMPLETED", Assert.Single((await staff.GetFromJsonAsync<StaffTaskPage>("/api/staff/tasks"))!.Items).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/staff-tasks/{taskId}/reassign", new ReassignStaffTask(4, otherId, "Closed task must not be reassigned"))).StatusCode);
        var completed = (await admin.GetFromJsonAsync<StaffTaskDetail>($"/api/admin/staff-tasks/{taskId}"))!;
        Assert.Equal(4, completed.History.Count);
        Assert.Equal("COMPLETED", completed.History[0].ToStatus);
        Assert.Equal("Task admin", completed.History[0].ActorName);
        Assert.Equal("Door inspection finished; awaiting review", completed.History[1].Report);
        Assert.Empty((await staff.GetFromJsonAsync<StaffTaskDetail>(detailsPath + "?page=2"))!.History);
        await using var historySeed = data.CreateCommand("SELECT flow.audit(@actor,'STAFF_TASK_UPDATED','staff_tasks',@task,jsonb_build_object('report','Historical fixture entry '||n)) FROM generate_series(1,26) n");
        historySeed.Parameters.AddWithValue("actor", adminId); historySeed.Parameters.AddWithValue("task", taskId);
        await historySeed.ExecuteNonQueryAsync();
        var firstHistory = (await staff.GetFromJsonAsync<StaffTaskDetail>(detailsPath))!;
        var secondHistory = (await staff.GetFromJsonAsync<StaffTaskDetail>(detailsPath + "?page=2"))!;
        Assert.Equal(25, firstHistory.History.Count); Assert.True(firstHistory.HasMore);
        Assert.Equal(5, secondHistory.History.Count); Assert.False(secondHistory.HasMore);
        Assert.Empty(firstHistory.History.Select(x => x.Id).Intersect(secondHistory.History.Select(x => x.Id)));
        await using var check = data.CreateCommand("SELECT status FROM ordering.disputes WHERE id=@id");
        check.Parameters.AddWithValue("id", incident);
        Assert.Equal("OPEN", await check.ExecuteScalarAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/admin/refunds")).StatusCode);
        await using var revoke = data.CreateCommand("UPDATE identity.users SET status='LOCKED' WHERE id=@id");
        revoke.Parameters.AddWithValue("id", staffId); await revoke.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync("/api/staff/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync(detailsPath)).StatusCode);
    }
}
