using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class SellerInventoryReadTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record Fixture(Guid Seller, Guid Actor, Guid Kiosk, Guid Slot, Guid Product, Guid Bouquet);
    private async Task<Fixture> Setup()
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Execute(f, """
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Inventory read test','0901234567','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@actor,@actor::text || '@example.invalid','!unprovisioned','Inventory seller','SELLER',@seller);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,seller_id)
            VALUES(@kiosk,@kiosk::text,'Read kiosk','Read address','HCM',@kiosk::text,@kiosk::text,'OFFLINE',@seller);
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status)
            VALUES(@product,@seller,'Read flower',350000,48,'ACTIVE');
            INSERT INTO kiosk_ops.bouquets(id,product_id,seller_id,qr_code,price_snapshot,status,stocked_at,sellable_until)
            VALUES(@bouquet,@product,@seller,@bouquet::text,350000,'STOCKED',public.app_now(),public.app_now()+interval '48 hours');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,status,current_seller_id,bouquet_id,zone)
            VALUES(@slot,@kiosk,'A01',0,'STOCKED',@seller,@bouquet,'WALK_IN');
            INSERT INTO kiosk_ops.slot_assignments(slot_id,seller_id,assigned_by)
            VALUES(@slot,@seller,'10000000-0000-0000-0000-000000000001');
            """);
        return f;
    }

    private async Task Execute(Fixture f, string sql, Guid? order = null)
    {
        using var scope = factory.Services.CreateScope();
        await using var cmd = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>().CreateCommand(sql);
        foreach (var (name, id) in new[] { ("seller",f.Seller), ("actor",f.Actor), ("kiosk",f.Kiosk),
            ("slot",f.Slot), ("product",f.Product), ("bouquet",f.Bouquet), ("order",order ?? f.Slot) }) cmd.Parameters.AddWithValue(name, id);
        await cmd.ExecuteNonQueryAsync();
    }

    private HttpClient Client(Fixture f)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId:f.Actor, sellerId:f.Seller));
        return client;
    }
    private static string Route(Fixture f) => $"/api/me/kiosks/{f.Kiosk}/slots";
    private static async Task<SellerSlotResponse> Read(HttpClient client, Fixture f)
        => Assert.Single((await client.GetFromJsonAsync<List<SellerSlotResponse>>(Route(f)))!);

    private async Task AddOrder(Fixture f, Guid order, string status, string paymentStatus, int ageSeconds = 0)
    {
        await Execute(f, $$"""
            INSERT INTO ordering.orders(id,order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token,created_at)
            VALUES(@order,@order::text,@order,@seller,@kiosk,'{{status}}',350000,350000,left(replace(@order::text,'-',''),8),public.app_now()+interval '{{ageSeconds}} seconds');
            INSERT INTO ordering.order_items(order_id,item_type,bouquet_id,slot_id,name_snapshot,quantity,unit_price,line_total,line_status)
            VALUES(@order,'BOUQUET',@bouquet,@slot,'Inventory test',1,350000,350000,
              CASE WHEN '{{status}}' IN ('EXPIRED','CANCELLED') THEN 'CANCELLED' ELSE 'ACTIVE' END);
            INSERT INTO payment.payments(id,kind,purpose,checkout_id,gateway,idempotency_key,amount,status,paid_at)
            VALUES(@order,'CHARGE','ORDER_CHECKOUT',@order,'MANUAL',@order::text,350000,'{{paymentStatus}}',
              CASE WHEN '{{paymentStatus}}'='SUCCEEDED' THEN public.app_now() ELSE NULL END);
            """, order);
    }

    [Fact]
    public async Task VerifiedPaymentChangesBouquetAndOrderReadWhileProductStaysActiveAndPickupClearsSlot()
    {
        var f = await Setup(); var order = Guid.NewGuid();
        using var client = Client(f);
        var available = await Read(client, f);
        Assert.Equal("STOCKED", available.Status);
        Assert.Equal("STOCKED", available.BouquetStatus);
        Assert.Null(available.OrderId); Assert.Null(available.OrderStatus); Assert.Null(available.PaymentStatus);
        await AddOrder(f, order, "AWAITING_PAYMENT", "PENDING");
        await Execute(f, """
            UPDATE kiosk_ops.bouquets SET status='HELD' WHERE id=@bouquet;
            UPDATE kiosk_ops.slots SET status='HELD',hold_order_id=@order,hold_until=public.app_now()+interval '10 minutes' WHERE id=@slot;
            """, order);
        var awaiting = await Read(client, f);
        Assert.Equal("HELD", awaiting.Status); Assert.Equal("HELD", awaiting.BouquetStatus);
        Assert.Equal(order, awaiting.OrderId); Assert.Equal(order.ToString(), awaiting.OrderCode);
        Assert.Equal("AWAITING_PAYMENT", awaiting.OrderStatus); Assert.Equal("PENDING", awaiting.PaymentStatus);

        await Execute(f, "SELECT flow.checkout_paid(@order,@order::text,true,350000);", order);
        var paid = await Read(client, f);
        Assert.Equal("HELD", paid.Status); Assert.Equal("SOLD", paid.BouquetStatus);
        Assert.Equal("PAID", paid.OrderStatus); Assert.Equal("SUCCEEDED", paid.PaymentStatus);
        var products = await client.GetFromJsonAsync<List<ProductResponse>>("/api/me/products");
        Assert.Equal("ACTIVE", Assert.Single(products!).Status);

        // Test-only simulated input to the actual SQL device transition function.
        // No broker/device command is sent by this test.
        await Execute(f, """
            UPDATE kiosk_ops.kiosks SET status='ONLINE' WHERE id=@kiosk;
            SELECT flow.request_pickup(@order,left(replace(@order::text,'-',''),8),@kiosk);
            SELECT flow.device_event((SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@order ORDER BY issued_at DESC LIMIT 1),'SENT');
            SELECT flow.device_event((SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@order ORDER BY issued_at DESC LIMIT 1),'ACK');
            SELECT flow.device_event((SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@order ORDER BY issued_at DESC LIMIT 1),'OPENED');
            SELECT flow.device_event((SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@order ORDER BY issued_at DESC LIMIT 1),'CLOSED');
            """, order);
        var empty = await Read(client, f);
        Assert.Equal("RENTED_EMPTY", empty.Status);
        Assert.Null(empty.BouquetId); Assert.Null(empty.BouquetStatus);
        Assert.Null(empty.OrderId); Assert.Null(empty.OrderCode); Assert.Null(empty.OrderStatus); Assert.Null(empty.PaymentStatus);
    }

    [Fact]
    public async Task FailedPickupReportsLatestOrderAndChargeInsteadOfRefundOrEarlierExpiredOrder()
    {
        var f = await Setup(); var expired = Guid.NewGuid(); var failed = Guid.NewGuid();
        await AddOrder(f, expired, "EXPIRED", "EXPIRED", -60);
        await AddOrder(f, failed, "DISPENSE_FAILED", "SUCCEEDED");
        await Execute(f, """
            UPDATE kiosk_ops.slots SET status='PENDING_REMOVAL' WHERE id=@slot;
            UPDATE kiosk_ops.bouquets SET status='SOLD' WHERE id=@bouquet;
            INSERT INTO payment.payments(kind,purpose,checkout_id,order_id,parent_payment_id,gateway,idempotency_key,amount,status,approved_by)
            VALUES('REFUND','ORDER_CHECKOUT',@order,@order,@order,'MANUAL',@bouquet::text,350000,'PENDING','10000000-0000-0000-0000-000000000001');
            """, failed);
        using var client = Client(f);
        var row = await Read(client, f);
        Assert.Equal("PENDING_REMOVAL", row.Status); Assert.Equal("SOLD", row.BouquetStatus);
        Assert.Equal(failed, row.OrderId); Assert.Equal("DISPENSE_FAILED", row.OrderStatus);
        Assert.Equal("SUCCEEDED", row.PaymentStatus);
    }

    [Fact]
    public async Task HoldPointerWinsOverHistoryAndAvailableBouquetDoesNotInheritExpiredOrder()
    {
        var f = await Setup(); var active = Guid.NewGuid(); var expired = Guid.NewGuid();
        await AddOrder(f, active, "AWAITING_PAYMENT", "PENDING", -60);
        await AddOrder(f, expired, "EXPIRED", "EXPIRED");
        await Execute(f, """
            UPDATE kiosk_ops.bouquets SET status='HELD' WHERE id=@bouquet;
            UPDATE kiosk_ops.slots SET status='HELD',hold_order_id=@order,hold_until=public.app_now()+interval '10 minutes' WHERE id=@slot;
            """, active);
        using var client = Client(f);
        Assert.Equal(active, (await Read(client,f)).OrderId);
        await Execute(f, """
            UPDATE kiosk_ops.slots SET status='STOCKED',hold_order_id=NULL,hold_until=NULL WHERE id=@slot;
            UPDATE kiosk_ops.bouquets SET status='STOCKED' WHERE id=@bouquet;
            """);
        var available = await Read(client,f);
        Assert.Equal("STOCKED",available.BouquetStatus);
        Assert.Null(available.OrderId); Assert.Null(available.PaymentStatus);
    }

    [Fact]
    public async Task NewBouquetInSameSlotNeverInheritsEarlierSalesDataAndForeignKioskIsHidden()
    {
        var f = await Setup(); var foreign = await Setup(); var order = Guid.NewGuid();
        await AddOrder(f,order,"DISPENSE_FAILED","SUCCEEDED");
        await Execute(f,"""
            INSERT INTO kiosk_ops.bouquets(product_id,seller_id,qr_code,price_snapshot,status,stocked_at,sellable_until)
            VALUES(@product,@seller,public.uuid_v7()::text,350000,'STOCKED',public.app_now(),public.app_now()+interval '48 hours');
            UPDATE kiosk_ops.slots SET bouquet_id=(SELECT id FROM kiosk_ops.bouquets WHERE product_id=@product AND id<>@bouquet LIMIT 1) WHERE id=@slot;
            """);
        using var client = Client(f);
        var row = await Read(client,f);
        Assert.Equal("STOCKED", row.BouquetStatus);
        Assert.Null(row.OrderId); Assert.Null(row.PaymentStatus);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(Route(foreign))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"/api/me/kiosks/{Guid.NewGuid()}/slots")).StatusCode);
    }

    [Fact]
    public async Task OpenApiContainsNullableSaleFieldsAndReadDoesNotLeakPickupOrBankSecrets()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer",factory.Token("ADMIN"));
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var fields = document.GetProperty("components").GetProperty("schemas").GetProperty("SellerSlotResponse").GetProperty("properties");
        foreach (var key in new[] {"bouquetStatus","orderId","orderCode","orderStatus","paymentStatus"})
            Assert.True(fields.TryGetProperty(key,out _),key);
        foreach (var key in new[] {"trackingToken","customerId","refundBankAccountEnc","bankAccountNumber"})
            Assert.False(fields.TryGetProperty(key,out _),key);
    }
}
