using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Microsoft.Extensions.Configuration;

namespace FloraBot.Api.Tests;

public sealed class QualityComplaintTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record Fixture(Guid Seller, Guid Actor, Guid Staff, Guid OtherStaff, Guid Kiosk,
        Guid Slot, Guid Product, Guid Order, Guid Item, Guid Complaint, string Tracking, Guid? Customer);
    private NpgsqlDataSource Data => factory.Services.GetRequiredService<NpgsqlDataSource>();
    private async Task<object?> Sql(string sql, params (string Key, object Value)[] values)
    {
        await using var command = Data.CreateCommand(sql);
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value);
        return await command.ExecuteScalarAsync();
    }
    private HttpClient Client(string role, Guid user, Guid seller)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role, userId: user, sellerId: seller));
        return client;
    }
    private async Task<HttpClient> Device(Fixture f)
    {
        var key = "quality-test-" + f.Kiosk;
        await Sql("UPDATE kiosk_ops.kiosks SET api_key_hash=@hash WHERE id=@id",
            ("hash", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))), ("id", f.Kiosk));
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Kiosk-Key", key); return client;
    }
    private static async Task Success(HttpResponseMessage response) =>
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private async Task<Fixture> Setup(bool member = false)
    {
        var seller = Guid.NewGuid(); var actor = Guid.NewGuid(); var staff = Guid.NewGuid(); var other = Guid.NewGuid();
        var kiosk = Guid.NewGuid(); var slot = Guid.NewGuid(); var product = Guid.NewGuid(); var bouquet = Guid.NewGuid();
        var order = Guid.NewGuid(); var item = Guid.NewGuid(); var customer = Guid.NewGuid();
        await Sql($"""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Quality shop','0901234567','ACTIVE','20000000-0000-0000-0000-00000000000d',CURRENT_DATE+365);
            INSERT INTO identity.users(id,email,full_name,password_hash,role,seller_id,status) VALUES
              (@actor,@actor::text||'@example.invalid','Quality Seller','!unprovisioned','SELLER',@seller,'ACTIVE'),
              (@staff,@staff::text||'@example.invalid','Quality Staff','!unprovisioned','SELLER_STAFF',@seller,'ACTIVE'),
              (@other,@other::text||'@example.invalid','Other Staff','!unprovisioned','SELLER_STAFF',@seller,'ACTIVE');
            INSERT INTO identity.users(id,email,full_name,password_hash,role,status,loyalty_points)
            SELECT @customer,@customer::text||'@example.invalid','Quality customer','!unprovisioned','CUSTOMER','ACTIVE',100 WHERE @member;
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,last_heartbeat_at)
            VALUES(@kiosk,@kiosk::text,'Quality kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE',clock_timestamp());
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,zone) VALUES(@slot,@kiosk,'W01',0,'WALK_IN');
            {CurrentMainKioskFixture.AssignKioskWithLeaseSql}
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status,quantity)
            VALUES(@product,@seller,'Quality roses',10000,48,'ACTIVE',2);
            INSERT INTO kiosk_ops.bouquets(id,product_id,seller_id,qr_code,price_snapshot,status,stocked_at,sellable_until)
            VALUES(@bouquet,@product,@seller,@bouquet::text,10000,'PICKED_UP',public.app_now()-interval '1 hour',public.app_now()+interval '47 hours');
            INSERT INTO ordering.orders(id,order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token,completed_at,customer_id,points_earned,loyalty_policy)
            VALUES(@order,@order::text,public.uuid_v7(),@seller,@kiosk,'COMPLETED',10000,10000,flow.new_tracking_token(),public.app_now(),
              CASE WHEN @member THEN @customer END,CASE WHEN @member THEN 100 ELSE 0 END,CASE WHEN @member THEN 'LEGACY_GLOBAL' ELSE 'SHOP_POLICY' END);
            INSERT INTO ordering.order_items(id,order_id,item_type,bouquet_id,slot_id,name_snapshot,quantity,unit_price,line_total)
            VALUES(@item,@order,'BOUQUET',@bouquet,@slot,'Quality roses',1,10000,10000);
            INSERT INTO kiosk_ops.slot_sensor_capabilities(slot_id,kiosk_id,relay_channel,enabled,door_verified,presence_verified)
            VALUES(@slot,@kiosk,0,true,true,true);
            """, ("seller", seller), ("actor", actor), ("staff", staff), ("other", other), ("kiosk", kiosk),
            ("slot", slot), ("product", product), ("bouquet", bouquet), ("order", order), ("item", item), ("member", member), ("customer", customer));
        var tracking = (string)(await Sql("SELECT tracking_token FROM ordering.orders WHERE id=@order", ("order", order)))!;
        var photo = EvidenceFixture.Reference(factory.Services, "open_dispute", PrivateEvidence.ReceiptBinding(order, tracking));
        using var receipt = factory.CreateClient();
        var opened = await receipt.PostAsJsonAsync("/api/receipts/flows/open_dispute", new
        { p_order = order, p_tracking = tracking, p_reason = "Flowers were damaged after collection", p_photo = photo });
        await Success(opened);
        var complaint = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        return new(seller, actor, staff, other, kiosk, slot, product, order, item, complaint, tracking, member ? customer : null);
    }
    private async Task<QualityComplaintDetail> Read(HttpClient seller, Fixture f) =>
        (await seller.GetFromJsonAsync<QualityComplaintDetail>($"/api/me/complaints/{f.Complaint}"))!;
    private async Task<Guid> Assign(HttpClient seller, Fixture f)
    {
        var path = $"/api/me/complaints/{f.Complaint}";
        var task = Guid.NewGuid();
        await Success(await seller.PostAsJsonAsync(path + "/accept", new QualityVersion(1)));
        await Success(await seller.PostAsJsonAsync(path + "/assignment", new AssignQualityTask(2, task, f.Staff, f.Item, "Check and handle the customer's flower quality complaint")));
        return task;
    }
    private async Task Presence(Fixture f, int value) => await Sql("""
        UPDATE kiosk_ops.kiosks SET status='ONLINE',last_heartbeat_at=clock_timestamp() WHERE id=@kiosk;
        INSERT INTO kiosk_ops.flower_presence(kiosk_id,event_id,observed_at,states,received_at)
        VALUES(@kiosk,gen_random_uuid(),(extract(epoch FROM clock_timestamp())*1000)::bigint+1,ARRAY[@value,-1,-1,-1],clock_timestamp())
        ON CONFLICT(kiosk_id) DO UPDATE SET states=excluded.states,observed_at=excluded.observed_at,received_at=excluded.received_at;
        """, ("kiosk", f.Kiosk), ("value", value));

    [Fact]
    public async Task SellerAcceptsAssignsReturnsAndExplainsAfterStaffReportWithoutMandatoryPhoto()
    {
        var f = await Setup(); using var seller = Client("SELLER", f.Actor, f.Seller);
        using var staff = Client("SELLER_STAFF", f.Staff, f.Seller);
        using var other = Client("SELLER_STAFF", f.OtherStaff, f.Seller);
        using var outsider = factory.CreateClient(); outsider.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        var path = $"/api/me/complaints/{f.Complaint}";
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync(path + "/accept", new QualityVersion(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync(path + "/replacement/cancel", new CancelQualityReplacement(1, "There is no replacement plan to cancel yet"))).StatusCode);
        var task = await Assign(seller, f);
        var duplicate = new AssignQualityTask(2, task, f.Staff, f.Item, "Check and handle the customer's flower quality complaint");
        await Success(await seller.PostAsJsonAsync(path + "/assignment", duplicate));
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync(path + "/assignment", duplicate with { TaskId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/staff/tasks/{task}/quality")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/staff/tasks/{task}/stock")).StatusCode);
        var taskList = (await staff.GetFromJsonAsync<StaffTaskPage>("/api/staff/tasks"))!;
        Assert.Equal("FLOWER_QUALITY", Assert.Single(taskList.Items, t => t.Id == task).WorkType);
        var transition = $"/api/staff/tasks/{task}/transition";
        await Success(await staff.PostAsJsonAsync(transition, new TransitionStaffTask(1, "IN_PROGRESS", "Started checking the reported flowers")));
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(transition, new TransitionStaffTask(2, "SUBMITTED", null))).StatusCode);
        await Success(await staff.PostAsJsonAsync(transition, new TransitionStaffTask(2, "SUBMITTED", "Inspected the flowers and documented findings")));
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/sellers/{f.Seller}/staff-tasks/{task}/transition",
            new TransitionStaffTask(3, "COMPLETED", "Attempt to bypass customer resolution"))).StatusCode);
        var detail = await Read(seller, f);
        Assert.Equal("REVIEW_REQUIRED", detail.HandlingState);
        await Success(await seller.PostAsJsonAsync(path + "/review", new ReviewQualityComplaint(detail.Version, 3, "RETURN", "Please recheck the customer's reported problem")));
        await Success(await staff.PostAsJsonAsync(transition, new TransitionStaffTask(4, "SUBMITTED", "Rechecked and explained the flower condition")));
        detail = await Read(seller, f);
        await Success(await seller.PostAsJsonAsync(path + "/review", new ReviewQualityComplaint(detail.Version, 5, "EXPLANATION", "The shop has explained the findings to the customer")));
        detail = await Read(seller, f);
        Assert.Equal("RESOLVED_FIXED", detail.Status); Assert.Equal("RESOLVED", detail.HandlingState);
        Assert.Equal("COMPLETED", detail.Task!.Status); Assert.Contains("Rechecked", detail.Task.Report);
        using var receipt = factory.CreateClient();
        var result = await receipt.PostAsJsonAsync("/api/receipts/complaints/lookup", new ReceiptLookupRequest(f.Order, f.Tracking));
        await Success(result); var customer = (await result.Content.ReadFromJsonAsync<QualityComplaintDetail>())!;
        Assert.Null(customer.Task); Assert.Equal("EXPLANATION", customer.Outcome);
        Assert.DoesNotContain("tracking", await result.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("COMPLETED", await Sql("SELECT status FROM ordering.orders WHERE id=@id", ("id", f.Order)));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM payment.payments WHERE order_id=@id AND kind='REFUND'", ("id", f.Order)));
        Assert.Equal(HttpStatusCode.NotFound, (await receipt.PostAsJsonAsync("/api/receipts/complaints/lookup", new ReceiptLookupRequest(f.Order, "AAAAAAAA"))).StatusCode);
    }

    [Fact]
    public async Task RefundOutcomeRefersToAdminWithoutCreatingOrPayingARefund()
    {
        var f = await Setup(); using var seller = Client("SELLER", f.Actor, f.Seller); using var staff = Client("SELLER_STAFF", f.Staff, f.Seller);
        var task = await Assign(seller, f);
        var transition = $"/api/staff/tasks/{task}/transition";
        await Success(await staff.PostAsJsonAsync(transition, new TransitionStaffTask(1, "IN_PROGRESS", "Checking the customer complaint")));
        await Success(await staff.PostAsJsonAsync(transition, new TransitionStaffTask(2, "SUBMITTED", "Recommend reviewing the customer's refund request")));
        var detail = await Read(seller, f);
        await Success(await seller.PostAsJsonAsync($"/api/me/complaints/{f.Complaint}/review",
            new ReviewQualityComplaint(detail.Version, 3, "REFUND_REVIEW", "Please review refund eligibility using the existing refund process")));
        detail = await Read(seller, f); Assert.Equal("OPEN", detail.Status); Assert.Equal("REFUND_REVIEW", detail.HandlingState);
        Assert.Equal("COMPLETED", detail.Task!.Status);
        using var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var referrals = (await admin.GetFromJsonAsync<QualityComplaintPage>("/api/admin/quality-complaints?state=REFUND_REVIEW"))!;
        Assert.Contains(referrals.Items, row => row.Id == f.Complaint);
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.GetAsync("/api/admin/quality-complaints?state=REFUND_REVIEW")).StatusCode);
        Assert.Equal("DISPUTED", await Sql("SELECT status FROM ordering.orders WHERE id=@id", ("id", f.Order)));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM payment.payments WHERE order_id=@id AND kind='REFUND'", ("id", f.Order)));
    }

    [Theory]
    [InlineData("held")]
    [InlineData("sensor")]
    [InlineData("owner")]
    public async Task PreorderReplacementPlanRejectsUnavailableSlots(string blocker)
    {
        var f = await Setup(); using var seller = Client("SELLER", f.Actor, f.Seller);
        await Assign(seller, f);
        await Sql("UPDATE kiosk_ops.slots SET zone='PREORDER' WHERE id=@slot", ("slot", f.Slot));
        if (blocker == "held")
            await Sql("UPDATE kiosk_ops.slots SET hold_order_id=@order,hold_until=public.app_now()+interval '1 hour' WHERE id=@slot", ("order", f.Order), ("slot", f.Slot));
        if (blocker == "sensor")
            await Sql("UPDATE kiosk_ops.slot_sensor_capabilities SET enabled=false WHERE slot_id=@slot", ("slot", f.Slot));
        if (blocker == "owner")
            await Sql("UPDATE kiosk_ops.slots SET current_seller_id=NULL WHERE id=@slot", ("slot", f.Slot));
        var detail = await Read(seller, f);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/me/complaints/{f.Complaint}/replacement-plan",
            new QualityReplacementPlan(detail.Version, 1, f.Product, f.Slot))).StatusCode);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM kiosk_ops.quality_replacements WHERE complaint_id=@id", ("id", f.Complaint)));
    }

    private async Task<(Guid Task, QualityComplaintDetail Detail)> ReadyReplacement(Fixture f, HttpClient seller, HttpClient staff, HttpClient device)
    {
        var task = await Assign(seller, f);
        var detail = await Read(seller, f);
        await Success(await seller.PostAsJsonAsync($"/api/me/complaints/{f.Complaint}/replacement-plan",
            new QualityReplacementPlan(detail.Version, 1, f.Product, f.Slot)));
        await Success(await staff.PostAsJsonAsync($"/api/staff/tasks/{task}/transition", new TransitionStaffTask(1, "IN_PROGRESS", "Preparing the replacement bouquet for collection")));
        await Presence(f, 0);
        var access = await staff.PostAsJsonAsync($"/api/staff/tasks/{task}/inventory-maintenance/access", new { operation = "START" });
        await Success(access); var issued = (await access.Content.ReadFromJsonAsync<MaintenanceAccessIssued>())!;
        var opened = await device.PostAsJsonAsync($"/api/kiosks/{f.Kiosk}/maintenance-access/{issued.Id}/confirm", new { code = issued.Code });
        await Success(opened);
        var cmd = (Guid)(await Sql("SELECT current_cmd_id FROM kiosk_ops.slot_maintenance_sessions WHERE id=@id", ("id", task)))!;
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/staff/tasks/{task}/transition", new TransitionStaffTask(2, "SUBMITTED", "Attempted submission before loading completion"))).StatusCode);
        await Sql("SELECT flow.device_event(@cmd,'SENT'); SELECT flow.device_event(@cmd,'ACK'); SELECT flow.device_event(@cmd,'OPENED'); SELECT flow.device_event(@cmd,'CLOSED');", ("cmd", cmd));
        await Presence(f, 1);
        var sync = await seller.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{task}/check", new { });
        await Success(sync);
        var session = (await sync.Content.ReadFromJsonAsync<SlotMaintenanceSessionResponse>())!;
        Assert.Equal("Completed", session.Stage);
        Assert.Equal("HELD", await Sql("SELECT status FROM kiosk_ops.slots WHERE id=@id", ("id", f.Slot)));
        Assert.Equal(1, await Sql("SELECT quantity FROM catalog.flower_products WHERE id=@id", ("id", f.Product)));
        await Success(await seller.PostAsJsonAsync($"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/maintenance-sessions/{task}/check", new { }));
        Assert.Equal(1, await Sql("SELECT quantity FROM catalog.flower_products WHERE id=@id", ("id", f.Product)));
        await Success(await staff.PostAsJsonAsync($"/api/staff/tasks/{task}/transition", new TransitionStaffTask(2, "SUBMITTED", "Loaded and checked the replacement bouquet at the kiosk")));
        detail = await Read(seller, f);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/me/complaints/{f.Complaint}/review",
            new ReviewQualityComplaint(detail.Version, 3, "REFUND_REVIEW", "Attempting refund while replacement is still reserved"))).StatusCode);
        await Success(await seller.PostAsJsonAsync($"/api/me/complaints/{f.Complaint}/review",
            new ReviewQualityComplaint(detail.Version, 3, "REPLACEMENT", "Please collect your free replacement bouquet at the original kiosk")));
        return (task, await Read(seller, f));
    }

    [Theory]
    [InlineData("WALK_IN")]
    [InlineData("PREORDER")]
    public async Task ReplacementUsesMonitoredLoadingAndReceiptScopedPickupWithoutASecondSale(string zone)
    {
        var f = await Setup(member: true); using var seller = Client("SELLER", f.Actor, f.Seller); using var staff = Client("SELLER_STAFF", f.Staff, f.Seller); using var device = await Device(f);
        await Sql("UPDATE kiosk_ops.slots SET zone=@zone WHERE id=@slot", ("zone", zone), ("slot", f.Slot));
        if (zone == "PREORDER")
            await Assert.ThrowsAsync<PostgresException>(async () => {
                await Sql("SELECT flow.stock_bouquet(public.uuid_v7(),@product,@slot,@actor,'quality-direct-stock-rejected')",
                    ("product", f.Product), ("slot", f.Slot), ("actor", f.Actor));
            });
        var (_, ready) = await ReadyReplacement(f, seller, staff, device);
        Assert.Equal("REPLACEMENT_READY", ready.HandlingState);
        var items = await device.GetFromJsonAsync<JsonElement>($"/api/kiosks/{f.Kiosk}/catalog/items");
        Assert.Empty(items.EnumerateArray());
        var path = $"/api/kiosks/{f.Kiosk}/quality-replacements/open";
        var input = new QualityPickupRequest(f.Complaint, f.Order, f.Tracking, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsJsonAsync(path, input)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await device.PostAsJsonAsync(path, input with { TrackingToken = "AAAAAAAA" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.PostAsJsonAsync($"/api/kiosks/{Guid.NewGuid()}/quality-replacements/open", input)).StatusCode);
        await Presence(f, 1);
        var response = await device.PostAsJsonAsync(path, input); await Success(response);
        var first = (await response.Content.ReadFromJsonAsync<QualityPickupResponse>())!;
        response = await device.PostAsJsonAsync(path, input); await Success(response);
        Assert.Equal(first.CommandId, (await response.Content.ReadFromJsonAsync<QualityPickupResponse>())!.CommandId);
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(path, input with { AttemptId = Guid.NewGuid() })).StatusCode);
        var dispatcher = new DeviceCommandDispatcher(Data, factory.Services.GetRequiredService<DotNetCore.CAP.ICapPublisher>(),
            new DeviceKeyRing(new Dictionary<string, byte[]> { [f.Kiosk.ToString()] = new byte[32] }));
        Assert.Contains(first.CommandId, await dispatcher.PendingAsync(CancellationToken.None));
        await Sql("SELECT flow.device_event(@cmd,'SENT'); SELECT flow.device_event(@cmd,'ACK'); SELECT flow.device_event(@cmd,'OPENED');", ("cmd", first.CommandId));
        // CLOSE precedes the empty telemetry; it must not create a second completed sale.
        await Sql("SELECT flow.device_event(@cmd,'CLOSED')", ("cmd", first.CommandId));
        Assert.Equal("READY", (await Read(seller, f)).Replacement!.State);
        await Presence(f, 0);
        await Sql("SELECT flow.expire_quality_replacements()");
        var done = await Read(seller, f);
        Assert.Equal("COLLECTED", done.Replacement!.State); Assert.Equal("RESOLVED_FIXED", done.Status);
        Assert.Equal("RENTED_EMPTY", await Sql("SELECT status FROM kiosk_ops.slots WHERE id=@id", ("id", f.Slot)));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM ordering.order_items WHERE order_id=@id", ("id", f.Order)));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM notify.audit_logs WHERE entity_id=@id AND action='POINTS_EARNED'", ("id", f.Order)));
        Assert.Equal(100L, await Sql("SELECT loyalty_points FROM identity.users WHERE id=@id", ("id", f.Customer!.Value)));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM payment.payments WHERE checkout_id=(SELECT checkout_id FROM ordering.orders WHERE id=@id)", ("id", f.Order)));
        Assert.Equal(1, await Sql("SELECT quantity FROM catalog.flower_products WHERE id=@id", ("id", f.Product)));
        await Sql("SELECT flow.device_event(@cmd,'CLOSED'); SELECT flow.expire_quality_replacements()", ("cmd", first.CommandId));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM notify.audit_logs WHERE entity_id=@id AND action='QUALITY_REPLACEMENT_COLLECTED'", ("id", f.Complaint)));
    }

    [Fact]
    public async Task ExpiryRequiresRecoveryAndDoesNotRefundOrRestoreShopStock()
    {
        var f = await Setup(); using var seller = Client("SELLER", f.Actor, f.Seller); using var staff = Client("SELLER_STAFF", f.Staff, f.Seller); using var device = await Device(f);
        var (task, _) = await ReadyReplacement(f, seller, staff, device);
        await Sql("UPDATE kiosk_ops.quality_replacements SET expires_at=public.app_now()-interval '1 second' WHERE task_id=@id; SELECT flow.expire_quality_replacements()", ("id", task));
        var expired = await Read(seller, f);
        Assert.Equal("EXPIRED", expired.Replacement!.State); Assert.Equal("ATTENTION_REQUIRED", expired.HandlingState);
        Assert.Equal("OPEN", expired.Status);
        Assert.Equal("PENDING_REMOVAL", await Sql("SELECT status FROM kiosk_ops.slots WHERE id=@id", ("id", f.Slot)));
        Assert.Equal(1, await Sql("SELECT quantity FROM catalog.flower_products WHERE id=@id", ("id", f.Product)));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM payment.payments WHERE order_id=@id AND kind='REFUND'", ("id", f.Order)));
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync($"/api/kiosks/{f.Kiosk}/quality-replacements/open",
            new QualityPickupRequest(f.Complaint, f.Order, f.Tracking, Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task AdminCannotResolveWhileDoorIsOpenAndClosedResolutionRevokesReplacement()
    {
        var f = await Setup(); using var seller = Client("SELLER", f.Actor, f.Seller); using var staff = Client("SELLER_STAFF", f.Staff, f.Seller); using var device = await Device(f);
        await ReadyReplacement(f, seller, staff, device);
        var opened = await device.PostAsJsonAsync($"/api/kiosks/{f.Kiosk}/quality-replacements/open",
            new QualityPickupRequest(f.Complaint, f.Order, f.Tracking, Guid.NewGuid())); await Success(opened);
        var cmd = (await opened.Content.ReadFromJsonAsync<QualityPickupResponse>())!.CommandId;
        await Sql("SELECT flow.device_event(@cmd,'SENT'); SELECT flow.device_event(@cmd,'ACK'); SELECT flow.device_event(@cmd,'OPENED');", ("cmd", cmd));
        using var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var resolution = new { p_dispute = f.Complaint, p_refund = 0, p_decision = "Reviewed the complaint and the agreed final explanation" };
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/flows/resolve_dispute", resolution)).StatusCode);
        await Sql("SELECT flow.device_event(@cmd,'CLOSED')", ("cmd", cmd));
        await Success(await admin.PostAsJsonAsync("/api/admin/flows/resolve_dispute", resolution));
        var closed = await Read(seller, f);
        Assert.Equal("RESOLVED", closed.HandlingState); Assert.Equal("CANCELLED", closed.Replacement!.State);
        Assert.Equal("PENDING_REMOVAL", await Sql("SELECT status FROM kiosk_ops.slots WHERE id=@id", ("id", f.Slot)));
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync($"/api/kiosks/{f.Kiosk}/quality-replacements/open",
            new QualityPickupRequest(f.Complaint, f.Order, f.Tracking, Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task SellerCanCancelReplacementIdempotentlyAndPhysicalFlowersAwaitRecovery()
    {
        var f = await Setup(); using var seller = Client("SELLER", f.Actor, f.Seller); using var staff = Client("SELLER_STAFF", f.Staff, f.Seller); using var device = await Device(f);
        var (_, ready) = await ReadyReplacement(f, seller, staff, device);
        var body = new CancelQualityReplacement(ready.Version, "The customer requests another resolution, recover the replacement flowers");
        var path = $"/api/me/complaints/{f.Complaint}/replacement/cancel";
        var cancelled = await seller.PostAsJsonAsync(path, body); await Success(cancelled);
        var again = await seller.PostAsJsonAsync(path, body); await Success(again);
        Assert.Equal((await cancelled.Content.ReadFromJsonAsync<QualityVersion>())!.Version, (await again.Content.ReadFromJsonAsync<QualityVersion>())!.Version);
        var detail = await Read(seller, f);
        Assert.Equal("CANCELLED", detail.Replacement!.State); Assert.Equal("ATTENTION_REQUIRED", detail.HandlingState);
        Assert.Equal("PENDING_REMOVAL", await Sql("SELECT status FROM kiosk_ops.slots WHERE id=@id", ("id", f.Slot)));
        Assert.Equal(1, await Sql("SELECT quantity FROM catalog.flower_products WHERE id=@id", ("id", f.Product)));
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync($"/api/kiosks/{f.Kiosk}/quality-replacements/open",
            new QualityPickupRequest(f.Complaint, f.Order, f.Tracking, Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task CustomerCannotReportBeforeReceivingAndSellerCannotAssignForeignStaff()
    {
        var f = await Setup(); var other = await Setup();
        using var seller = Client("SELLER", f.Actor, f.Seller);
        await Success(await seller.PostAsJsonAsync($"/api/me/complaints/{f.Complaint}/accept", new QualityVersion(1)));
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync($"/api/me/complaints/{f.Complaint}/assignment",
            new AssignQualityTask(2, Guid.NewGuid(), other.Staff, f.Item, "Check this customer's reported flower condition"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync($"/api/me/complaints/{f.Complaint}/assignment",
            new AssignQualityTask(2, Guid.NewGuid(), f.Staff, other.Item, "Check this customer's reported flower condition"))).StatusCode);
        var unpaidCollection = Guid.NewGuid();
        var tracking = (string)(await Sql("""
            INSERT INTO ordering.orders(id,order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token)
            VALUES(@id,@id::text,@id,@seller,@kiosk,'PAID',10000,10000,flow.new_tracking_token()) RETURNING tracking_token;
            """, ("id", unpaidCollection), ("seller", f.Seller), ("kiosk", f.Kiosk)))!;
        var photo = EvidenceFixture.Reference(factory.Services, "open_dispute", PrivateEvidence.ReceiptBinding(unpaidCollection, tracking));
        using var customer = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsJsonAsync("/api/receipts/flows/open_dispute", new
        { p_order = unpaidCollection, p_tracking = tracking, p_reason = "The flower quality is unacceptable", p_photo = photo })).StatusCode);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM ordering.disputes WHERE order_id=@id", ("id", unpaidCollection)));
    }

    [Fact]
    public async Task ReassignmentRetainsAnUnloadedPlanAndRevokesThePreviousStaffAccess()
    {
        var f = await Setup(); using var seller = Client("SELLER", f.Actor, f.Seller);
        using var staff = Client("SELLER_STAFF", f.Staff, f.Seller); using var next = Client("SELLER_STAFF", f.OtherStaff, f.Seller);
        using var device = await Device(f);
        var task = await Assign(seller, f);
        await Success(await staff.PostAsJsonAsync($"/api/staff/tasks/{task}/transition", new TransitionStaffTask(1, "IN_PROGRESS", "Preparing replacement loading for the customer")));
        var detail = await Read(seller, f);
        await Success(await seller.PostAsJsonAsync($"/api/me/complaints/{f.Complaint}/replacement-plan", new QualityReplacementPlan(detail.Version, 2, f.Product, f.Slot)));
        await Presence(f, 0);
        var oldAccess = await staff.PostAsJsonAsync($"/api/staff/tasks/{task}/inventory-maintenance/access", new { operation = "START" }); await Success(oldAccess);
        var old = (await oldAccess.Content.ReadFromJsonAsync<MaintenanceAccessIssued>())!;
        var reassigned = await seller.PostAsJsonAsync($"/api/sellers/{f.Seller}/staff-tasks/{task}/reassign",
            new ReassignStaffTask(2, f.OtherStaff, "The next shop staff member will deliver the replacement")); await Success(reassigned);
        var updated = (await reassigned.Content.ReadFromJsonAsync<StaffTaskResult>())!;
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/staff/tasks/{task}/quality")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/staff/tasks/{task}/inventory-maintenance")).StatusCode);
        var rejected = await device.PostAsJsonAsync($"/api/kiosks/{f.Kiosk}/maintenance-access/{old.Id}/confirm", new { code = old.Code });
        Assert.False(rejected.IsSuccessStatusCode);
        Assert.Equal("PLANNED", (await Read(seller, f)).Replacement!.State);
        await Success(await next.PostAsJsonAsync($"/api/staff/tasks/{task}/transition", new TransitionStaffTask(updated.Version!.Value, "IN_PROGRESS", "Taking over the assigned replacement loading")));
        await Success(await next.PostAsJsonAsync($"/api/staff/tasks/{task}/inventory-maintenance/access", new { operation = "START" }));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM kiosk_ops.slot_maintenance_sessions WHERE id=@id", ("id", task)));
    }

    [Fact]
    public async Task ComplaintEvidenceLinksOnlyAttachedPhotosFromTheCorrectQualityTask()
    {
        var f = await Setup(); using var seller = Client("SELLER", f.Actor, f.Seller); using var staff = Client("SELLER_STAFF", f.Staff, f.Seller);
        var task = await Assign(seller, f);
        await Success(await staff.PostAsJsonAsync($"/api/staff/tasks/{task}/transition", new TransitionStaffTask(1, "IN_PROGRESS", "Checking the customer's flower condition")));
        var attachment = Guid.NewGuid();
        var ticket = EvidenceFixture.Reference(factory.Services, "staff_task", $"staff:{f.Staff}:{task}:2");
        await Success(await staff.PostAsJsonAsync($"/api/staff/tasks/{task}/evidence", new StaffEvidenceInput(attachment, 2, ticket)));
        var detail = await Read(seller, f);
        var photo = Assert.Single(detail.Evidence.Items, x => x.Id == attachment);
        Assert.Equal("STAFF_TASK", photo.Source); Assert.Equal(task, photo.TaskId);
        Assert.DoesNotContain("cloudinary", photo.Url, StringComparison.OrdinalIgnoreCase);
        using var outsider = factory.CreateClient(); outsider.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(photo.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await seller.GetAsync(photo.Url.Replace(f.Complaint.ToString(), Guid.NewGuid().ToString()))).StatusCode);
        // The test ticket intentionally has no real storage bytes; a storage error remains an error.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await seller.GetAsync(photo.Url)).StatusCode);
        var different = await Setup(); using var otherSeller = Client("SELLER", different.Actor, different.Seller);
        Assert.DoesNotContain((await Read(otherSeller, different)).Evidence.Items, x => x.Id == attachment);
    }

    [Fact]
    public async Task PrivateEvidenceBytesPaginationAndReceiptCapabilityAreChecked()
    {
        var f = await Setup(); using var seller = Client("SELLER", f.Actor, f.Seller);
        var task = await Assign(seller, f);
        using var staff = Client("SELLER_STAFF", f.Staff, f.Seller);
        await Success(await staff.PostAsJsonAsync($"/api/staff/tasks/{task}/transition", new TransitionStaffTask(1, "IN_PROGRESS", "Collecting evidence of the reported flower condition")));
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
        using var storage = new QualityStorage(png); using var transport = new HttpClient(storage);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CLOUDINARY_CLOUD_NAME"] = "test-cloud", ["CLOUDINARY_API_KEY"] = "test", ["CLOUDINARY_API_SECRET"] = "test-secret" }).Build();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped(_ => new CloudinaryMedia(transport, configuration, TimeProvider.System))));
        using var reader = app.CreateClient(); reader.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: f.Actor, sellerId: f.Seller));
        // The upload-ticket verifier is tested separately. Use verified metadata to exercise private reads here.
        var attachment = Guid.NewGuid(); var privateId = "florabot-evidence/" + attachment.ToString("N");
        await Sql("""
            INSERT INTO notify.attachments(id,owner_service,owner_type,owner_id,phase,file_url,mime_type,size_bytes,sha256,uploaded_by)
            VALUES(@id,'kiosk_ops','staff_task',@task,'EVIDENCE',@url,'image/png',@size,@hash,@staff);
            """, ("id", attachment), ("task", task), ("url", $"https://res.cloudinary.com/test-cloud/image/authenticated/v1/{privateId}.png"),
            ("size", (long)png.Length), ("hash", Convert.ToHexStringLower(SHA256.HashData(png))), ("staff", f.Staff));
        var photoPath = $"/api/me/complaints/{f.Complaint}/evidence/{attachment}";
        var bytes = await reader.GetAsync(photoPath); await Success(bytes);
        Assert.Equal(png, await bytes.Content.ReadAsByteArrayAsync()); Assert.True(bytes.Headers.CacheControl!.NoStore);
        using var receipt = app.CreateClient();
        var customerPhoto = $"/api/receipts/complaints/{f.Complaint}/evidence/{attachment}";
        Assert.Equal(HttpStatusCode.NotFound, (await receipt.PostAsJsonAsync(customerPhoto, new ReceiptLookupRequest(f.Order, "AAAAAAAA"))).StatusCode);
        var customerBytes = await receipt.PostAsJsonAsync(customerPhoto, new ReceiptLookupRequest(f.Order, f.Tracking)); await Success(customerBytes);
        Assert.Equal(png, await customerBytes.Content.ReadAsByteArrayAsync());
        storage.Fail = true; Assert.Equal(HttpStatusCode.ServiceUnavailable, (await reader.GetAsync(photoPath)).StatusCode);
        storage.Fail = false; storage.Corrupt = true; Assert.Equal(HttpStatusCode.ServiceUnavailable, (await reader.GetAsync(photoPath)).StatusCode);
        await Sql("""
            INSERT INTO notify.attachments(owner_service,owner_type,owner_id,phase,file_url,mime_type,size_bytes,sha256,uploaded_by)
            SELECT 'kiosk_ops','staff_task',@task,'EVIDENCE','https://example.invalid/photo-'||n,'image/png',1,'verified-fixture',@staff
            FROM generate_series(1,23) n;
            """, ("task", task), ("staff", f.Staff));
        var first = await Read(seller, f);
        var second = (await seller.GetFromJsonAsync<QualityComplaintDetail>($"/api/me/complaints/{f.Complaint}?evidencePage=2"))!;
        Assert.Equal(20, first.Evidence.Items.Count); Assert.True(first.Evidence.HasMore);
        Assert.Equal(5, second.Evidence.Items.Count); Assert.False(second.Evidence.HasMore);
        Assert.Empty(first.Evidence.Items.Select(x => x.Id).Intersect(second.Evidence.Items.Select(x => x.Id)));
    }

    private sealed class QualityStorage(byte[] bytes) : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public bool Corrupt { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.cloudinary.com", request.RequestUri!.Host);
            return Task.FromResult(Fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Corrupt ? "bad-image"u8.ToArray() : bytes) });
        }
    }
}
