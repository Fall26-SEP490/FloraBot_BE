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
    public async Task CustomerCannotOpenShopDirectlyViaClosedLegacyEndpoint()
    {
        await using var app = new ApiFactory();
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var user = Guid.NewGuid();
        await using var insert = source.CreateCommand("INSERT INTO identity.users(id,email,full_name,password_hash,role,loyalty_points) VALUES(@id,@email,'Direct opener','!unprovisioned','CUSTOMER',5000)");
        insert.Parameters.AddWithValue("id", user);
        insert.Parameters.AddWithValue("email", $"direct-{user:N}@example.invalid");
        await insert.ExecuteNonQueryAsync();

        await using var command = source.CreateCommand("SELECT flow.open_member_shop(@user,'Direct flower shop','0901234567','HCM')");
        command.Parameters.AddWithValue("user", user);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
        Assert.Equal("42501", ex.SqlState);
        Assert.Contains("ngừng hỗ trợ", ex.MessageText);

        // Verify existing seller retains idempotent access
        var sellerId = Guid.NewGuid();
        await using var makeSeller = source.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,address,status,package_id,package_expires_at)
            VALUES(@sid,'Existing shop','0901234567','HCM','ACTIVE',
                   (SELECT id FROM identity.subscription_packages ORDER BY id LIMIT 1),
                   CURRENT_DATE + interval '1 year');
            UPDATE identity.users SET role='SELLER',seller_id=@sid WHERE id=@uid;
            """);
        makeSeller.Parameters.AddWithValue("sid", sellerId);
        makeSeller.Parameters.AddWithValue("uid", user);
        await makeSeller.ExecuteNonQueryAsync();

        await using var retry = source.CreateCommand("SELECT flow.open_member_shop(@user,'Direct flower shop','0901234567','HCM')");
        retry.Parameters.AddWithValue("user", user);
        var returnedId = (Guid)(await retry.ExecuteScalarAsync())!;
        Assert.Equal(sellerId, returnedId);
    }

    [Fact]
    public async Task LegacyHttpMemberShopReturnsGoneForCustomerAndPreservesSeller()
    {
        await using var app = new ApiFactory();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var email = $"onboarding-{Guid.NewGuid():N}@example.invalid";
        var registration = await app.RegisterAndVerifyAsync(client, "Future shop owner", email, "Member-shop-password-2026");
        registration.EnsureSuccessStatusCode();
        var original = (await registration.Content.ReadFromJsonAsync<SessionResponse>())!;
        void UseCookie(HttpResponseMessage response)
        {
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_access=")).Split(';')[0]);
        }
        UseCookie(registration);
        client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5173");

        var input = new { shopName = "Member flower shop", phone = "0901234567", address = "HCM" };
        var response = await client.PostAsJsonAsync("/api/member/shop", input);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);

        // Verify user role remains CUSTOMER
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var check = source.CreateCommand("SELECT role, seller_id FROM identity.users WHERE id=@id");
        check.Parameters.AddWithValue("id", original.Id);
        await using (var reader = await check.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("CUSTOMER", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
        }
    }
}
