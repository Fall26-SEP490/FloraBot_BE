using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FloraBot.Api.Modules.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class PreorderPickupOtpTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record Fixture(Guid Seller, Guid Actor, Guid Customer, Guid Kiosk, Guid Slot, Guid Product, Guid Bouquet, Guid Order, Guid Request)
    {
        public string Email => $"{Customer}@example.invalid";
        public string Key => $"test-only-device-{Kiosk}";
    }
    private PreorderPickupOtp Service => factory.Services.GetRequiredService<PreorderPickupOtp>();
    private async Task<Fixture> Setup()
    {
        var f = new Fixture(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        await Sql(f,"""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'OTP shop','0901234567','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@actor,@actor::text||'@example.invalid','!test','Seller','SELLER',@seller),
              (@customer,@email,'!test','Customer','CUSTOMER',NULL);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,seller_id,api_key_hash,last_heartbeat_at)
            VALUES(@kiosk,@kiosk::text,'OTP kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE',@seller,@keyhash,public.app_now());
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status)
            VALUES(@product,@seller,'OTP flower',350000,48,'ACTIVE');
            INSERT INTO ordering.orders(id,order_code,checkout_id,customer_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token)
            VALUES(@order,@order::text,@order,@customer,@seller,@kiosk,'PAID',350000,350000,@order::text);
            INSERT INTO ordering.web_requests(id,customer_id,seller_id,product_id,kiosk_id,kind,instructions,pickup_at,pickup_before,state,order_id)
            VALUES(@request,@customer,@seller,@product,@kiosk,'CUSTOM','Test preorder',public.app_now()-interval '5 minutes',public.app_now()+interval '2 hours','ORDERED',@order);
            INSERT INTO payment.payments(kind,purpose,checkout_id,gateway,idempotency_key,amount,status,paid_at)
            VALUES('CHARGE','ORDER_CHECKOUT',@order,'MANUAL',@order::text,350000,'SUCCEEDED',public.app_now());
            INSERT INTO kiosk_ops.bouquets(id,product_id,seller_id,qr_code,price_snapshot,status,stocked_at,sellable_until)
            VALUES(@bouquet,@product,@seller,@bouquet::text,350000,'SOLD',public.app_now(),public.app_now()+interval '48 hours');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,status,current_seller_id,bouquet_id,zone,hold_order_id,hold_until)
            VALUES(@slot,@kiosk,'A01',0,'HELD',@seller,@bouquet,'PREORDER',@order,public.app_now()+interval '2 hours');
            INSERT INTO ordering.order_items(order_id,item_type,bouquet_id,slot_id,name_snapshot,quantity,unit_price,line_total)
            VALUES(@order,'BOUQUET',@bouquet,@slot,'OTP flower',1,350000,350000);
            """);
        return f;
    }
    private async Task<object?> Sql(Fixture f,string sql)
    {
        await using var cmd=factory.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand(sql);
        foreach(var (key,value) in new[]{("seller",f.Seller),("actor",f.Actor),("customer",f.Customer),("kiosk",f.Kiosk),("slot",f.Slot),("product",f.Product),("bouquet",f.Bouquet),("order",f.Order),("request",f.Request)})
            cmd.Parameters.AddWithValue(key,value);
        cmd.Parameters.AddWithValue("email",f.Email);
        cmd.Parameters.AddWithValue("keyhash",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(f.Key))).ToLowerInvariant());
        return await cmd.ExecuteScalarAsync();
    }
    private string Code(Fixture f) => Regex.Match(factory.EmailSender.SentEmails.Last(e=>e.Recipient==f.Email).Text,@"\b\d{8}\b").Value;
    private async Task AssertFailure(int status,Func<Task> action) => Assert.Equal(status,(await Assert.ThrowsAsync<PickupOtpFailure>(action)).Status);

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    public async Task CustomerCanRequestAndSellerCanQuoteNearTermPickup(int minutes)
    {
        var f = await Setup();
        await Sql(f, "DELETE FROM ordering.web_requests WHERE id=@request; UPDATE kiosk_ops.kiosks SET latitude=10.77,longitude=106.69 WHERE id=@kiosk");
        Assert.Equal(f.Request, await Sql(f, $"SELECT flow.web_request(@request,@customer,'CUSTOM',@product,@kiosk,NULL,public.app_now()+interval '{minutes} minutes','Early pickup request')"));
        await Sql(f, $"SELECT flow.web_quote(@request,@seller,@actor,350000,public.app_now()+interval '{minutes} minutes','Early pickup quote',false)");
        Assert.Equal(true, await Sql(f, "SELECT state='QUOTED' AND quote_expires_at>public.app_now() FROM ordering.web_requests WHERE id=@request"));
    }

    [Theory]
    [InlineData("-1 minute")]
    [InlineData("31 days")]
    public async Task CustomerPickupStillRejectsPastOrBeyondMaximum(string offset)
    {
        var f = await Setup();
        await Sql(f, "DELETE FROM ordering.web_requests WHERE id=@request; UPDATE kiosk_ops.kiosks SET latitude=10.77,longitude=106.69 WHERE id=@kiosk");
        await Assert.ThrowsAsync<PostgresException>(() => Sql(f, $"SELECT flow.web_request(@request,@customer,'CUSTOM',@product,@kiosk,NULL,public.app_now()+interval '{offset}','Invalid pickup request')"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SellerCanStockWellBeforeAppointmentButNotAfterDeadline(bool expired)
    {
        var f = await Setup();
        await Sql(f, "DELETE FROM ordering.order_items WHERE order_id=@order; UPDATE kiosk_ops.slots SET status='RENTED_EMPTY',bouquet_id=NULL,hold_order_id=NULL,hold_until=NULL WHERE id=@slot; UPDATE ordering.web_requests SET pickup_at=public.app_now()+interval '6 hours',pickup_before=public.app_now()+interval '8 hours' WHERE id=@request");
        await Sql(f, "INSERT INTO kiosk_ops.slot_assignments(slot_id,seller_id,assigned_by) VALUES(@slot,@seller,@actor)");
        if (expired)
        {
            await Sql(f, "UPDATE ordering.web_requests SET pickup_at=public.app_now()-interval '2 hours',pickup_before=public.app_now()-interval '1 hour' WHERE id=@request");
            await Assert.ThrowsAsync<PostgresException>(() => Sql(f, "SELECT flow.web_fulfill(@request,@seller,@actor,@slot,@order::text)"));
        }
        else
        {
            Assert.IsType<Guid>(await Sql(f, "SELECT flow.web_fulfill(@request,@seller,@actor,@slot,@order::text)"));
            Assert.Equal("HELD", await Sql(f, "SELECT status FROM kiosk_ops.slots WHERE id=@slot"));
        }
    }
    [Fact]
    public async Task EmailCodeOpensOneCommandAndParallelRetryReturnsSameCommand()
    {
        var f=await Setup();
        var sent=await Service.Send(f.Request,f.Actor,default);
        Assert.DoesNotContain(f.Email,sent.RecipientMasked);
        var code=Code(f); Assert.Equal(8,code.Length);
        Assert.Equal(false,await Sql(f,$"SELECT code_hash='{code}' OR email_hash=@email FROM ordering.preorder_pickup_otps WHERE request_id=@request"));
        var attempt=Guid.NewGuid();
        var results=await Task.WhenAll(Service.Open(f.Kiosk,code,attempt,default),Service.Open(f.Kiosk,code,attempt,default));
        Assert.Equal(results[0].CommandId,results[1].CommandId);
        Assert.Equal(1L,await Sql(f,"SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@order"));
        Assert.Equal("DISPENSING",await Sql(f,"SELECT status FROM ordering.orders WHERE id=@order"));
        await AssertFailure(401,()=>Service.Open(f.Kiosk,code,Guid.NewGuid(),default));
        await AssertFailure(404,()=>Service.Status(f.Kiosk,attempt,"wrong",default));
        var status=await Service.Status(f.Kiosk,attempt,results[0].WatchToken,default);
        Assert.Equal("ISSUED",status.CommandStatus); Assert.Equal("DISPENSING",status.OrderStatus);
        await Sql(f,"""
            SELECT flow.device_event((SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@order),'SENT');
            SELECT flow.device_event((SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@order),'ACK');
            SELECT flow.device_event((SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@order),'OPENED');
            SELECT flow.device_event((SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@order),'CLOSED');
            """);
        Assert.Equal("COMPLETED",(await Service.Status(f.Kiosk,attempt,results[0].WatchToken,default)).OrderStatus);
    }
    [Theory]
    [InlineData("DELETE FROM payment.payments WHERE checkout_id=@order")]
    [InlineData("UPDATE kiosk_ops.slots SET zone='WALK_IN' WHERE id=@slot")]
    [InlineData("UPDATE kiosk_ops.slots SET status='RENTED_EMPTY',bouquet_id=NULL,hold_order_id=NULL,hold_until=NULL WHERE id=@slot")]
    [InlineData("UPDATE ordering.web_requests SET state='CANCELLED' WHERE id=@request")]
    public async Task CannotSendForUnpaidUnstockedOrIneligibleOrder(string mutation)
    {
        var f=await Setup(); await Sql(f,mutation);
        await AssertFailure(409,()=>Service.Send(f.Request,f.Actor,default));
        Assert.Equal(0L,await Sql(f,"SELECT count(*) FROM ordering.preorder_pickup_otps WHERE request_id=@request"));
    }
    [Theory]
    [InlineData("UPDATE kiosk_ops.kiosks SET status='OFFLINE' WHERE id=@kiosk",409)]
    [InlineData("UPDATE kiosk_ops.kiosks SET last_heartbeat_at=public.app_now()-interval '1 hour' WHERE id=@kiosk",409)]
    [InlineData("UPDATE ordering.web_requests SET pickup_before=public.app_now()-interval '1 second',pickup_at=public.app_now()-interval '1 hour' WHERE id=@request",409)]
    [InlineData("UPDATE identity.users SET email=@customer::text||'changed@example.invalid' WHERE id=@customer",401)]
    [InlineData("UPDATE ordering.preorder_pickup_otps SET expires_at=public.app_now()-interval '1 second',issued_at=public.app_now()-interval '1 hour' WHERE request_id=@request",401)]
    public async Task OpenRechecksTimeDeviceAndRecipient(string mutation,int status)
    {
        var f=await Setup(); await Service.Send(f.Request,f.Actor,default); var code=Code(f);
        await Sql(f,mutation); await AssertFailure(status,()=>Service.Open(f.Kiosk,code,Guid.NewGuid(),default));
        Assert.Equal(0L,await Sql(f,"SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@order"));
    }
    [Fact]
    public async Task ReadyPaidOrderCanBeCollectedBeforeScheduledPickup()
    {
        var f = await Setup();
        await Sql(f, "UPDATE ordering.web_requests SET pickup_at=public.app_now()+interval '1 hour' WHERE id=@request");
        await Service.Send(f.Request, f.Actor, default);
        var opened = await Service.Open(f.Kiosk, Code(f), Guid.NewGuid(), default);
        Assert.Equal(f.Order, opened.OrderId);
        Assert.Equal(1L, await Sql(f, "SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@order"));
    }
    [Fact]
    public async Task ResendRevokesOldCodeAndEmailFailureNeverActivatesCode()
    {
        var f=await Setup(); await Service.Send(f.Request,f.Actor,default); var old=Code(f);
        await AssertFailure(429,()=>Service.Send(f.Request,f.Actor,default));
        await Sql(f,"UPDATE ordering.preorder_pickup_otps SET issued_at=issued_at-interval '61 seconds' WHERE request_id=@request");
        await Service.Send(f.Request,f.Actor,default);
        await AssertFailure(401,()=>Service.Open(f.Kiosk,old,Guid.NewGuid(),default));
        await Service.Open(f.Kiosk,Code(f),Guid.NewGuid(),default);
        await AssertFailure(409,()=>Service.Send(f.Request,f.Actor,default));
        var failed=await Setup(); factory.EmailSender.ShouldFail=true;
        try { await AssertFailure(503,()=>Service.Send(failed.Request,failed.Actor,default)); }
        finally { factory.EmailSender.ShouldFail=false; }
        Assert.Equal("FAILED",await Sql(failed,"SELECT status FROM ordering.preorder_pickup_otps WHERE request_id=@request"));
    }
    [Fact]
    public async Task CodesAreKioskBoundAndFiveWrongCodesBlockFurtherAttempts()
    {
        var f=await Setup(); await Service.Send(f.Request,f.Actor,default);
        var other=await Setup(); await AssertFailure(401,()=>Service.Open(other.Kiosk,Code(f),Guid.NewGuid(),default));
        for(var n=0;n<5;n++) await AssertFailure(401,()=>Service.Open(f.Kiosk,"not-valid",Guid.NewGuid(),default));
        await AssertFailure(429,()=>Service.Open(f.Kiosk,Code(f),Guid.NewGuid(),default));
        Assert.Equal(0L,await Sql(f,"SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@order"));
    }
    [Fact]
    public async Task SellerAndDeviceApiEnforceOwnershipAndDoNotReturnCode()
    {
        var f=await Setup(); var other=await Setup();
        using var seller=factory.CreateClient();
        seller.DefaultRequestHeaders.Authorization=new("Bearer",factory.Token("SELLER",userId:f.Actor,sellerId:f.Seller));
        Assert.Equal(HttpStatusCode.NotFound,(await seller.PostAsync($"/api/me/preorders/{other.Request}/pickup-otp",null)).StatusCode);
        var response=await seller.PostAsync($"/api/me/preorders/{f.Request}/pickup-otp",null);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.DoesNotContain(Code(f),await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var customer=factory.CreateClient();
        customer.DefaultRequestHeaders.Authorization=new("Bearer",factory.Token("CUSTOMER",userId:f.Customer));
        Assert.Equal(HttpStatusCode.Forbidden,(await customer.PostAsync($"/api/me/preorders/{f.Request}/pickup-otp",null)).StatusCode);
        using var device=factory.CreateClient(); device.DefaultRequestHeaders.Add("X-Kiosk-Key",f.Key);
        var input=new PreorderPickupOtpOpenRequest(Code(f),Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden,(await device.PostAsJsonAsync($"/api/kiosks/{other.Kiosk}/preorders/pickup-otp/open",input)).StatusCode);
        var opened=await device.PostAsJsonAsync($"/api/kiosks/{f.Kiosk}/preorders/pickup-otp/open",input);
        Assert.Equal(HttpStatusCode.OK,opened.StatusCode);
        var body=await opened.Content.ReadFromJsonAsync<PreorderPickupOtpOpened>();
        device.DefaultRequestHeaders.Add("X-Pickup-Token",body!.WatchToken);
        Assert.Equal(HttpStatusCode.OK,(await device.GetAsync($"/api/kiosks/{f.Kiosk}/preorders/pickup-otp/attempts/{input.AttemptId}")).StatusCode);
    }
    [Fact]
    public async Task ReceiptCannotBypassEmailOtpButStillWorksForWalkIn()
    {
        var f=await Setup();
        await Assert.ThrowsAsync<PostgresException>(()=>Sql(f,"SELECT flow.request_pickup(@order,@order::text,@kiosk)"));
        Assert.Equal(0L,await Sql(f,"SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@order"));
        await Sql(f,"DELETE FROM ordering.web_requests WHERE id=@request; UPDATE kiosk_ops.slots SET zone='WALK_IN' WHERE id=@slot");
        Assert.IsType<Guid>(await Sql(f,"SELECT flow.request_pickup(@order,@order::text,@kiosk)"));
    }
    [Fact]
    public async Task SendLimitIsPersistedAndCommandNeverOutlivesPickupWindow()
    {
        var f=await Setup();
        await Service.Send(f.Request,f.Actor,default);
        Assert.Equal(true,await Sql(f,"SELECT otp.expires_at=w.pickup_before FROM ordering.preorder_pickup_otps otp JOIN ordering.web_requests w ON w.id=otp.request_id WHERE w.id=@request"));
        await Sql(f,"UPDATE ordering.preorder_pickup_otps SET issued_at=issued_at-interval '61 seconds',send_count=5 WHERE request_id=@request");
        await AssertFailure(429,()=>Service.Send(f.Request,f.Actor,default));
        await Sql(f,"UPDATE ordering.web_requests SET pickup_before=public.app_now()+interval '30 seconds' WHERE id=@request");
        await Service.Open(f.Kiosk,Code(f),Guid.NewGuid(),default);
        Assert.Equal(true,await Sql(f,"SELECT t.expires_at<=w.pickup_before FROM kiosk_ops.unlock_tokens t JOIN ordering.web_requests w ON w.order_id=t.order_id WHERE w.id=@request"));
    }
}
