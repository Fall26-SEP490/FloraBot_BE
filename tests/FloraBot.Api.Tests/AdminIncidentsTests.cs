using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Modules.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class AdminIncidentsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
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
