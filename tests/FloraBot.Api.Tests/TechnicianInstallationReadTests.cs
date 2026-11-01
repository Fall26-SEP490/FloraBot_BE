using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Tests;

public sealed class TechnicianInstallationReadTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task ListIncludesHandedOverHistoryAndCurrentJobsOnlyForAssignedTechnician()
    {
        var technician = Guid.NewGuid();
        var otherTechnician = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using (var users = data.CreateCommand("""
            INSERT INTO identity.users(id,email,password_hash,full_name,role)
            VALUES(@first,@first::text || '@example.invalid','!test-only','History technician','TECHNICIAN'),
                  (@second,@second::text || '@example.invalid','!test-only','Other technician','TECHNICIAN');
            """))
        {
            users.Parameters.AddWithValue("first", technician);
            users.Parameters.AddWithValue("second", otherTechnician);
            await users.ExecuteNonQueryAsync();
        }

        var expected = new Dictionary<Guid, string>();
        Guid active = default, foreign = default, customer = default;
        foreach (var (status, assignee) in new (string, Guid?)[]
        {
            ("SCHEDULED", technician), ("INSTALLING", technician), ("AWAITING_HANDOVER", technician),
            ("ACTIVE", technician), ("ACTIVE", otherTechnician), ("CANCELLED", technician), ("ACTIVE", null)
        })
        {
            var id = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var kioskId = Guid.NewGuid();
            await using var seed = data.CreateCommand("""
                INSERT INTO identity.users(id,email,password_hash,full_name,role)
                VALUES(@customer,@customer::text || '@example.invalid','!test-only','History customer','CUSTOMER');
                INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
                VALUES(@kiosk,@kiosk::text,'History kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'OFFLINE');
                INSERT INTO kiosk_ops.kiosk_applications(
                    id,customer_id,package_id,shop_name,phone,contact_email,installation_address,
                    latitude,longitude,bank_name,bank_holder,bank_account_no_enc,status,
                    assigned_technician_id,assigned_kiosk_id,scheduled_date,time_window,handover_at)
                VALUES(@id,@customer,'20000000-0000-0000-0000-00000000000c','History shop',
                    '0901234567',@customer::text || '@example.invalid','Test address',10.8,106.7,
                    'TEST','TEST','test-only-placeholder',@status,@technician,@kiosk,CURRENT_DATE,
                    '09:00 - 12:00',CASE WHEN @status='ACTIVE' THEN public.app_now() ELSE NULL END);
                """);
            seed.Parameters.AddWithValue("id", id);
            seed.Parameters.AddWithValue("customer", customerId);
            seed.Parameters.AddWithValue("kiosk", kioskId);
            seed.Parameters.AddWithValue("status", status);
            seed.Parameters.AddWithValue("technician", NpgsqlDbType.Uuid, (object?)assignee ?? DBNull.Value);
            await seed.ExecuteNonQueryAsync();
            if (assignee == technician && status != "CANCELLED") expected.Add(id, status);
            if (status == "ACTIVE" && assignee == technician) { active = id; customer = customerId; }
            if (assignee == otherTechnician) foreign = id;
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("TECHNICIAN", userId: technician));
        const string path = "/api/staff/kiosk-installations";
        var first = await client.GetFromJsonAsync<List<TechnicianInstallationSummary>>(path);
        Assert.NotNull(first);
        Assert.Equal(expected.Keys.Order(), first.Select(x => x.Id).Order());
        Assert.All(first, row => Assert.Equal(expected[row.Id], row.Status));
        Assert.All(first, row => Assert.False(string.IsNullOrWhiteSpace(row.KioskCode)));

        // Refreshing the history remains a read. It must not require another handover.
        var again = await client.GetFromJsonAsync<List<TechnicianInstallationSummary>>(path);
        Assert.Equal(first.OrderBy(x => x.Id), again!.OrderBy(x => x.Id));
        var detail = await client.GetFromJsonAsync<TechnicianInstallationDetail>($"{path}/{active}");
        Assert.Equal("ACTIVE", detail!.Status);
        Assert.Equal(active, detail.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/{foreign}")).StatusCode);

        using var other = factory.CreateClient();
        other.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("TECHNICIAN", userId: otherTechnician));
        var otherJobs = await other.GetFromJsonAsync<List<TechnicianInstallationSummary>>(path);
        Assert.Equal(foreign, Assert.Single(otherJobs!).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{path}/{active}")).StatusCode);

        using var member = factory.CreateClient();
        member.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("CUSTOMER", userId: customer));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(path)).StatusCode);
    }
}
