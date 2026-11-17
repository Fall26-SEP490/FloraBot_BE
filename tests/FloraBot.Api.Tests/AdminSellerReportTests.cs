using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Payment;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class AdminSellerReportTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task GroupedPaymentIsAllocatedOnceAndOrderHistoryUsesOrderStatus()
    {
        var ids = "seller other kiosk second foreign checkout charge first secondOrder foreignOrder cancelled"
            .Split(' ').ToDictionary(x => x, _ => Guid.NewGuid());
        var db = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var seed = db.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id)
            VALUES(@seller,'Report seller','0900000001','APPROVED','20000000-0000-0000-0000-00000000000a'),
                  (@other,'Other report seller','0900000002','APPROVED','20000000-0000-0000-0000-00000000000a');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            SELECT id,id::text,'Report kiosk','Test address','HCM',id::text,id::text,'OFFLINE'
            FROM unnest(ARRAY[@kiosk,@second,@foreign]) x(id);
            INSERT INTO ordering.orders(id,order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token)
            SELECT id,id::text,checkout,seller,kiosk,status,amount,amount,id::text FROM (
                VALUES (@first,@checkout,@seller,@kiosk,'PAID',100),
                       (@secondOrder,@checkout,@seller,@second,'PAID',200),
                       (@foreignOrder,@checkout,@other,@foreign,'PAID',700),
                       (@cancelled,@cancelled,@seller,@kiosk,'CANCELLED',400)
            ) x(id,checkout,seller,kiosk,status,amount);
            INSERT INTO payment.payments(id,kind,purpose,checkout_id,gateway,idempotency_key,amount,status,paid_at)
            VALUES(@charge,'CHARGE','ORDER_CHECKOUT',@checkout,'MANUAL',@charge::text,1000,'SUCCEEDED',now());
            INSERT INTO payment.payments(kind,purpose,checkout_id,order_id,parent_payment_id,gateway,idempotency_key,amount,status,paid_at,approved_by)
            VALUES('REFUND','ORDER_CHECKOUT',@checkout,@first,@charge,'MANUAL','refund-'||@first::text,50,'SUCCEEDED',now(),'10000000-0000-0000-0000-000000000001');
            """);
        foreach (var (key, value) in ids) seed.Parameters.AddWithValue(key, value);
        await seed.ExecuteNonQueryAsync();

        using var client = factory.CreateClient();
        var history = $"/api/admin/sellers/{ids["seller"]}/orders";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(history)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var report = await client.GetFromJsonAsync<SellerSalesReportResponse>($"/api/sellers/{ids["seller"]}/reports/sales");
        Assert.NotNull(report);
        Assert.Equal(300, report.GrossPaid);
        Assert.Equal(50, report.TotalRefunded);
        Assert.Equal(250, report.NetReceipts);
        Assert.Equal(2, report.PaidOrders);
        Assert.Equal(300, report.ByKiosk.Sum(k => k.GrossPaid));
        var month = DateTime.UtcNow.AddHours(7).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var filtered = await client.GetFromJsonAsync<SellerSalesReportResponse>(
            $"/api/sellers/{ids["seller"]}/reports/sales?month={month}&kioskId={ids["kiosk"]}");
        Assert.Equal(100, filtered!.GrossPaid);
        Assert.Equal(50, filtered.NetReceipts);
        using var orders = await client.GetFromJsonAsync<JsonDocument>(history + "?status=CANCELLED");
        Assert.Equal(ids["cancelled"], Assert.Single(orders!.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using var detail = await client.GetFromJsonAsync<JsonDocument>(history + "/" + ids["first"]);
        Assert.Equal(ids["kiosk"], detail!.RootElement.GetProperty("kioskId").GetGuid());
        Assert.Equal(2, detail.RootElement.GetProperty("payments").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(history + "/" + ids["foreignOrder"])).StatusCode);
    }
}
