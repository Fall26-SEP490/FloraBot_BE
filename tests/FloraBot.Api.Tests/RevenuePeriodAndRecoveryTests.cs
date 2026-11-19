using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class RevenuePeriodAndRecoveryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task SellerSearchAndPaginationReachBeyondTheFirstHundredShops()
    {
        var prefix = Guid.NewGuid().ToString();
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var seed = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id)
              SELECT public.uuid_v7(),@prefix||'-'||lpad(n::text,3,'0'),'0900000000','APPROVED',
                '20000000-0000-0000-0000-00000000000a'::uuid FROM generate_series(1,105) n;
            """);
        seed.Parameters.AddWithValue("prefix", prefix);
        await seed.ExecuteNonQueryAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var ids = new HashSet<Guid>();
        for (var page = 1; page <= 5; page++)
        {
            using var rows = await client.GetFromJsonAsync<JsonDocument>($"/api/admin/sellers?search={prefix}&page={page}");
            foreach (var row in rows!.RootElement.EnumerateArray()) Assert.True(ids.Add(row.GetProperty("id").GetGuid()));
        }
        Assert.Equal(105, ids.Count);
        using var found = await client.GetFromJsonAsync<JsonDocument>($"/api/admin/sellers?search={prefix}-001&page=1");
        Assert.Equal(prefix + "-001", Assert.Single(found!.RootElement.EnumerateArray()).GetProperty("shopName").GetString());
    }

    [Fact]
    public async Task SharedCheckoutAllocatesOnceAndRefundUsesItsOwnMonthAndSeller()
    {
        var ids = "seller other kiosk checkout first second charge".Split(' ').ToDictionary(k => k, _ => Guid.NewGuid());
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var seed = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id)
              VALUES(@seller,'Allocation A','0900000000','APPROVED','20000000-0000-0000-0000-00000000000a'),
                    (@other,'Allocation B','0900000001','APPROVED','20000000-0000-0000-0000-00000000000a');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
              VALUES(@kiosk,@kiosk::text,'Allocation fixture','Test','HCM',@kiosk::text,@kiosk::text,'OFFLINE');
            INSERT INTO ordering.orders(id,order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token)
              VALUES(@first,@first::text,@checkout,@seller,@kiosk,'PAID',100,100,@first::text),
                    (@second,@second::text,@checkout,@other,@kiosk,'PAID',300,300,@second::text);
            INSERT INTO payment.payments(id,kind,purpose,checkout_id,gateway,idempotency_key,amount,status,paid_at)
              VALUES(@charge,'CHARGE','ORDER_CHECKOUT',@checkout,'MANUAL',@charge::text,400,'SUCCEEDED','2025-01-31 16:59:59+00');
            INSERT INTO payment.payments(kind,purpose,checkout_id,order_id,parent_payment_id,gateway,idempotency_key,amount,status,paid_at,approved_by)
              VALUES('REFUND','ORDER_CHECKOUT',@checkout,@first,@charge,'MANUAL',@first::text,40,'SUCCEEDED','2025-01-31 17:00:00+00',flow.sys_user()),
                    ('REFUND','ORDER_CHECKOUT',@checkout,@first,@charge,'MANUAL',@second::text,10,'PENDING',NULL,flow.sys_user());
            """);
        foreach (var (key, id) in ids) seed.Parameters.AddWithValue(key, id);
        await seed.ExecuteNonQueryAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        async Task<JsonElement> Totals(Guid seller, string from, string to) =>
            (await client.GetFromJsonAsync<AdminRevenuePage>($"/api/admin/revenue/flowers?sellerId={seller}&from={from}&to={to}"))!.Totals;
        var january = await Totals(ids["seller"], "2025-01-01", "2025-01-31");
        Assert.Equal("100", january.GetProperty("grossPaid").GetString());
        Assert.Equal("0", january.GetProperty("totalRefunded").GetString());
        var february = await Totals(ids["seller"], "2025-02-01", "2025-02-28");
        Assert.Equal("0", february.GetProperty("grossPaid").GetString());
        Assert.Equal("40", february.GetProperty("totalRefunded").GetString());
        Assert.Equal("-40", february.GetProperty("netReceipts").GetString());
        var other = await Totals(ids["other"], "2025-01-01", "2025-02-28");
        Assert.Equal("300", other.GetProperty("grossPaid").GetString());
        Assert.Equal("0", other.GetProperty("totalRefunded").GetString());
    }

    [Fact]
    public async Task RentalRevenueUsesLedgerAndDoesNotMixWithFlowerReceipts()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        const string period = "from=2041-02-01&to=2041-02-01";
        var before = await client.GetFromJsonAsync<AdminRevenuePage>($"/api/admin/revenue/rental?{period}");
        var id = Guid.NewGuid();
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var seed = data.CreateCommand("""
            INSERT INTO payment.ledger_entries(journal_id,account,amount,ref_type,ref_id,created_at)
              VALUES(@id,'PLATFORM_SUBSCRIPTION',-1000,'SUBSCRIPTION',@id,'2041-01-31 17:00:00+00'),
                    (@id,'GATEWAY_CLEARING',1000,'SUBSCRIPTION',@id,'2041-01-31 17:00:00+00');
            """);
        seed.Parameters.AddWithValue("id", id);
        await seed.ExecuteNonQueryAsync();
        var rental = await client.GetFromJsonAsync<AdminRevenuePage>($"/api/admin/revenue/rental?{period}");
        Assert.Equal(long.Parse(before!.Totals.GetProperty("rentalRevenue").GetString()!) + 1000,
            long.Parse(rental!.Totals.GetProperty("rentalRevenue").GetString()!));
        Assert.Equal("0", rental.Totals.GetProperty("grossPaid").GetString());
        var flowers = await client.GetFromJsonAsync<AdminRevenuePage>($"/api/admin/revenue/flowers?{period}");
        Assert.Equal("0", flowers!.Totals.GetProperty("rentalRevenue").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/admin/revenue/rental?{period}&sellerId={id}")).StatusCode);
    }

    [Fact]
    public async Task RevenueUsesFullPeriodTotalsAcrossPagesAndVietnamDateBoundaries()
    {
        var seller = Guid.NewGuid(); var kiosk = Guid.NewGuid();
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var seed = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id)
              VALUES(@seller,'Period report fixture','0900000000','APPROVED','20000000-0000-0000-0000-00000000000a');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
              VALUES(@kiosk,@kiosk::text,'History fixture','Test','HCM',@kiosk::text,@kiosk::text,'OFFLINE');
            WITH rows AS (SELECT public.uuid_v7() AS id,n FROM generate_series(0,129) n), orders AS (
              INSERT INTO ordering.orders(id,order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token)
              SELECT id,id::text,id,@seller,@kiosk,'PAID',100,100,id::text FROM rows RETURNING id
            )
            INSERT INTO payment.payments(kind,purpose,checkout_id,gateway,idempotency_key,amount,status,paid_at,created_at)
              SELECT 'CHARGE','ORDER_CHECKOUT',r.id,'MANUAL',r.id::text,100,'SUCCEEDED',
                '2025-01-01 17:00:00+00'::timestamptz+make_interval(days=>r.n),'2024-12-01'::timestamptz
              FROM rows r JOIN orders o ON o.id=r.id;
            """);
        seed.Parameters.AddWithValue("seller", seller); seed.Parameters.AddWithValue("kiosk", kiosk);
        await seed.ExecuteNonQueryAsync();
        using var client = factory.CreateClient();
        var path = $"/api/admin/revenue/flowers?from=2025-01-02&to=2025-05-11&sellerId={seller}&pageSize=20";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var first = await client.GetFromJsonAsync<AdminRevenuePage>(path);
        var last = await client.GetFromJsonAsync<AdminRevenuePage>(path + "&page=7");
        Assert.Equal(130, first!.TotalCount);
        Assert.Equal("13000", first.Totals.GetProperty("grossPaid").GetString());
        Assert.Equal(first.Totals.GetRawText(), last!.Totals.GetRawText());
        Assert.Equal(10, last.Items.Count);
        var boundary = await client.GetFromJsonAsync<AdminRevenuePage>($"/api/admin/revenue/flowers?from=2025-01-02&to=2025-01-02&sellerId={seller}");
        Assert.Equal("100", boundary!.Totals.GetProperty("grossPaid").GetString());
        var historical = await client.GetFromJsonAsync<List<HistoricalReportKiosk>>($"/api/admin/sellers/{seller}/report-kiosks");
        Assert.Contains(historical!, k => k.Id == kiosk);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/revenue/flowers?page=0")).StatusCode);
    }

    [Fact]
    public async Task RecoveredSlotRequiresEvidenceAndCurrentSensorsThenSupportsIdempotentSubmissionAndManagerReview()
    {
        var ids = "tech manager other seller owner kiosk slot incident task session photo operation".Split(' ').ToDictionary(k => k, _ => Guid.NewGuid());
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var seed = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id)
              VALUES(@seller,'Recovery fixture','0900000000','APPROVED','20000000-0000-0000-0000-00000000000a');
            INSERT INTO identity.users(id,email,full_name,password_hash,role,seller_id) VALUES
              (@tech,@tech::text||'@test.invalid','Tech','!test','TECHNICIAN',NULL),
              (@manager,@manager::text||'@test.invalid','Manager','!test','OPERATIONS_MANAGER',NULL),
              (@other,@other::text||'@test.invalid','Other','!test','TECHNICIAN',NULL),
              (@owner,@owner::text||'@test.invalid','Owner','!test','SELLER',@seller);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,seller_id,last_heartbeat_at)
              VALUES(@kiosk,@kiosk::text,'Recovery fixture','Test','HCM',@kiosk::text,@kiosk::text,'ONLINE',@seller,clock_timestamp());
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,status,current_seller_id,zone)
              VALUES(@slot,@kiosk,'S01',0,'LOCKED',@seller,'WALK_IN');
            INSERT INTO kiosk_ops.manager_kiosks(manager_id,kiosk_id) VALUES(@manager,@kiosk);
            INSERT INTO ordering.disputes(id,kind,kiosk_id,slot_id,reason) VALUES(@incident,'DEVICE_FAULT',@kiosk,@slot,'Inspection after recovery');
            INSERT INTO kiosk_ops.staff_tasks(id,kind,assignee_id,assigned_by,kiosk_id,incident_id,instructions,status,version,created_at)
              VALUES(@task,'INCIDENT',@tech,@manager,@kiosk,@incident,'Inspect recovered slot at kiosk','IN_PROGRESS',2,clock_timestamp()-interval '1 hour');
            INSERT INTO kiosk_ops.slot_maintenance_sessions(id,kiosk_id,slot_id,seller_id,actor_id,action,stage,intended_stage,previous_slot_status,reason,expires_at)
              VALUES(@session,@kiosk,@slot,@seller,@owner,'LOAD','NeedsInspection','AwaitLoading','RENTED_EMPTY','Expired load session',clock_timestamp()-interval '1 minute');
            INSERT INTO kiosk_ops.slot_sensor_capabilities(slot_id,kiosk_id,relay_channel,enabled,door_verified,presence_verified)
              VALUES(@slot,@kiosk,0,true,true,true);
            """);
        foreach (var (key, id) in ids) seed.Parameters.AddWithValue(key, id);
        await seed.ExecuteNonQueryAsync();
        using var tech = factory.CreateClient(); using var manager = factory.CreateClient(); using var other = factory.CreateClient();
        tech.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("TECHNICIAN", userId: ids["tech"]));
        manager.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("OPERATIONS_MANAGER", userId: ids["manager"]));
        other.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("TECHNICIAN", userId: ids["other"]));
        var path = $"/api/staff/tasks/{ids["task"]}/resolve-incident";
        var input = new StaffIncidentResolutionInput(ids["operation"], 2, "Inspected recovered slot and verified it is empty");
        async Task AssertCode(string code, StaffIncidentResolutionInput? body = null)
        {
            var response = await tech.PostAsJsonAsync(path, body ?? input);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/staff/tasks/{ids["task"]}/repair-readiness")).StatusCode);
        await AssertCode("TASK_VERSION_CONFLICT", input with { Version = 1 });
        await AssertCode("REPAIR_SESSION_RECOVERY_REQUIRED");
        seed.CommandText = """
            UPDATE kiosk_ops.slots SET status='RENTED_EMPTY' WHERE id=@slot;
            UPDATE kiosk_ops.slot_maintenance_sessions SET stage='Recovered',updated_at=clock_timestamp() WHERE id=@session;
            """;
        await seed.ExecuteNonQueryAsync();
        await AssertCode("REPAIR_EVIDENCE_REQUIRED");
        seed.CommandText = """
            INSERT INTO notify.attachments(id,owner_service,owner_type,owner_id,file_url,mime_type,size_bytes,sha256,phase,uploaded_by)
              VALUES(@photo,'kiosk_ops','staff_task',@task,'https://example.invalid/evidence.jpg','image/jpeg',1000,repeat('a',64),'EVIDENCE',@tech);
            """;
        await seed.ExecuteNonQueryAsync();
        await AssertCode("REPAIR_SENSOR_STALE");
        seed.CommandText = """
            INSERT INTO kiosk_ops.flower_presence(kiosk_id,event_id,observed_at,received_at,states)
              VALUES(@kiosk,public.uuid_v7(),(extract(epoch FROM clock_timestamp())*1000)::bigint,clock_timestamp(),ARRAY[1,0,0,0]);
            """;
        await seed.ExecuteNonQueryAsync();
        await AssertCode("REPAIR_SENSOR_MISMATCH");
        // A recently observed packet may arrive after recovery even though it was measured before it.
        seed.CommandText = """
            UPDATE kiosk_ops.flower_presence SET states=ARRAY[0,0,0,0],received_at=clock_timestamp(),
              observed_at=(SELECT (extract(epoch FROM updated_at)*1000)::bigint-1000
                FROM kiosk_ops.slot_maintenance_sessions WHERE id=@session) WHERE kiosk_id=@kiosk;
            """;
        await seed.ExecuteNonQueryAsync();
        await AssertCode("REPAIR_SENSOR_STALE");
        seed.CommandText = "UPDATE kiosk_ops.flower_presence SET states=ARRAY[0,0,0,0],received_at=clock_timestamp(),observed_at=(extract(epoch FROM clock_timestamp())*1000)::bigint WHERE kiosk_id=@kiosk";
        await seed.ExecuteNonQueryAsync();
        var result = await tech.PostAsJsonAsync(path, input); result.EnsureSuccessStatusCode();
        var version = (await result.Content.ReadFromJsonAsync<StaffIncidentResolutionResult>())!.Version;
        (await tech.PostAsJsonAsync(path, input)).EnsureSuccessStatusCode();
        // Returning a report must keep the incident open and allow a new operation, preserving old retries.
        var returned = await manager.PostAsJsonAsync($"/api/operations/staff-tasks/{ids["task"]}/transition",
            new TransitionStaffTask(version, "IN_PROGRESS", "Please clarify the physical inspection findings"));
        returned.EnsureSuccessStatusCode();
        version = (await returned.Content.ReadFromJsonAsync<StaffTaskResult>())!.Version!.Value;
        seed.CommandText = "SELECT status FROM ordering.disputes WHERE id=@incident";
        Assert.Equal("OPEN", await seed.ExecuteScalarAsync());
        (await tech.PostAsJsonAsync(path, input)).EnsureSuccessStatusCode();
        var revised = input with { Id = Guid.NewGuid(), Version = version, Report = "Rechecked physical slot and clarified inspection findings" };
        var resubmitted = await tech.PostAsJsonAsync(path, revised);
        resubmitted.EnsureSuccessStatusCode();
        version = (await resubmitted.Content.ReadFromJsonAsync<StaffIncidentResolutionResult>())!.Version;
        (await tech.PostAsJsonAsync(path, revised)).EnsureSuccessStatusCode();
        (await manager.PostAsJsonAsync($"/api/operations/staff-tasks/{ids["task"]}/transition", new TransitionStaffTask(version, "COMPLETED", "Reviewed recovered slot evidence and verified actual state"))).EnsureSuccessStatusCode();
        seed.CommandText = "SELECT status FROM ordering.disputes WHERE id=@incident";
        Assert.Equal("RESOLVED_FIXED", await seed.ExecuteScalarAsync());
    }
}
