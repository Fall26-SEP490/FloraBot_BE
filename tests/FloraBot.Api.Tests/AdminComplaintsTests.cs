using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Modules.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class AdminComplaintsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData(0)]
    [InlineData(100000)]
    public async Task AdminReviewsAndResolvesComplaintWithSourceRules(long refund)
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var order = Guid.NewGuid(); var kiosk = Guid.NewGuid();
        await using var fixture = data.CreateCommand("""
            INSERT INTO ordering.orders(id,order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token,completed_at)
            VALUES(@id,@id::text,@id,'20000000-0000-0000-0000-000000000001',@kiosk,'COMPLETED',350000,350000,flow.new_tracking_token(),public.app_now());
            INSERT INTO payment.payments(kind,purpose,checkout_id,gateway,idempotency_key,amount,status,paid_at)
            VALUES('CHARGE','ORDER_CHECKOUT',@id,'MANUAL',@id::text,350000,'SUCCEEDED',public.app_now());
            INSERT INTO ordering.order_items(order_id,item_type,bouquet_id,slot_id,name_snapshot,quantity,unit_price,line_total)
            VALUES(@id,'BOUQUET',@id,@id,'Complaint fixture flowers',1,350000,350000);
            SELECT flow.open_dispute(@id,(SELECT tracking_token FROM ordering.orders WHERE id=@id),'Flowers damaged at pickup','https://example.invalid/complaint.jpg');
            """);
        fixture.Parameters.AddWithValue("id", order); fixture.Parameters.AddWithValue("kiosk", kiosk);
        var id = (Guid)(await fixture.ExecuteScalarAsync())!;
        using var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        using var seller = factory.CreateClient(); seller.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        using var anonymous = factory.CreateClient();
        var path = $"/api/admin/complaints?kioskId={kiosk}";
        var detailPath = $"/api/admin/complaints/{id}";
        foreach (var url in new[] { path, detailPath })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await seller.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        }
        var response = await admin.GetAsync(detailPath);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("trackingToken", json); Assert.DoesNotContain("refundBank", json); Assert.DoesNotContain("customerId", json);
        var detail = (await response.Content.ReadFromJsonAsync<AdminComplaintDetail>())!;
        Assert.Equal(order, detail.Complaint.OrderId); Assert.Equal(350000, detail.Complaint.OrderTotal);
        Assert.Equal("Complaint fixture flowers", Assert.Single(detail.Items).Name);
        Assert.Equal("https://example.invalid/complaint.jpg", Assert.Single(detail.Evidence.Items).Url);
        Assert.Single((await admin.GetFromJsonAsync<AdminComplaintPage>(path))!.Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path + "&page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path + "&status=RESOLVED_FIXED")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/complaints/{Guid.NewGuid()}")).StatusCode);
        var body = new { p_dispute = id, p_refund = refund, p_decision = "Reviewed the customer evidence" };
        const string resolve = "/api/admin/flows/resolve_dispute";
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsJsonAsync(resolve, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(resolve, new { p_dispute = id, p_refund = 350001, body.p_decision })).StatusCode);
        (await admin.PostAsJsonAsync(resolve, body)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(resolve, body)).StatusCode);
        var status = refund == 0 ? "RESOLVED_REJECT" : "RESOLVED_REFUND";
        var closed = (await admin.GetFromJsonAsync<AdminComplaintDetail>(detailPath))!;
        Assert.Equal(status, closed.Complaint.Status); Assert.Equal(body.p_decision, closed.Complaint.Decision);
        Assert.Single((await admin.GetFromJsonAsync<AdminComplaintPage>(path + "&status=" + status))!.Items);
        await using var verify = data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE entity_id=@id AND actor_id='10000000-0000-0000-0000-000000000001' AND action=@action");
        verify.Parameters.AddWithValue("id", id); verify.Parameters.AddWithValue("action", refund == 0 ? "DISPUTE_REJECTED" : "DISPUTE_REFUND_APPROVED");
        Assert.Equal(1L, await verify.ExecuteScalarAsync());
        await using var history = data.CreateCommand("""
            WITH orders AS (
                INSERT INTO ordering.orders(order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token,completed_at)
                SELECT @kiosk::text || '-' || n,public.uuid_v7(),'20000000-0000-0000-0000-000000000001',@kiosk,'DISPUTED',1,1,flow.new_tracking_token(),public.app_now()
                FROM generate_series(1,26) n RETURNING id,kiosk_id
            )
            INSERT INTO ordering.disputes(kind,order_id,kiosk_id,reason)
            SELECT 'COMPLAINT',id,kiosk_id,'Pagination fixture' FROM orders;
            """);
        history.Parameters.AddWithValue("kiosk", kiosk); await history.ExecuteNonQueryAsync();
        var first = (await admin.GetFromJsonAsync<AdminComplaintPage>(path))!;
        var second = (await admin.GetFromJsonAsync<AdminComplaintPage>(path + "&page=2"))!;
        Assert.Equal(25, first.Items.Count); Assert.True(first.HasMore);
        Assert.Single(second.Items); Assert.False(second.HasMore);
        Assert.DoesNotContain(second.Items[0].Id, first.Items.Select(x => x.Id));
    }
}
