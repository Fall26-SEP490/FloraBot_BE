using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class CustomerPrivacyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Kiosk = "40000000-0000-0000-0000-000000000001";
    private const string History = "/api/kiosks/" + Kiosk + "/customer/history";
    private const string Forget = "/api/kiosks/" + Kiosk + "/flows/forget_customer";

    [Fact]
    public async Task OnlyCustomerSessionsCanReadOrForget()
    {
        using var client = factory.CreateClient();
        foreach (var role in new[] { "", "ADMIN", "SELLER" })
        {
            client.DefaultRequestHeaders.Authorization = role == "" ? null : new("Bearer", factory.Token(role));
            var expected = role == "" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            var read = await client.GetAsync(History);
            Assert.Equal(expected, read.StatusCode); Assert.True(read.Headers.CacheControl?.NoStore);
            var forget = await client.PostAsJsonAsync(Forget, new { });
            Assert.Equal(expected, forget.StatusCode); Assert.True(forget.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task CustomerHistoryIsPrivatePaginatedAndErasureRevokesExistingSession()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var first = Guid.NewGuid(); var other = Guid.NewGuid();
        await using var fixture = data.CreateCommand("""
            INSERT INTO identity.users(id,phone,email,full_name,role,loyalty_points)
            VALUES(@first,'0300000001',@first::text||'@example.invalid','Privacy fixture A','CUSTOMER',123),
                  (@other,'0300000002',@other::text||'@example.invalid','Privacy fixture B','CUSTOMER',456);
            INSERT INTO ordering.orders(order_code,checkout_id,customer_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token,ecard_content,created_at)
            SELECT @first::text||'-'||n,public.uuid_v7(),@first,'20000000-0000-0000-0000-000000000001',
              '40000000-0000-0000-0000-000000000001','CANCELLED',100000,100000,flow.new_tracking_token(),'PRIVATE CARD',public.app_now()+n*interval '1 second'
            FROM generate_series(1,26) n;
            INSERT INTO ordering.orders(order_code,checkout_id,customer_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token)
            VALUES(@other::text,public.uuid_v7(),@other,'20000000-0000-0000-0000-000000000001',
              '40000000-0000-0000-0000-000000000001','CANCELLED',200000,200000,flow.new_tracking_token());
            INSERT INTO ordering.order_items(order_id,item_type,accessory_id,name_snapshot,quantity,unit_price,line_total)
            SELECT id,'ACCESSORY',public.uuid_v7(),'Thiệp hoa thử nghiệm',1,100000,100000 FROM ordering.orders WHERE order_code=@first::text||'-26';
            """);
        fixture.Parameters.AddWithValue("first", first); fixture.Parameters.AddWithValue("other", other);
        await fixture.ExecuteNonQueryAsync();
        try
        {
            var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = first, Role = "CUSTOMER", FullName = "Privacy fixture A" }));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(History)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = first, Role = "CUSTOMER", FullName = "Privacy fixture A" }, kioskId: Kiosk));
            var response = await client.GetAsync(History); response.EnsureSuccessStatusCode();
            Assert.True(response.Headers.CacheControl?.NoStore);
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(other.ToString(), json); Assert.DoesNotContain("PRIVATE CARD", json);
            Assert.DoesNotContain("trackingToken", json); Assert.DoesNotContain("0300000001", json);
            using var page = JsonDocument.Parse(json);
            Assert.Equal(123, page.RootElement.GetProperty("loyaltyPoints").GetInt64());
            Assert.True(page.RootElement.GetProperty("hasMore").GetBoolean());
            Assert.Equal(25, page.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(first + "-26", page.RootElement.GetProperty("items")[0].GetProperty("orderCode").GetString());
            Assert.Equal("Thiệp hoa thử nghiệm", page.RootElement.GetProperty("items")[0].GetProperty("items")[0].GetString());
            Assert.False(string.IsNullOrWhiteSpace(page.RootElement.GetProperty("items")[0].GetProperty("shopName").GetString()));
            var second = await client.GetFromJsonAsync<JsonElement>(History + "?page=2");
            Assert.Single(second.GetProperty("items").EnumerateArray());
            Assert.False(second.GetProperty("hasMore").GetBoolean());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(History + "?page=0")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(History.Replace(Kiosk, Guid.NewGuid().ToString()))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Forget.Replace(Kiosk, Guid.NewGuid().ToString()), new { })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Forget, new { p_customer = other })).StatusCode);
            var forgotten = await client.PostAsJsonAsync(Forget, new { }); forgotten.EnsureSuccessStatusCode();
            Assert.True(forgotten.Headers.CacheControl?.NoStore);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(History)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Forget, new { })).StatusCode);
            await using var state = data.CreateCommand("""
                SELECT phone IS NULL AND email='deleted-'||id||'@florabot.invalid' AND full_name='Khách đã xóa' AND status='DISABLED' AND loyalty_points=0
                FROM identity.users WHERE id=@first;
                """);
            state.Parameters.AddWithValue("first", first); Assert.Equal(true, await state.ExecuteScalarAsync());
            await using var preserved = data.CreateCommand("SELECT count(*) FROM ordering.orders WHERE customer_id=@first");
            preserved.Parameters.AddWithValue("first", first); Assert.Equal(26L, await preserved.ExecuteScalarAsync());
            await using var audit = data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE action='CUSTOMER_FORGOTTEN' AND actor_id=@first AND entity_id=@first");
            audit.Parameters.AddWithValue("first", first); Assert.Equal(1L, await audit.ExecuteScalarAsync());
            client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = other, Role = "CUSTOMER", FullName = "Privacy fixture B" }, kioskId: Kiosk));
            var otherPage = await client.GetFromJsonAsync<JsonElement>(History);
            Assert.Equal(456, otherPage.GetProperty("loyaltyPoints").GetInt64());
            Assert.Single(otherPage.GetProperty("items").EnumerateArray());
        }
        finally
        {
            await using var cleanup = data.CreateCommand("DELETE FROM ordering.order_items WHERE order_id IN (SELECT id FROM ordering.orders WHERE customer_id=ANY(@ids)); DELETE FROM ordering.orders WHERE customer_id=ANY(@ids); DELETE FROM identity.users WHERE id=ANY(@ids)");
            cleanup.Parameters.AddWithValue("ids", new[] { first, other }); await cleanup.ExecuteNonQueryAsync();
        }
    }
}
