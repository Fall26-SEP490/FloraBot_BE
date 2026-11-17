using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Modules.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class AdminIncidentsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task IncidentReadsExposeCurrentTaskAndRespectManagerKioskScope()
    {
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        var kiosk = Guid.NewGuid();
        var incident = Guid.NewGuid();
        var technician = Guid.NewGuid();
        var manager = Guid.NewGuid();
        var otherManager = Guid.NewGuid();
        var task = Guid.NewGuid();
        await using var seed = data.CreateCommand("""
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Incident workflow kiosk','Address','HCM',@kiosk::text,@kiosk::text,'OFFLINE');
            INSERT INTO identity.users(id,email,password_hash,full_name,role)
            VALUES(@tech,@tech::text||'@example.test','!','Technician test','TECHNICIAN'),
                  (@manager,@manager::text||'@example.test','!','Manager test','OPERATIONS_MANAGER'),
                  (@other,@other::text||'@example.test','!','Other manager','OPERATIONS_MANAGER');
            INSERT INTO kiosk_ops.manager_kiosks(manager_id,kiosk_id) VALUES(@manager,@kiosk);
            INSERT INTO ordering.disputes(id,kind,kiosk_id,reason) VALUES(@incident,'DEVICE_FAULT',@kiosk,'Test incident');
            """);
        foreach (var (name, id) in new[] { ("kiosk", kiosk), ("incident", incident), ("tech", technician), ("manager", manager), ("other", otherManager) })
            seed.Parameters.AddWithValue(name, id);
        await seed.ExecuteNonQueryAsync();
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        using var operations = factory.CreateClient();
        operations.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("OPERATIONS_MANAGER", userId: manager));
        var adminPath = $"/api/admin/incidents?kioskId={kiosk}";
        var managerPath = $"/api/operations/incidents?kioskId={kiosk}";
        var detailPath = $"/api/admin/incidents/{incident}";

        async Task Check(string progress, string? status)
        {
            var adminRow = Assert.Single((await admin.GetFromJsonAsync<AdminIncidentPage>(adminPath))!.Items);
            var managerRow = Assert.Single((await operations.GetFromJsonAsync<AdminIncidentPage>(managerPath))!.Items);
            var detail = (await admin.GetFromJsonAsync<AdminIncidentDetail>(detailPath))!.Incident;
            foreach (var row in new[] { adminRow, managerRow, detail })
            {
                Assert.Equal(progress, row.Progress);
                Assert.Equal(status, row.Task?.Status);
                if (status is not null)
                {
                    Assert.Equal(task, row.Task!.Id);
                    Assert.Equal(technician, row.Task.AssigneeId);
                    Assert.Equal("Technician test", row.Task.AssigneeName);
                }
            }
        }

        await Check("UNASSIGNED", null);
        await using var write = data.CreateCommand("""
            INSERT INTO kiosk_ops.staff_tasks(id,kind,assignee_id,assigned_by,kiosk_id,incident_id,instructions)
            VALUES(@task,'INCIDENT',@tech,@manager,@kiosk,@incident,'Check kiosk door');
            """);
        foreach (var (name, id) in new[] { ("task", task), ("tech", technician), ("manager", manager), ("kiosk", kiosk), ("incident", incident) })
            write.Parameters.AddWithValue(name, id);
        await write.ExecuteNonQueryAsync();
        await Check("IN_PROGRESS", "ASSIGNED");
        write.CommandText = "UPDATE kiosk_ops.staff_tasks SET status=@status WHERE id=@task";
        var state = write.Parameters.AddWithValue("status", "IN_PROGRESS");
        foreach (var (status, progress) in new[] { ("IN_PROGRESS", "IN_PROGRESS"), ("SUBMITTED", "AWAITING_REVIEW"), ("COMPLETED", "COMPLETED") })
        {
            state.Value = status;
            await write.ExecuteNonQueryAsync();
            await Check(progress, status);
        }
        operations.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("OPERATIONS_MANAGER", userId: otherManager));
        Assert.Empty((await operations.GetFromJsonAsync<AdminIncidentPage>(managerPath))!.Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await operations.GetAsync(detailPath)).StatusCode);
    }

    [Fact]
    public async Task QueueIsAdminOnlyPagedAndResolutionUsesSourceFlowAndAudit()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var kiosk = Guid.NewGuid(); var slot = Guid.NewGuid();
        await using var fixture = data.CreateCommand("""
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,api_key_hash)
            VALUES(@kiosk,@kiosk::text,'Incident test kiosk','Incident address','HCM',@kiosk::text,@kiosk::text,'OFFLINE','unused');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel) VALUES(@slot,@kiosk,'A01',0);
            SELECT flow.report_device_fault(@kiosk,@slot,'10000000-0000-0000-0000-000000000001','Door fault evidence','https://example.invalid/fault.jpg');
            """);
        fixture.Parameters.AddWithValue("kiosk", kiosk); fixture.Parameters.AddWithValue("slot", slot);
        var id = (Guid)(await fixture.ExecuteScalarAsync())!;
        await using var more = data.CreateCommand("""
            INSERT INTO ordering.disputes(kind,kiosk_id,reason,created_at)
            SELECT 'DEVICE_FAULT',@kiosk,'Queue fixture ' || n, now()+interval '1 hour' FROM generate_series(1,25) n;
            """);
        more.Parameters.AddWithValue("kiosk", kiosk); await more.ExecuteNonQueryAsync();
        var path = $"/api/admin/incidents?kioskId={kiosk}";
        using var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var response = await admin.GetAsync(path);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var first = (await response.Content.ReadFromJsonAsync<AdminIncidentPage>())!;
        Assert.Equal(25, first.Items.Count); Assert.True(first.HasMore); Assert.Equal(1, first.Page);
        Assert.Equal(id, first.Items[0].Id); Assert.Equal("Incident test kiosk", first.Items[0].Location!.KioskName);
        Assert.Equal("FAULT", first.Items[0].Slot!.Status);
        var second = (await admin.GetFromJsonAsync<AdminIncidentPage>(path + "&page=2"))!;
        Assert.Single(second.Items); Assert.False(second.HasMore);
        Assert.DoesNotContain(second.Items[0].Id, first.Items.Select(x => x.Id));
        var detailPath = $"/api/admin/incidents/{id}";
        var detail = (await admin.GetFromJsonAsync<AdminIncidentDetail>(detailPath))!;
        Assert.Equal("https://example.invalid/fault.jpg", Assert.Single(detail.Evidence.Items).Url);
        Assert.False(detail.Evidence.HasMore);
        using var seller = factory.CreateClient(); seller.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        using var anonymous = factory.CreateClient();
        foreach (var url in new[] { path, detailPath })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await seller.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path + "&page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path + "&status=ALL")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/incidents/{Guid.NewGuid()}")).StatusCode);
        var body = new { p_dispute = id, p_note = "Checked and repaired the door" };
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsJsonAsync("/api/admin/flows/resolve_device_fault", body)).StatusCode);
        (await admin.PostAsJsonAsync("/api/admin/flows/resolve_device_fault", body)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/flows/resolve_device_fault", body)).StatusCode);
        var resolved = (await admin.GetFromJsonAsync<AdminIncidentDetail>(detailPath))!;
        Assert.Equal("RESOLVED_FIXED", resolved.Incident.Status); Assert.Equal(body.p_note, resolved.Incident.Decision);
        Assert.NotNull(resolved.Incident.ResolvedAt); Assert.Equal("FREE", resolved.Incident.Slot!.Status);
        Assert.Single((await admin.GetFromJsonAsync<AdminIncidentPage>(path + "&status=RESOLVED_FIXED"))!.Items);
        await using var audit = data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE action='DEVICE_FAULT_RESOLVED' AND entity_id=@id AND actor_id='10000000-0000-0000-0000-000000000001'");
        audit.Parameters.AddWithValue("id", id); Assert.Equal(1L, await audit.ExecuteScalarAsync());
    }
}
