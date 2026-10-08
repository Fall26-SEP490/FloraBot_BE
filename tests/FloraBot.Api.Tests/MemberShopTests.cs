using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Modules.Ordering;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class MemberShopTests
{
    [Fact]
    public async Task ConcurrentShopRequestsCreateOneShopAndOneAudit()
    {
        await using var app = new ApiFactory();
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var user = Guid.NewGuid();
        await using var insert = source.CreateCommand("INSERT INTO identity.users(id,email,full_name,password_hash,role,loyalty_points) VALUES(@id,@email,'Concurrent owner','!unprovisioned','CUSTOMER',5000)");
        insert.Parameters.AddWithValue("id", user);
        insert.Parameters.AddWithValue("email", $"concurrent-{user:N}@example.invalid");
        await insert.ExecuteNonQueryAsync();
        async Task<Guid> Open()
        {
            await using var command = source.CreateCommand("SELECT flow.open_member_shop(@user,'Concurrent flower shop','0901234567','HCM')");
            command.Parameters.AddWithValue("user", user);
            return (Guid)(await command.ExecuteScalarAsync())!;
        }
        var shops = await Task.WhenAll(Open(), Open());
        Assert.Equal(shops[0], shops[1]);
        await using var check = source.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE actor_id=@user AND action='MEMBER_SHOP_OPENED'");
        check.Parameters.AddWithValue("user", user);
        Assert.Equal(1L, await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task OpeningShopPreservesIdentityPointsAndRetriesWithoutDuplicates()
    {
        await using var app = new ApiFactory();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var email = $"onboarding-{Guid.NewGuid():N}@example.invalid";
        var registration = await client.PostAsJsonAsync("/api/auth/register", new { fullName = "Future shop owner", email, password = "Member-shop-password-2026" });
        registration.EnsureSuccessStatusCode();
        var original = (await registration.Content.ReadFromJsonAsync<SessionResponse>())!;
        void UseCookie(HttpResponseMessage response)
        {
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_access=")).Split(';')[0]);
        }
        UseCookie(registration);
        client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5173");
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var points = source.CreateCommand("UPDATE identity.users SET loyalty_points=12000 WHERE id=@id");
        points.Parameters.AddWithValue("id", original.Id);
        await points.ExecuteNonQueryAsync();
        var previousRequest = Guid.NewGuid();
        await using var previous = source.CreateCommand("""
            INSERT INTO ordering.web_requests(id,customer_id,seller_id,product_id,kiosk_id,kind,instructions,pickup_at,pickup_before,state)
            VALUES(@request,@user,gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),'CUSTOM','Historical customer request',
              public.app_now()+interval '1 day',public.app_now()+interval '2 days','REQUESTED')
            """);
        previous.Parameters.AddWithValue("request", previousRequest);
        previous.Parameters.AddWithValue("user", original.Id);
        await previous.ExecuteNonQueryAsync();
        var input = new { shopName = "Member flower shop", phone = "0901234567", address = "HCM" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/member/shop", input with { phone = "invalid" })).StatusCode);
        var opened = await client.PostAsJsonAsync("/api/member/shop", input);
        opened.EnsureSuccessStatusCode();
        var owner = (await opened.Content.ReadFromJsonAsync<SessionResponse>())!;
        Assert.Equal(original.Id, owner.Id);
        Assert.Equal("SELLER", owner.Role);
        Assert.Equal("PENDING", owner.SellerStatus);
        Assert.NotNull(owner.SellerId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/member/profile")).StatusCode);
        UseCookie(opened);
        var profile = (await client.GetFromJsonAsync<MemberProfile>("/api/member/profile"))!;
        Assert.Equal(12000, profile.LoyaltyPoints);
        Assert.Equal(email, profile.Email);
        var history = await client.GetFromJsonAsync<CustomerHistoryPage>("/api/member/history");
        Assert.Equal(12000, history!.LoyaltyPoints);
        var requests = await client.GetFromJsonAsync<WebRequestPage>("/api/member/preorders");
        Assert.Equal(previousRequest, Assert.Single(requests!.Items).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/preorders")).StatusCode);
        var retry = await client.PostAsJsonAsync("/api/member/shop", input);
        retry.EnsureSuccessStatusCode();
        Assert.Equal(owner.SellerId, (await retry.Content.ReadFromJsonAsync<SessionResponse>())!.SellerId);
        await using var audit = source.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE actor_id=@id AND action='MEMBER_SHOP_OPENED'");
        audit.Parameters.AddWithValue("id", original.Id);
        Assert.Equal(1L, await audit.ExecuteScalarAsync());
        await using var earn = source.CreateCommand("SELECT flow.redeem_points(@user,1000,@order); SELECT flow.restore_points(@user,1000,@order); SELECT flow.credit_points(@user,500,@order)");
        earn.Parameters.AddWithValue("user", original.Id);
        earn.Parameters.AddWithValue("order", Guid.NewGuid());
        await earn.ExecuteNonQueryAsync();
        Assert.Equal(12500, (await client.GetFromJsonAsync<MemberProfile>("/api/member/profile"))!.LoyaltyPoints);
    }
}
