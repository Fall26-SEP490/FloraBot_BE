using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Tests;

public sealed class SellerSlotRemovalTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record Fixture(Guid Seller, Guid Actor, Guid Kiosk, Guid Slot, Guid Product, Guid Bouquet);

    private async Task<Fixture> Setup(string status = "STOCKED", string? zone = "WALK_IN", string role = "SELLER")
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var cmd = source.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Removal tests','0901234567','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@actor,@actor::text || '@example.invalid','!unprovisioned','Removal actor',@role,
                CASE WHEN @role IN ('SELLER','SELLER_STAFF') THEN @seller ELSE NULL END);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,seller_id)
            VALUES(@kiosk,@kiosk::text,'Removal kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE',@seller);
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status)
            VALUES(@product,@seller,'Removal flower',350000,48,'ACTIVE');
            INSERT INTO kiosk_ops.bouquets(id,product_id,seller_id,qr_code,price_snapshot,status,stocked_at,sellable_until)
            VALUES(@bouquet,@product,@seller,@bouquet::text,350000,
                CASE WHEN @status='PENDING_REMOVAL' THEN 'SOLD' ELSE 'STOCKED' END,
                public.app_now(),public.app_now()+interval '48 hours');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,status,current_seller_id,bouquet_id,zone)
            VALUES(@slot,@kiosk,'A01',0,@status,@seller,@bouquet,@zone);
            INSERT INTO kiosk_ops.slot_assignments(slot_id,seller_id,assigned_by)
            VALUES(@slot,@seller,'10000000-0000-0000-0000-000000000001');
            """);
        Parameters(cmd, f);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("zone", NpgsqlDbType.Text, (object?)zone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("role", role);
        await cmd.ExecuteNonQueryAsync();
        return f;
    }

    private static void Parameters(NpgsqlCommand cmd, Fixture f)
    {
        foreach (var (name, id) in new[] { ("seller", f.Seller), ("actor", f.Actor), ("kiosk", f.Kiosk),
            ("slot", f.Slot), ("product", f.Product), ("bouquet", f.Bouquet) }) cmd.Parameters.AddWithValue(name, id);
    }

    private HttpClient Client(Fixture f, string role = "SELLER")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role, userId: f.Actor, sellerId: f.Seller));
        return client;
    }

    private static Task<HttpResponseMessage> Remove(HttpClient client, Fixture f, Guid? bouquet = null, bool confirmed = true)
        => client.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"/api/me/kiosks/{f.Kiosk}/slots/{f.Slot}/bouquet")
        { Content = JsonContent.Create(new RemoveSlotBouquetRequest(bouquet ?? f.Bouquet, "Thu hoi hoa de nap bo moi", confirmed)) });

    private async Task Execute(Fixture f, string sql)
    {
        using var scope = factory.Services.CreateScope();
        await using var cmd = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>().CreateCommand(sql);
        Parameters(cmd, f);
        await cmd.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("STOCKED", "WALK_IN")]
    [InlineData("PENDING_REMOVAL", "PREORDER")]
    [InlineData("PENDING_REMOVAL", null)]
    public async Task RemovalPreservesLeaseZoneAndHistoryWithoutCreatingDeviceEvents(string state, string? zone)
    {
        var f = await Setup(state, zone);
        using var client = Client(f);
        var response = await Remove(client, f);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadFromJsonAsync<RemoveSlotBouquetResponse>();
        Assert.Equal("RENTED_EMPTY", body!.Status);
        Assert.Null(body.BouquetId);
        Assert.Equal(f.Bouquet, body.RemovedBouquetId);
        Assert.Equal(zone, body.Zone);
        using var scope = factory.Services.CreateScope();
        await using var check = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            SELECT s.status='RENTED_EMPTY' AND s.bouquet_id IS NULL AND s.current_seller_id=@seller
              AND s.hold_order_id IS NULL AND s.hold_until IS NULL AND s.row_version=1
              AND b.status='RETURNED'
              AND (SELECT count(*)=1 FROM kiosk_ops.inventory_logs WHERE slot_id=@slot AND movement_type='RETURN_SELLER' AND performed_by=@actor)
              AND (SELECT count(*)=1 FROM notify.audit_logs WHERE entity_id=@slot AND action='SELLER_SLOT_BOUQUET_REMOVED' AND actor_id=@actor)
              AND NOT EXISTS(SELECT 1 FROM kiosk_ops.unlock_tokens WHERE slot_id=@slot)
            FROM kiosk_ops.slots s CROSS JOIN kiosk_ops.bouquets b WHERE s.id=@slot AND b.id=@bouquet;
            """);
        Parameters(check, f);
        Assert.Equal(true, await check.ExecuteScalarAsync());
        var slots = await client.GetFromJsonAsync<List<SellerSlotResponse>>($"/api/me/kiosks/{f.Kiosk}/slots");
        Assert.Null(Assert.Single(slots!).BouquetId);
    }

    [Fact]
    public async Task ParallelRetryIsIdempotentAndOldRequestCannotRemoveNewStock()
    {
        var f = await Setup();
        using var client = Client(f);
        var results = await Task.WhenAll(Remove(client, f), Remove(client, f));
        Assert.All(results, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        await Execute(f, """
            INSERT INTO kiosk_ops.bouquets(product_id,seller_id,qr_code,price_snapshot,stocked_at,sellable_until)
            SELECT @product,@seller,public.uuid_v7()::text,350000,public.app_now(),public.app_now()+interval '48 hours';
            UPDATE kiosk_ops.slots SET status='STOCKED',bouquet_id=(SELECT id FROM kiosk_ops.bouquets WHERE product_id=@product AND id<>@bouquet LIMIT 1) WHERE id=@slot;
            """);
        Assert.Equal(HttpStatusCode.Conflict, (await Remove(client, f)).StatusCode);
        using var scope = factory.Services.CreateScope();
        await using var check = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>().CreateCommand(
            "SELECT count(*) FROM kiosk_ops.inventory_logs WHERE slot_id=@slot AND movement_type='RETURN_SELLER'");
        check.Parameters.AddWithValue("slot", f.Slot);
        Assert.Equal(1L, await check.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("AWAITING_PAYMENT")]
    [InlineData("PAID")]
    [InlineData("DISPENSING")]
    [InlineData("COMPLETED")]
    [InlineData("DISPUTED")]
    public async Task ActiveOrCompletedCustomerOrderBlocksRemovalEvenIfSlotStateIsInconsistent(string status)
    {
        var f = await Setup("PENDING_REMOVAL");
        await AddOrder(f, status);
        using var client = Client(f);
        Assert.Equal(HttpStatusCode.Conflict, (await Remove(client, f)).StatusCode);
        await AssertUnchanged(f);
    }

    private async Task AddOrder(Fixture f, string status)
    {
        await Execute(f, $$"""
            INSERT INTO ordering.orders(id,order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token,completed_at)
            VALUES(@slot,@slot::text,@slot,@seller,@kiosk,'{{status}}',350000,350000,@slot::text,
                CASE WHEN '{{status}}' IN ('COMPLETED','DISPUTED') THEN public.app_now() ELSE NULL END);
            INSERT INTO ordering.order_items(order_id,item_type,bouquet_id,slot_id,name_snapshot,quantity,unit_price,line_total)
            VALUES(@slot,'BOUQUET',@bouquet,@slot,'Removal test order',1,350000,350000);
            """);
    }

    [Fact]
    public async Task FailedPickupCanBeRemovedWithoutChangingOrderOrRefund()
    {
        var f = await Setup("PENDING_REMOVAL");
        await AddOrder(f, "DISPENSE_FAILED");
        await Execute(f, """
            INSERT INTO payment.payments(id,kind,purpose,checkout_id,gateway,idempotency_key,amount,status,paid_at)
            VALUES(@bouquet,'CHARGE','ORDER_CHECKOUT',@slot,'MANUAL',@bouquet::text,350000,'SUCCEEDED',public.app_now());
            INSERT INTO payment.payments(kind,purpose,checkout_id,order_id,parent_payment_id,gateway,idempotency_key,amount,status,approved_by)
            VALUES('REFUND','ORDER_CHECKOUT',@slot,@slot,@bouquet,'MANUAL',@slot::text,350000,'PENDING','10000000-0000-0000-0000-000000000001');
            """);
        using var client = Client(f);
        Assert.Equal(HttpStatusCode.OK, (await Remove(client, f)).StatusCode);
        using var scope = factory.Services.CreateScope();
        await using var check = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            SELECT (SELECT status='DISPENSE_FAILED' FROM ordering.orders WHERE id=@slot)
              AND (SELECT count(*)=1 FROM ordering.order_items WHERE order_id=@slot AND bouquet_id=@bouquet)
              AND (SELECT count(*)=2 FROM payment.payments WHERE checkout_id=@slot)
              AND EXISTS(SELECT 1 FROM payment.payments WHERE checkout_id=@slot AND kind='REFUND' AND status='PENDING' AND amount=350000)
              AND EXISTS(SELECT 1 FROM payment.payments WHERE id=@bouquet AND status='SUCCEEDED');
            """);
        Parameters(check, f);
        Assert.Equal(true, await check.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("ISSUED")]
    [InlineData("SENT")]
    [InlineData("ACKED")]
    [InlineData("OPENED")]
    public async Task ActiveDoorCommandBlocksRemoval(string tokenState)
    {
        var f = await Setup();
        await Execute(f, $$"""
            INSERT INTO kiosk_ops.unlock_tokens(kiosk_id,slot_id,purpose,issued_to_user_id,token_hash,status,expires_at,door_opened_at)
            VALUES(@kiosk,@slot,'SELLER_ACCESS',@actor,@slot::text,'{{tokenState}}',public.app_now()+interval '5 minutes',
              CASE WHEN '{{tokenState}}'='OPENED' THEN public.app_now() ELSE NULL END);
            """);
        using var client = Client(f);
        Assert.Equal(HttpStatusCode.Conflict, (await Remove(client, f)).StatusCode);
        await AssertUnchanged(f);
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("SELLER_STAFF")]
    [InlineData("CUSTOMER")]
    [InlineData("TECHNICIAN")]
    [InlineData("OPERATIONS_MANAGER")]
    public async Task OnlySellerMayRemove(string role)
    {
        var f = await Setup(role: role);
        using var client = Client(f, role);
        Assert.Equal(HttpStatusCode.Forbidden, (await Remove(client, f)).StatusCode);
        await AssertUnchanged(f);
    }

    [Fact]
    public async Task ForeignOwnerWrongKioskMissingConfirmationAndStaleBouquetAreDenied()
    {
        var own = await Setup(); var foreign = await Setup();
        using var client = Client(own);
        Assert.Equal(HttpStatusCode.NotFound, (await Remove(client, foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Remove(client, own with { Kiosk = foreign.Kiosk })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Remove(client, own, confirmed: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Remove(client, own, Guid.NewGuid())).StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Remove(anonymous, own)).StatusCode);
        await AssertUnchanged(own);
        await AssertUnchanged(foreign);
    }

    private async Task AssertUnchanged(Fixture f)
    {
        using var scope = factory.Services.CreateScope();
        await using var check = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM kiosk_ops.slots WHERE id=@slot AND bouquet_id=@bouquet)
              AND NOT EXISTS(SELECT 1 FROM kiosk_ops.inventory_logs WHERE slot_id=@slot AND movement_type='RETURN_SELLER')
              AND NOT EXISTS(SELECT 1 FROM notify.audit_logs WHERE entity_id=@slot AND action='SELLER_SLOT_BOUQUET_REMOVED');
            """);
        Parameters(check, f);
        Assert.Equal(true, await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DeleteContractExportsJsonBodyAndPathParameters()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var operation = document.GetProperty("paths").GetProperty("/api/me/kiosks/{kioskId}/slots/{slotId}/bouquet").GetProperty("delete");
        Assert.True(operation.GetProperty("requestBody").GetProperty("required").GetBoolean());
        Assert.Equal(2, operation.GetProperty("parameters").EnumerateArray().Count(x => x.GetProperty("in").GetString() == "path"));
        Assert.True(document.GetProperty("components").GetProperty("schemas").TryGetProperty("RemoveSlotBouquetRequest", out _));
        var exportPath = Environment.GetEnvironmentVariable("EXPORT_OPENAPI_PATH");
        if (!string.IsNullOrEmpty(exportPath)) await File.WriteAllTextAsync(exportPath, document.GetRawText());
    }
}
