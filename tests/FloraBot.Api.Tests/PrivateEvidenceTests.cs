using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class PrivateEvidenceTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jN1sAAAAASUVORK5CYII=");

    [Fact]
    public async Task StaffEvidenceIsPrivateVersionBoundAndRetrySafe()
    {
        using var handler = new Storage(); using var transport = new HttpClient(handler);
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CLOUDINARY_CLOUD_NAME"] = "test-cloud", ["CLOUDINARY_API_KEY"] = "test", ["CLOUDINARY_API_SECRET"] = "test-secret" }).Build();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped(_ => new CloudinaryMedia(transport, settings, TimeProvider.System))));
        using var scope = app.Services.CreateScope(); var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var staffId = Guid.NewGuid(); var otherId = Guid.NewGuid(); var taskId = Guid.NewGuid(); var incident = Guid.NewGuid();
        var kiosk = Guid.Parse("40000000-0000-0000-0000-000000000001");
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.users(id,email,full_name,password_hash,role) VALUES
              (@staff,@staff::text||'@example.invalid','Evidence staff','!unprovisioned','STAFF'),
              (@other,@other::text||'@example.invalid','Other evidence staff','!unprovisioned','STAFF');
            INSERT INTO ordering.disputes(id,kind,kiosk_id,reason) VALUES(@incident,'DEVICE_FAULT',@kiosk,'Evidence door test');
            """);
        setup.Parameters.AddWithValue("staff", staffId); setup.Parameters.AddWithValue("other", otherId);
        setup.Parameters.AddWithValue("incident", incident); setup.Parameters.AddWithValue("kiosk", kiosk); await setup.ExecuteNonQueryAsync();
        using var staff = app.CreateClient(); using var other = app.CreateClient(); using var admin = app.CreateClient();
        staff.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("STAFF", userId: staffId));
        other.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("STAFF", userId: otherId));
        admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        (await admin.PostAsJsonAsync("/api/admin/staff-tasks", new AssignStaffTask(taskId, staffId, "INCIDENT", kiosk, null, incident, "Inspect the door with private evidence"))).EnsureSuccessStatusCode();
        var path = $"/api/staff/tasks/{taskId}/evidence";
        HttpContent Content() { var body = new ByteArrayContent(Png); body.Headers.ContentType = new("image/png"); return body; }
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsync(path + "/upload", Content())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync(path + "/upload", Content())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync(path + "/upload", Content())).StatusCode);
        (await staff.PostAsJsonAsync($"/api/staff/tasks/{taskId}/transition", new TransitionStaffTask(1, "IN_PROGRESS", "Started inspecting door and collecting photos"))).EnsureSuccessStatusCode();
        var upload = await staff.PostAsync(path + "/upload", Content()); upload.EnsureSuccessStatusCode();
        var ticket = (await upload.Content.ReadFromJsonAsync<EvidenceUploadResponse>())!;
        var input = new StaffEvidenceInput(Guid.NewGuid(), 2, ticket.Reference);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(path, input with { Reference = "https://example.invalid/photo.png" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(path, input with { Version = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsJsonAsync(path, input)).StatusCode);
        var responses = await Task.WhenAll(staff.PostAsJsonAsync(path, input), staff.PostAsJsonAsync(path, input));
        foreach (var response in responses) response.EnsureSuccessStatusCode();
        var saved = (await responses[0].Content.ReadFromJsonAsync<StaffEvidenceItem>())!;
        Assert.Equal(saved, await responses[1].Content.ReadFromJsonAsync<StaffEvidenceItem>());
        Assert.NotEqual(DateTime.MinValue, saved.CreatedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(path, input with { Id = Guid.NewGuid() })).StatusCode);
        var list = (await staff.GetFromJsonAsync<StaffEvidencePage>(path))!;
        Assert.Equal(saved, Assert.Single(list.Items)); Assert.False(list.HasMore);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.GetAsync(path + "?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(path)).StatusCode);
        var read = await staff.GetAsync(path + "/" + saved.Id); read.EnsureSuccessStatusCode();
        Assert.True(read.Headers.CacheControl?.NoStore); Assert.Equal(Png, await read.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(path + "/" + saved.Id)).StatusCode);
        var adminPath = $"/api/admin/staff-tasks/{taskId}/evidence";
        Assert.Equal(Png, await admin.GetByteArrayAsync(adminPath + "/" + saved.Id));
        handler.Corrupt = true; Assert.Equal(HttpStatusCode.ServiceUnavailable, (await staff.GetAsync(path + "/" + saved.Id)).StatusCode); handler.Corrupt = false;
        var detail = (await staff.GetFromJsonAsync<StaffTaskDetail>($"/api/staff/tasks/{taskId}"))!;
        Assert.Equal("STAFF_TASK_EVIDENCE_ADDED", detail.History[0].Action);
        var secondTask = Guid.NewGuid(); var secondIncident = Guid.NewGuid();
        await using var secondSetup = data.CreateCommand("INSERT INTO ordering.disputes(id,kind,kiosk_id,reason) VALUES(@id,'DEVICE_FAULT',@kiosk,'Second task evidence test')");
        secondSetup.Parameters.AddWithValue("id", secondIncident); secondSetup.Parameters.AddWithValue("kiosk", kiosk); await secondSetup.ExecuteNonQueryAsync();
        (await admin.PostAsJsonAsync("/api/admin/staff-tasks", new AssignStaffTask(secondTask, staffId, "INCIDENT", kiosk, null, secondIncident, "Inspect another door with private evidence"))).EnsureSuccessStatusCode();
        (await staff.PostAsJsonAsync($"/api/staff/tasks/{secondTask}/transition", new TransitionStaffTask(1, "IN_PROGRESS", "Started inspecting the second door"))).EnsureSuccessStatusCode();
        var secondPath = $"/api/staff/tasks/{secondTask}/evidence";
        var anotherUpload = await staff.PostAsync(path + "/upload", Content()); anotherUpload.EnsureSuccessStatusCode();
        var secondUpload = await staff.PostAsync(secondPath + "/upload", Content()); secondUpload.EnsureSuccessStatusCode();
        var firstReference = (await anotherUpload.Content.ReadFromJsonAsync<EvidenceUploadResponse>())!.Reference;
        var secondReference = (await secondUpload.Content.ReadFromJsonAsync<EvidenceUploadResponse>())!.Reference;
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(secondPath, input)).StatusCode);
        var collisionId = Guid.NewGuid();
        var collisions = await Task.WhenAll(staff.PostAsJsonAsync(path, new StaffEvidenceInput(collisionId, 2, firstReference)), staff.PostAsJsonAsync(secondPath, new StaffEvidenceInput(collisionId, 2, secondReference)));
        Assert.Single(collisions, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(collisions, x => x.StatusCode == HttpStatusCode.Conflict);
        // Reusing an existing ID with a different upload cannot be mistaken for a retry.
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(path, input with { Reference = firstReference })).StatusCode);
        (await staff.PostAsJsonAsync($"/api/staff/tasks/{taskId}/transition", new TransitionStaffTask(2, "SUBMITTED", "Photos attached for administrator review"))).EnsureSuccessStatusCode();
        (await staff.PostAsJsonAsync(path, input)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(path, new StaffEvidenceInput(Guid.NewGuid(), 2, firstReference))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsync(path + "/upload", Content())).StatusCode);
        (await admin.PostAsJsonAsync($"/api/admin/staff-tasks/{taskId}/transition", new TransitionStaffTask(3, "IN_PROGRESS", "Collect additional views of the repaired door"))).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/admin/staff-tasks/{taskId}/reassign", new ReassignStaffTask(4, otherId, "Next shift will collect additional evidence"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync(path + "/" + saved.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync(path, input)).StatusCode);
        Assert.Contains(saved, (await other.GetFromJsonAsync<StaffEvidencePage>(path))!.Items);
        Assert.Equal(Png, await other.GetByteArrayAsync(path + "/" + saved.Id));
        await using var seed = data.CreateCommand("""
            INSERT INTO notify.attachments(owner_service,owner_type,owner_id,file_url,mime_type,size_bytes,sha256,phase,uploaded_by)
            SELECT 'kiosk_ops','staff_task',@task,'https://example.invalid/page-'||n,'image/png',1,'fixture','EVIDENCE',@actor
            FROM generate_series(1,26) n
            """);
        seed.Parameters.AddWithValue("task", taskId); seed.Parameters.AddWithValue("actor", otherId); await seed.ExecuteNonQueryAsync();
        var firstPage = (await other.GetFromJsonAsync<StaffEvidencePage>(path))!;
        var secondPage = (await other.GetFromJsonAsync<StaffEvidencePage>(path + "?page=2"))!;
        Assert.Equal(25, firstPage.Items.Count); Assert.True(firstPage.HasMore); Assert.False(secondPage.HasMore);
        Assert.Empty(firstPage.Items.Select(x => x.Id).Intersect(secondPage.Items.Select(x => x.Id)));
        Assert.Equal(saved, Assert.Single((await other.GetFromJsonAsync<StaffEvidencePage>(path + "?attachmentId=" + saved.Id))!.Items));
        Assert.Empty((await other.GetFromJsonAsync<StaffEvidencePage>(path + "?attachmentId=" + Guid.NewGuid()))!.Items);
    }

    [Theory]
    [InlineData("pay_withdrawal")]
    [InlineData("confirm_refund")]
    public async Task FinancialProofRequiresPrivateBoundUploadAndStoresActualBytes(string purpose)
    {
        using var handler = new Storage(); using var transport = new HttpClient(handler);
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CLOUDINARY_CLOUD_NAME"] = "test-cloud", ["CLOUDINARY_API_KEY"] = "test", ["CLOUDINARY_API_SECRET"] = "test-secret" }).Build();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped(_ => new CloudinaryMedia(transport, settings, TimeProvider.System))));
        using var scope = app.Services.CreateScope(); var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var (seller, owner, approver, payer) = await PayoutApprovalTests.Setup(source);
        var withdrawal = purpose == "pay_withdrawal";
        Guid id;
        if (withdrawal)
        {
            await using var create = source.CreateCommand("SELECT flow.request_withdrawal(@seller,@owner,100000)");
            create.Parameters.AddWithValue("seller", seller); create.Parameters.AddWithValue("owner", owner);
            id = (Guid)(await create.ExecuteScalarAsync())!;
        }
        else
        {
            var order = Guid.NewGuid();
            await using var create = source.CreateCommand("""
                INSERT INTO ordering.orders(id,order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token,completed_at,refund_bank_name,refund_bank_account_enc,refund_bank_holder)
                VALUES(@id,@id::text,@id,@seller,'40000000-0000-0000-0000-000000000001','COMPLETED',350000,350000,flow.new_tracking_token(),public.app_now(),'Test bank','test-encrypted','Test customer');
                INSERT INTO payment.payments(kind,purpose,checkout_id,gateway,idempotency_key,amount,status,paid_at)
                VALUES('CHARGE','ORDER_CHECKOUT',@id,'MANUAL',@id::text,350000,'SUCCEEDED',public.app_now());
                SELECT flow.create_refund(@id,100000,'Private proof test',flow.sys_user());
                """);
            create.Parameters.AddWithValue("id", order); create.Parameters.AddWithValue("seller", seller);
            id = (Guid)(await create.ExecuteScalarAsync())!;
        }
        using var client = app.CreateClient();
        HttpContent Content() { var body = new ByteArrayContent(Png); body.Headers.ContentType = new("image/png"); return body; }
        var upload = $"/api/admin/evidence/{purpose}/{id}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(upload, Content())).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(upload, Content())).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN", userId: payer));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(upload.Replace(id.ToString(), Guid.NewGuid().ToString()), Content())).StatusCode);
        Assert.Equal(0, handler.Uploads);
        handler.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync(upload, Content())).StatusCode);
        handler.Fail = false;
        var uploaded = await client.PostAsync(upload, Content()); uploaded.EnsureSuccessStatusCode();
        var reference = (await uploaded.Content.ReadFromJsonAsync<EvidenceUploadResponse>())!.Reference;
        var tickets = scope.ServiceProvider.GetRequiredService<PrivateEvidence>();
        Assert.Throws<ArgumentException>(() => tickets.Resolve(reference, purpose, PrivateEvidence.AdminBinding(approver.ToString(), id)));
        Assert.Throws<ArgumentException>(() => tickets.Resolve(reference, purpose, PrivateEvidence.AdminBinding(payer.ToString(), Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => tickets.Resolve(reference, withdrawal ? "confirm_refund" : "pay_withdrawal", PrivateEvidence.AdminBinding(payer.ToString(), id)));
        var key = withdrawal ? "p_w" : "p_refund";
        async Task<HttpResponseMessage> Pay(string proof) => await client.PostAsJsonAsync($"/api/admin/flows/{purpose}", new Dictionary<string, object> { [key] = id, ["p_proof_url"] = proof });
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay("https://example.invalid/unverified.png")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN", userId: approver));
        (await client.PostAsJsonAsync($"/api/admin/flows/{(withdrawal ? "approve_withdrawal" : "approve_refund")}", new Dictionary<string, object> { [key] = id })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(reference)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN", userId: payer));
        (await Pay(reference)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Pay(reference)).StatusCode);
        await using var metadata = source.CreateCommand("SELECT size_bytes,sha256 FROM notify.attachments WHERE owner_id=@id AND phase='PROOF'");
        metadata.Parameters.AddWithValue("id", id);
        await using (var reader = await metadata.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync()); Assert.Equal(Png.Length, reader.GetInt64(0));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Png)), reader.GetString(1)); Assert.False(await reader.ReadAsync());
        }
        var listResponse = await client.GetAsync(upload); listResponse.EnsureSuccessStatusCode(); Assert.True(listResponse.Headers.CacheControl?.NoStore);
        var item = Assert.Single((await listResponse.Content.ReadFromJsonAsync<IncidentEvidenceResponse>())!.Items);
        Assert.StartsWith("/api/admin/evidence/", item.Url);
        var read = await client.GetAsync(item.Url); read.EnsureSuccessStatusCode(); Assert.True(read.Headers.CacheControl?.NoStore);
        Assert.Equal(Png, await read.Content.ReadAsByteArrayAsync());
        handler.Corrupt = true; Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(item.Url)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(upload)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(item.Url)).StatusCode);
    }

    [Fact]
    public async Task ReceiptUploadIsPrivateBoundSingleUseAndAdminOnly()
    {
        using var handler = new Storage(); using var transport = new HttpClient(handler);
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CLOUDINARY_CLOUD_NAME"] = "test-cloud", ["CLOUDINARY_API_KEY"] = "test", ["CLOUDINARY_API_SECRET"] = "test-secret" }).Build();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped(_ => new CloudinaryMedia(transport, settings, TimeProvider.System))));
        using var scope = app.Services.CreateScope(); var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var order = Guid.NewGuid();
        await using var setup = source.CreateCommand("""
            INSERT INTO ordering.orders(id,order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token,completed_at)
            VALUES(@id,'EVD-'||@id,@id,@id,@id,'COMPLETED',350000,350000,flow.new_tracking_token(),public.app_now()) RETURNING tracking_token;
            """);
        setup.Parameters.AddWithValue("id", order); var token = (string)(await setup.ExecuteScalarAsync())!;
        using var client = app.CreateClient();
        HttpContent Content(byte[]? bytes = null) { var body = new ByteArrayContent(bytes ?? Png); body.Headers.ContentType = new("image/png"); return body; }
        var upload = $"/api/receipts/{order}/evidence";
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(upload, Content())).StatusCode);
        Assert.Equal(0, handler.Uploads);
        client.DefaultRequestHeaders.Add("X-Receipt-Token", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(upload, Content("bad"u8.ToArray()))).StatusCode);
        Assert.Equal(0, handler.Uploads);
        var response = await client.PostAsync(upload, Content());
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var ticket = (await response.Content.ReadFromJsonAsync<EvidenceUploadResponse>())!;
        Assert.StartsWith("evidence:v1:", ticket.Reference); Assert.DoesNotContain("cloudinary", ticket.Reference);
        var tickets = scope.ServiceProvider.GetRequiredService<PrivateEvidence>();
        var binding = PrivateEvidence.ReceiptBinding(order, token);
        Assert.Throws<ArgumentException>(() => tickets.Resolve(ticket.Reference, "return_to_seller", binding));
        Assert.Throws<ArgumentException>(() => tickets.Resolve(ticket.Reference, "open_dispute", binding + "wrong"));
        Assert.Throws<ArgumentException>(() => tickets.Resolve(ticket.Reference + "tampered", "open_dispute", binding));
        var payload = new { p_order = order, p_tracking = token, p_reason = "Hoa bị dập khi nhận.", p_photo = ticket.Reference };
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync("/api/receipts/flows/open_dispute", payload)));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        await using var lookup = source.CreateCommand("SELECT a.id,a.file_url,a.sha256,a.size_bytes FROM notify.attachments a JOIN ordering.disputes d ON d.id=a.owner_id WHERE d.order_id=@id");
        lookup.Parameters.AddWithValue("id", order);
        await using var reader = await lookup.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        var attachment = reader.GetGuid(0);
        Assert.Contains("/image/authenticated/", reader.GetString(1));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Png)), reader.GetString(2)); Assert.Equal(Png.Length, reader.GetInt64(3));
        Assert.False(await reader.ReadAsync()); await reader.DisposeAsync();
        var read = $"/api/admin/evidence/{attachment}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(read)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(read)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var image = await client.GetAsync(read); Assert.True(image.IsSuccessStatusCode); Assert.True(image.Headers.CacheControl?.NoStore);
        Assert.Equal(Png, await image.Content.ReadAsByteArrayAsync());
        var kiosk = Guid.NewGuid(); var slot = Guid.NewGuid();
        var seller = Guid.Parse("20000000-0000-0000-0000-000000000001");
        await using var slotSetup = source.CreateCommand("""
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,api_key_hash)
            VALUES(@id,@id::text,'Evidence fixture','Fixture','HCM',@id::text,@id::text,'ONLINE','fixture');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,current_seller_id) VALUES(@slot,@id,'A01',0,@seller);
            """);
        slotSetup.Parameters.AddWithValue("id", kiosk); slotSetup.Parameters.AddWithValue("slot", slot); slotSetup.Parameters.AddWithValue("seller", seller);
        await slotSetup.ExecuteNonQueryAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        foreach (var purpose in new[] { "report_device_fault", "return_to_seller" })
        {
            var sellerUpload = $"/api/sellers/{seller}/slots/{slot}/evidence/{purpose}";
            var before = handler.Uploads;
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(sellerUpload.Replace(slot.ToString(), Guid.NewGuid().ToString()), Content())).StatusCode);
            Assert.Equal(before, handler.Uploads);
            var sellerResponse = await client.PostAsync(sellerUpload, Content());
            Assert.True(sellerResponse.IsSuccessStatusCode, await sellerResponse.Content.ReadAsStringAsync());
            var sellerTicket = (await sellerResponse.Content.ReadFromJsonAsync<EvidenceUploadResponse>())!;
            Assert.Equal(Png.Length, tickets.Resolve(sellerTicket.Reference, purpose, PrivateEvidence.SellerBinding("10000000-0000-0000-0000-000000000003", seller, slot)).SizeBytes);
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        handler.Corrupt = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(read)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync(upload, Content())).StatusCode);
        handler.Corrupt = false; handler.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync(upload, Content())).StatusCode);
    }

    [Fact]
    public void ExpiredAndForeignReferencesAreRejected()
    {
        var clock = new Clock(); var protection = new EphemeralDataProtectionProvider();
        var tickets = new PrivateEvidence(protection, clock);
        var media = new VerifiedMedia("https://example.invalid/test", "test", "image/png", 1, "hash");
        var issued = tickets.Issue("report_device_fault", "seller:one", media);
        Assert.Equal(media, tickets.Resolve(issued.Reference, "report_device_fault", "seller:one"));
        Assert.Throws<ArgumentException>(() => tickets.Resolve(issued.Reference, "report_device_fault", "seller:two"));
        Assert.Throws<ArgumentException>(() => tickets.Resolve("evidence:v1:%%%", "report_device_fault", "seller:one"));
        clock.Now = clock.Now.AddMinutes(31);
        Assert.Throws<ArgumentException>(() => tickets.Resolve(issued.Reference, "report_device_fault", "seller:one"));
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Storage : HttpMessageHandler
    {
        public int Uploads { get; private set; }
        public bool Corrupt { get; set; }
        public bool Fail { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fail) return new(HttpStatusCode.ServiceUnavailable);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("api.cloudinary.com", request.RequestUri!.Host);
                Assert.EndsWith("/image/download", request.RequestUri.AbsolutePath);
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                Assert.Equal("authenticated", query["type"]);
                var canonical = string.Join("&", query.Where(x => x.Key is not ("api_key" or "signature")).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical + "test-secret"))), query["signature"].ToString());
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Corrupt ? "corrupt"u8.ToArray() : Png) };
            }
            Uploads++;
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            async Task<string> Value(string name) => await form.Single(x => x.Headers.ContentDisposition!.Name!.Trim('"') == name).ReadAsStringAsync(ct);
            Assert.Equal("authenticated", await Value("type"));
            var id = await Value("public_id"); Assert.StartsWith("florabot-evidence/", id);
            var timestamp = await Value("timestamp");
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"overwrite=false&public_id={id}&timestamp={timestamp}&type=authenticatedtest-secret"))), await Value("signature"));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { public_id = id, secure_url = $"https://res.cloudinary.com/test-cloud/image/authenticated/v1/{id}.png" }) };
        }
    }
}
