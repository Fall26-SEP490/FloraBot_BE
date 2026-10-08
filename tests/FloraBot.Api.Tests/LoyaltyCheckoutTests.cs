using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Payment;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class LoyaltyCheckoutTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedemptionIsAtomicCappedPerSellerAndRestoredOnce(bool shopOwner)
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var customer = Guid.NewGuid(); var kiosk = Guid.NewGuid();
        var sellers = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var products = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var slots = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var bouquets = new List<Guid>();
        var key = "loyalty-fixture-" + kiosk;
        await using var setup = source.CreateCommand("""
            INSERT INTO identity.users(id,email,full_name,role,loyalty_points)
            VALUES(@customer,@customer::text||'@example.invalid','Loyalty fixture','CUSTOMER',300000);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,api_key_hash,last_heartbeat_at)
            VALUES(@kiosk,@kiosk::text,'Loyalty fixture','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE',@hash,CURRENT_TIMESTAMP);
            """);
        setup.Parameters.AddWithValue("customer", customer); setup.Parameters.AddWithValue("kiosk", kiosk);
        setup.Parameters.AddWithValue("hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant());
        await setup.ExecuteNonQueryAsync();
        for (var index = 0; index < 2; index++)
        {
            await using var stock = source.CreateCommand("""
                INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
                VALUES(@seller,'Same shop name','0901112233','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
                INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status)
                VALUES(@product,@seller,'Loyalty bouquet',@price,48,'ACTIVE');
                INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel) VALUES(@slot,@kiosk,@code,@relay);
                SELECT flow.assign_slot(@slot,@seller,'10000000-0000-0000-0000-000000000001');
                SELECT flow.stock_bouquet(flow.new_batch(),@product,@slot,'10000000-0000-0000-0000-000000000001',@qr);
                """);
            stock.Parameters.AddWithValue("seller", sellers[index]); stock.Parameters.AddWithValue("product", products[index]);
            stock.Parameters.AddWithValue("slot", slots[index]); stock.Parameters.AddWithValue("kiosk", kiosk);
            stock.Parameters.AddWithValue("price", index == 0 ? 400000L : 200000L);
            stock.Parameters.AddWithValue("code", "A0" + index); stock.Parameters.AddWithValue("relay", index);
            stock.Parameters.AddWithValue("qr", products[index].ToString());
            await stock.ExecuteNonQueryAsync();
            await using var find = source.CreateCommand("SELECT bouquet_id FROM kiosk_ops.slots WHERE id=@id");
            find.Parameters.AddWithValue("id", slots[index]); bouquets.Add((Guid)(await find.ExecuteScalarAsync())!);
        }
        using var client = factory.CreateClient();
        var member = new User { Id = customer, Role = "CUSTOMER", FullName = "Loyalty fixture" };
        if (shopOwner)
        {
            member.Role = "SELLER"; member.SellerId = Guid.Parse("20000000-0000-0000-0000-000000000001"); member.PasswordHash = "!unprovisioned";
            await using var promote = source.CreateCommand("UPDATE identity.users SET role='SELLER',seller_id=@seller,password_hash=@hash WHERE id=@id");
            promote.Parameters.AddWithValue("seller", member.SellerId.Value); promote.Parameters.AddWithValue("hash", member.PasswordHash);
            promote.Parameters.AddWithValue("id", customer); await promote.ExecuteNonQueryAsync();
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", scope.ServiceProvider.GetRequiredService<TokenService>()
            .Issue(member, kioskId: kiosk.ToString()));
        var path = $"/api/kiosks/{kiosk}/flows/kiosk_checkout";
        var card = string.Concat(Enumerable.Repeat("🌼", 150));
        var longCard = await client.PostAsJsonAsync(path, new { p_bouquets = bouquets, p_ecard = card + "a" });
        Assert.Equal(HttpStatusCode.Conflict, longCard.StatusCode);
        Assert.Contains("Lời thiệp tối đa 150 ký tự", (await longCard.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        var pointsPath = $"/api/kiosks/{kiosk}/customer/points";
        var pointsResponse = await client.GetAsync(pointsPath); pointsResponse.EnsureSuccessStatusCode();
        Assert.True(pointsResponse.Headers.CacheControl?.NoStore);
        var pointsInfo = (await pointsResponse.Content.ReadFromJsonAsync<FloraBot.Api.Modules.Identity.CustomerPointsResponse>())!;
        Assert.Equal(300000, pointsInfo.LoyaltyPoints);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/kiosks/{Guid.NewGuid()}/customer/points")).StatusCode);
        var negative = await client.PostAsJsonAsync(path, new { p_bouquets = bouquets, p_points = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.True((await negative.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("p_points", out _));

        using var device = factory.CreateClient(); device.DefaultRequestHeaders.Add("X-Kiosk-Key", key);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync(pointsPath)).StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(pointsPath)).StatusCode);
        using var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(pointsPath)).StatusCode);
        var guest = await device.PostAsJsonAsync(path, new { p_bouquets = bouquets, p_points = 1 });
        Assert.Equal(HttpStatusCode.Conflict, guest.StatusCode);
        Assert.Contains("Đổi điểm cần đăng nhập", (await guest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        await using var capQuery = source.CreateCommand("SELECT floor(400000 * flow.cfg('points_max_redeem_percent') / 100)::bigint");
        var cap = (long)(await capQuery.ExecuteScalarAsync())!;
        Assert.Equal(cap, (long)Math.Floor(400000 * pointsInfo.MaxRedeemPercent / 100));
        var excessive = await client.PostAsJsonAsync(path, new { p_bouquets = bouquets, p_points = cap + 1 });
        Assert.Equal(HttpStatusCode.Conflict, excessive.StatusCode);
        Assert.Contains("Đổi điểm tối đa", (await excessive.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        await using var untouched = source.CreateCommand("""
            SELECT (SELECT loyalty_points=300000 FROM identity.users WHERE id=@customer)
              AND NOT EXISTS(SELECT 1 FROM ordering.orders WHERE kiosk_id=@kiosk)
              AND (SELECT count(*)=2 FROM kiosk_ops.slots WHERE kiosk_id=@kiosk AND status='STOCKED');
            """);
        untouched.Parameters.AddWithValue("customer", customer); untouched.Parameters.AddWithValue("kiosk", kiosk);
        Assert.Equal(true, await untouched.ExecuteScalarAsync());

        var redeem = Math.Min(cap, 150000L); Assert.True(redeem > 0);
        await using var balance = source.CreateCommand("UPDATE identity.users SET loyalty_points=@balance WHERE id=@id");
        balance.Parameters.AddWithValue("id", customer); balance.Parameters.AddWithValue("balance", 0L);
        await balance.ExecuteNonQueryAsync();
        var insufficient = await client.PostAsJsonAsync(path, new { p_bouquets = bouquets, p_points = redeem });
        Assert.Equal(HttpStatusCode.Conflict, insufficient.StatusCode);
        Assert.Contains("điểm", (await insufficient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        balance.Parameters["balance"].Value = 300000L; await balance.ExecuteNonQueryAsync();
        Assert.Equal(true, await untouched.ExecuteScalarAsync());
        var response = await client.PostAsJsonAsync(path, new { p_bouquets = bouquets, p_points = redeem, p_ecard = card });
        response.EnsureSuccessStatusCode(); Assert.True(response.Headers.CacheControl?.NoStore);
        var checkout = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        var payment = await client.GetFromJsonAsync<CheckoutResponse>($"/api/kiosks/{kiosk}/checkouts/{checkout}");
        Assert.Equal(600000 - redeem, payment!.Amount); Assert.Equal(2, payment.Orders.Count);
        Assert.Equal(redeem, payment.Orders.Sum(x => x.PointsRedeemed));
        Assert.Equal(redeem, payment.Orders.Sum(x => x.DiscountAmount));
        await using var cardState = source.CreateCommand("SELECT count(*) FROM ordering.orders WHERE checkout_id=@checkout AND ecard_content=@card");
        cardState.Parameters.AddWithValue("checkout", checkout); cardState.Parameters.AddWithValue("card", card);
        Assert.Equal(2L, await cardState.ExecuteScalarAsync());
        Assert.Equal(300000 - redeem, (await client.GetFromJsonAsync<FloraBot.Api.Modules.Identity.CustomerPointsResponse>(pointsPath))!.LoyaltyPoints);
        await using var state = source.CreateCommand("""
            SELECT (SELECT loyalty_points=300000-@points FROM identity.users WHERE id=@customer)
              AND (SELECT count(*)=1 FROM ordering.orders WHERE checkout_id=@checkout AND seller_id=@seller AND discount_amount=@points AND points_redeemed=@points AND total_amount=400000-@points)
              AND (SELECT count(*)=1 FROM ordering.orders WHERE checkout_id=@checkout AND seller_id<>@seller AND discount_amount=0 AND points_redeemed=0 AND total_amount=200000);
            """);
        state.Parameters.AddWithValue("points", redeem); state.Parameters.AddWithValue("customer", customer);
        state.Parameters.AddWithValue("checkout", checkout); state.Parameters.AddWithValue("seller", sellers[0]);
        Assert.Equal(true, await state.ExecuteScalarAsync());

        // Target this checkout only: a global expiry sweep could affect concurrent fixtures.
        await using var expire = source.CreateCommand("""
            UPDATE payment.payments SET status='EXPIRED' WHERE checkout_id=@checkout AND status='PENDING';
            SELECT flow.release_checkout(@checkout,'EXPIRED');
            SELECT flow.release_checkout(@checkout,'EXPIRED');
            """);
        expire.Parameters.AddWithValue("checkout", checkout); await expire.ExecuteNonQueryAsync();
        await using var restored = source.CreateCommand("""
            SELECT (SELECT loyalty_points=300000 FROM identity.users WHERE id=@customer)
              AND (SELECT count(*)=2 FROM ordering.orders WHERE checkout_id=@checkout AND status='EXPIRED')
              AND (SELECT count(*)=2 FROM kiosk_ops.slots WHERE kiosk_id=@kiosk AND status='STOCKED' AND hold_order_id IS NULL);
            """);
        restored.Parameters.AddWithValue("customer", customer); restored.Parameters.AddWithValue("checkout", checkout);
        restored.Parameters.AddWithValue("kiosk", kiosk); Assert.Equal(true, await restored.ExecuteScalarAsync());
    }
}
