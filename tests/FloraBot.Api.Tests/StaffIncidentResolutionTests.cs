using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class StaffIncidentResolutionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedResolutionRestoresSlotAndSubmitsReportWithoutPayingRefund(bool refundPending)
    {
        using var scope = factory.Services.CreateScope(); var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var staffId = Guid.NewGuid(); var otherId = Guid.NewGuid(); var kiosk = Guid.NewGuid(); var slot = Guid.NewGuid();
        var incident = Guid.NewGuid(); var task = Guid.NewGuid(); var order = Guid.NewGuid(); var charge = Guid.NewGuid(); var refund = Guid.NewGuid();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.users(id,email,full_name,password_hash,role) VALUES
              (@staff,@staff::text||'@example.invalid','Resolution staff','!unprovisioned','TECHNICIAN'),
              (@other,@other::text||'@example.invalid','Other resolution staff','!unprovisioned','TECHNICIAN');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,api_key_hash)
              VALUES(@kiosk,@kiosk::text,'Resolution fixture','Fixture','HCM',@kiosk::text,@kiosk::text,'ONLINE','fixture');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,status) VALUES(@slot,@kiosk,'A01',0,'FAULT');
            INSERT INTO ordering.orders(id,order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token)
              VALUES(@order,@order::text,@order,@order,@kiosk,'DISPENSE_FAILED',350000,350000,flow.new_tracking_token());
            INSERT INTO ordering.disputes(id,kind,kiosk_id,slot_id,order_id,reason)
              VALUES(@incident,@kind,@kiosk,@slot,@order,'Door repaired after inspection');
            """);
        setup.Parameters.AddWithValue("staff", staffId); setup.Parameters.AddWithValue("other", otherId); setup.Parameters.AddWithValue("kiosk", kiosk);
        setup.Parameters.AddWithValue("slot", slot); setup.Parameters.AddWithValue("order", order); setup.Parameters.AddWithValue("incident", incident);
        setup.Parameters.AddWithValue("kind", refundPending ? "DISPENSE_FAILED" : "DEVICE_FAULT"); await setup.ExecuteNonQueryAsync();
        if (refundPending)
        {
            await using var payments = data.CreateCommand("""
                INSERT INTO payment.payments(id,kind,purpose,checkout_id,gateway,idempotency_key,amount,status,paid_at)
                  VALUES(@charge,'CHARGE','ORDER_CHECKOUT',@order,'MANUAL',@charge::text,350000,'SUCCEEDED',public.app_now());
                INSERT INTO payment.payments(id,kind,purpose,checkout_id,order_id,parent_payment_id,gateway,idempotency_key,amount,status,approved_by)
                  VALUES(@refund,'REFUND','ORDER_CHECKOUT',@order,@order,@charge,'MANUAL',@refund::text,350000,'PENDING',flow.sys_user());
                """);
            payments.Parameters.AddWithValue("charge", charge); payments.Parameters.AddWithValue("refund", refund); payments.Parameters.AddWithValue("order", order);
            await payments.ExecuteNonQueryAsync();
        }
        using var admin = factory.CreateClient(); using var staff = factory.CreateClient(); using var other = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        staff.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("TECHNICIAN", userId: staffId));
        other.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("TECHNICIAN", userId: otherId));
        (await admin.PostAsJsonAsync("/api/admin/staff-tasks", new AssignStaffTask(task, staffId, "INCIDENT", kiosk, null, incident, "Inspect door and record repair findings"))).EnsureSuccessStatusCode();
        var path = $"/api/staff/tasks/{task}/resolve-incident";
        var input = new StaffIncidentResolutionInput(Guid.NewGuid(), 2, "Door has been repaired and inspected on site");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync(path, input)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(path, input)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(path, input)).StatusCode);
        (await staff.PostAsJsonAsync($"/api/staff/tasks/{task}/transition", new TransitionStaffTask(1, "IN_PROGRESS", "Started inspecting the faulty door"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(path, input with { Report = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(path, input with { Version = 1 })).StatusCode);
        if (refundPending)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(path, input)).StatusCode);
            await using var check = data.CreateCommand("SELECT status FROM payment.payments WHERE id=@id"); check.Parameters.AddWithValue("id", refund);
            Assert.Equal("PENDING", await check.ExecuteScalarAsync());
            check.CommandText = "SELECT status FROM kiosk_ops.staff_tasks WHERE id=@id"; check.Parameters["id"].Value = task;
            Assert.Equal("IN_PROGRESS", await check.ExecuteScalarAsync());
            check.CommandText = "SELECT status FROM kiosk_ops.slots WHERE id=@id"; check.Parameters["id"].Value = slot;
            Assert.Equal("FAULT", await check.ExecuteScalarAsync());
            return;
        }
        var responses = await Task.WhenAll(staff.PostAsJsonAsync(path, input), staff.PostAsJsonAsync(path, input));
        foreach (var response in responses) response.EnsureSuccessStatusCode();
        Assert.Equal(3, (await responses[0].Content.ReadFromJsonAsync<StaffIncidentResolutionResult>())!.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(path, input with { Report = "Different repair report must not overwrite" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(path, input with { Id = Guid.NewGuid() })).StatusCode);
        await using var verify = data.CreateCommand("SELECT status FROM kiosk_ops.slots WHERE id=@id"); verify.Parameters.AddWithValue("id", slot);
        Assert.Equal("FREE", await verify.ExecuteScalarAsync());
        verify.CommandText = "SELECT status FROM ordering.disputes WHERE id=@id"; verify.Parameters["id"].Value = incident;
        Assert.Equal("RESOLVED_FIXED", await verify.ExecuteScalarAsync());
        verify.CommandText = "SELECT status FROM kiosk_ops.staff_tasks WHERE id=@id"; verify.Parameters["id"].Value = task;
        Assert.Equal("SUBMITTED", await verify.ExecuteScalarAsync());
        verify.CommandText = "SELECT count(*) FROM notify.audit_logs WHERE entity_id=@id AND action='STAFF_INCIDENT_RESOLVED'";
        Assert.Equal(1L, await verify.ExecuteScalarAsync());
        var detail = (await staff.GetFromJsonAsync<StaffTaskDetail>($"/api/staff/tasks/{task}"))!;
        Assert.Equal("STAFF_INCIDENT_RESOLVED", detail.History[0].Action);
        (await admin.PostAsJsonAsync($"/api/admin/staff-tasks/{task}/transition", new TransitionStaffTask(3, "COMPLETED", "Repair report reviewed and approved"))).EnsureSuccessStatusCode();
        (await staff.PostAsJsonAsync(path, input)).EnsureSuccessStatusCode();
    }
}
