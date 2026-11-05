using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
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
        var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();

        var customer = Guid.NewGuid();
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();
        var sellerC = Guid.NewGuid();
        var kioskA = Guid.NewGuid();
        var kioskB = Guid.NewGuid();
        var slotA1 = Guid.NewGuid();
        var slotA2 = Guid.NewGuid();
        var slotB1 = Guid.NewGuid();
        var productA1 = Guid.NewGuid();
        var productA2 = Guid.NewGuid();
        var productB1 = Guid.NewGuid();
        var keyA = "loyalty-fixture-" + kioskA;
        var keyB = "loyalty-fixture-" + kioskB;
        var hashA = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keyA)));
        var hashB = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keyB)));

        await using var setup = source.CreateCommand("""
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
            VALUES (@sellerA, 'Shop A', '0901111111', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30),
                   (@sellerB, 'Shop B', '0902222222', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30),
                   (@sellerC, 'Shop C', '0903333333', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30);

            INSERT INTO identity.users(id, email, full_name, role, status, loyalty_points, seller_id, password_hash)
            VALUES (@customer, @customer::text || '@example.invalid', 'Loyalty fixture', @role, 'ACTIVE', 999999, @buyerSeller, '!unprovisioned');

            INSERT INTO identity.shop_loyalty_balances(customer_id, seller_id, balance, debt)
            VALUES (@customer, @sellerA, 300000, 0),
                   (@customer, @sellerB, 50000, 0);

            INSERT INTO payment.payment_channel_versions (
                id, seller_id, version, secret_ref, expected_bank_bin, expected_bank_account, expected_bank_name, state, verified_at, updated_at
            ) VALUES (
                public.uuid_v7(), @sellerA, 1, 'channel-sec-a', '970415', '1010099887766', 'Shop A Bank', 'ACTIVE', public.app_now(), public.app_now()
            ), (
                public.uuid_v7(), @sellerB, 1, 'channel-sec-b', '970422', '999988887777', 'Shop B Bank', 'ACTIVE', public.app_now(), public.app_now()
            );

            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status, api_key_hash, last_heartbeat_at)
            VALUES (@kioskA, @kioskA::text, 'Kiosk A', 'Address A', 'HCM', @kioskA::text, @kioskA::text, 'ONLINE', @hashA, CURRENT_TIMESTAMP),
                   (@kioskB, @kioskB::text, 'Kiosk B', 'Address B', 'HCM', @kioskB::text, @kioskB::text, 'ONLINE', @hashB, CURRENT_TIMESTAMP);

            INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel, zone)
            VALUES (@slotA1, @kioskA, 'A01', 0, 'WALK_IN'),
                   (@slotA2, @kioskA, 'A02', 1, 'WALK_IN'),
                   (@slotB1, @kioskB, 'B01', 0, 'WALK_IN');
            """);
        setup.Parameters.AddWithValue("sellerA", sellerA);
        setup.Parameters.AddWithValue("sellerB", sellerB);
        setup.Parameters.AddWithValue("sellerC", sellerC);
        setup.Parameters.AddWithValue("customer", customer);
        setup.Parameters.AddWithValue("role", shopOwner ? "SELLER" : "CUSTOMER");
        setup.Parameters.AddWithValue("buyerSeller", shopOwner ? (object)sellerC : DBNull.Value);
        setup.Parameters.AddWithValue("kioskA", kioskA);
        setup.Parameters.AddWithValue("kioskB", kioskB);
        setup.Parameters.AddWithValue("hashA", hashA);
        setup.Parameters.AddWithValue("hashB", hashB);
        setup.Parameters.AddWithValue("slotA1", slotA1);
        setup.Parameters.AddWithValue("slotA2", slotA2);
        setup.Parameters.AddWithValue("slotB1", slotB1);
        await setup.ExecuteNonQueryAsync();

        await CurrentMainKioskFixture.AssignKioskWithLeaseAsync(source, kioskA, sellerA);
        await CurrentMainKioskFixture.AssignKioskWithLeaseAsync(source, kioskB, sellerB);

        var bouquets = new List<Guid>();
        await using (var stock = source.CreateCommand("""
            INSERT INTO catalog.flower_products(id, seller_id, name, price, shelf_life_hours, status)
            VALUES (@productA1, @sellerA, 'Bouquet 1', 400000, 48, 'ACTIVE'),
                   (@productA2, @sellerA, 'Bouquet 2', 200000, 48, 'ACTIVE'),
                   (@productB1, @sellerB, 'Bouquet B', 150000, 48, 'ACTIVE');

            SELECT flow.stock_bouquet(flow.new_batch(), @productA1, @slotA1, '10000000-0000-0000-0000-000000000001', @qrA1);
            SELECT flow.stock_bouquet(flow.new_batch(), @productA2, @slotA2, '10000000-0000-0000-0000-000000000001', @qrA2);
            SELECT flow.stock_bouquet(flow.new_batch(), @productB1, @slotB1, '10000000-0000-0000-0000-000000000001', @qrB1);
            """))
        {
            stock.Parameters.AddWithValue("sellerA", sellerA);
            stock.Parameters.AddWithValue("sellerB", sellerB);
            stock.Parameters.AddWithValue("productA1", productA1);
            stock.Parameters.AddWithValue("productA2", productA2);
            stock.Parameters.AddWithValue("productB1", productB1);
            stock.Parameters.AddWithValue("slotA1", slotA1);
            stock.Parameters.AddWithValue("slotA2", slotA2);
            stock.Parameters.AddWithValue("slotB1", slotB1);
            stock.Parameters.AddWithValue("qrA1", productA1.ToString());
            stock.Parameters.AddWithValue("qrA2", productA2.ToString());
            stock.Parameters.AddWithValue("qrB1", productB1.ToString());
            await stock.ExecuteNonQueryAsync();
        }

        await using (var findBouquets = source.CreateCommand("SELECT bouquet_id FROM kiosk_ops.slots WHERE id IN (@slotA1, @slotA2) ORDER BY slot_code"))
        {
            findBouquets.Parameters.AddWithValue("slotA1", slotA1);
            findBouquets.Parameters.AddWithValue("slotA2", slotA2);
            await using var reader = await findBouquets.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                bouquets.Add(reader.GetGuid(0));
            }
        }
        Assert.Equal(2, bouquets.Count);

        var member = new User
        {
            Id = customer,
            Role = shopOwner ? "SELLER" : "CUSTOMER",
            FullName = "Loyalty fixture",
            SellerId = shopOwner ? sellerC : null,
            PasswordHash = "!unprovisioned",
            Status = "ACTIVE"
        };

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokenService.Issue(
            member,
            sellerStatus: shopOwner ? "ACTIVE" : null,
            kioskId: kioskA.ToString()));

        var path = $"/api/kiosks/{kioskA}/flows/kiosk_checkout";
        var card = string.Concat(Enumerable.Repeat("🌼", 150));
        var longCard = await client.PostAsJsonAsync(path, new { p_bouquets = bouquets, p_ecard = card + "a" });
        Assert.Equal(HttpStatusCode.Conflict, longCard.StatusCode);
        Assert.Contains("Lời thiệp tối đa 150 ký tự", (await longCard.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        var pointsPath = $"/api/kiosks/{kioskA}/customer/points";
        var pointsResponse = await client.GetAsync(pointsPath);
        pointsResponse.EnsureSuccessStatusCode();
        Assert.True(pointsResponse.Headers.CacheControl?.NoStore);
        var pointsInfo = (await pointsResponse.Content.ReadFromJsonAsync<CustomerPointsResponse>())!;
        Assert.Equal(300000, pointsInfo.LoyaltyPoints);

        await using var capQuery = source.CreateCommand("SELECT flow.cfg('points_max_redeem_percent')::numeric");
        var cfgMaxPercent = (decimal)(await capQuery.ExecuteScalarAsync())!;
        Assert.Equal(cfgMaxPercent, pointsInfo.MaxRedeemPercent);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/kiosks/{kioskB}/customer/points")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/kiosks/{Guid.NewGuid()}/customer/points")).StatusCode);

        var negative = await client.PostAsJsonAsync(path, new { p_bouquets = bouquets, p_points = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.True((await negative.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("p_points", out _));

        using var device = factory.CreateClient();
        device.DefaultRequestHeaders.Add("X-Kiosk-Key", keyA);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync(pointsPath)).StatusCode);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(pointsPath)).StatusCode);

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(pointsPath)).StatusCode);

        var guest = await device.PostAsJsonAsync(path, new { p_bouquets = bouquets, p_points = 1 });
        Assert.Equal(HttpStatusCode.Conflict, guest.StatusCode);
        Assert.Contains("Đổi điểm cần đăng nhập", (await guest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        var cap = (long)Math.Floor(600000 * pointsInfo.MaxRedeemPercent / 100);
        await using var capCheckQuery = source.CreateCommand("SELECT floor(600000 * flow.cfg('points_max_redeem_percent') / 100)::bigint");
        var dbCap = (long)(await capCheckQuery.ExecuteScalarAsync())!;
        Assert.Equal(dbCap, cap);

        var excessive = await client.PostAsJsonAsync(path, new { p_bouquets = bouquets, p_points = cap + 1 });
        Assert.Equal(HttpStatusCode.Conflict, excessive.StatusCode);
        Assert.Contains("Đổi điểm tối đa", (await excessive.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        await using var untouched = source.CreateCommand("""
            SELECT (SELECT balance = 300000 AND debt = 0 FROM identity.shop_loyalty_balances WHERE customer_id = @customer AND seller_id = @sellerA)
              AND (SELECT balance = 50000 AND debt = 0 FROM identity.shop_loyalty_balances WHERE customer_id = @customer AND seller_id = @sellerB)
              AND (SELECT loyalty_points = 999999 FROM identity.users WHERE id = @customer)
              AND NOT EXISTS (SELECT 1 FROM ordering.orders WHERE kiosk_id = @kioskA)
              AND (SELECT count(*) = 2 FROM kiosk_ops.slots WHERE kiosk_id = @kioskA AND status = 'STOCKED');
            """);
        untouched.Parameters.AddWithValue("customer", customer);
        untouched.Parameters.AddWithValue("sellerA", sellerA);
        untouched.Parameters.AddWithValue("sellerB", sellerB);
        untouched.Parameters.AddWithValue("kioskA", kioskA);
        Assert.Equal(true, await untouched.ExecuteScalarAsync());

        var redeem = Math.Min(cap, 150000L);
        Assert.True(redeem > 0);

        await using var balance = source.CreateCommand("UPDATE identity.shop_loyalty_balances SET balance = 0 WHERE customer_id = @customer AND seller_id = @sellerA");
        balance.Parameters.AddWithValue("customer", customer);
        balance.Parameters.AddWithValue("sellerA", sellerA);
        await balance.ExecuteNonQueryAsync();

        var insufficient = await client.PostAsJsonAsync(path, new { p_bouquets = bouquets, p_points = redeem });
        Assert.Equal(HttpStatusCode.Conflict, insufficient.StatusCode);
        Assert.Contains("điểm", (await insufficient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        balance.CommandText = "UPDATE identity.shop_loyalty_balances SET balance = 300000 WHERE customer_id = @customer AND seller_id = @sellerA";
        await balance.ExecuteNonQueryAsync();

        Assert.Equal(true, await untouched.ExecuteScalarAsync());

        var response = await client.PostAsJsonAsync(path, new { p_bouquets = bouquets, p_points = redeem, p_ecard = card });
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);

        var checkout = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        var payment = await client.GetFromJsonAsync<CheckoutResponse>($"/api/kiosks/{kioskA}/checkouts/{checkout}");
        Assert.Equal(600000 - redeem, payment!.Amount);
        Assert.Single(payment.Orders);
        Assert.Equal(redeem, payment.Orders.Sum(x => x.PointsRedeemed));
        Assert.Equal(redeem, payment.Orders.Sum(x => x.DiscountAmount));
        Assert.Equal(600000 - redeem, payment.Orders[0].Amount);

        await using var cardState = source.CreateCommand("SELECT count(*) FROM ordering.orders WHERE checkout_id = @checkout AND ecard_content = @card");
        cardState.Parameters.AddWithValue("checkout", checkout);
        cardState.Parameters.AddWithValue("card", card);
        Assert.Equal(1L, await cardState.ExecuteScalarAsync());

        var pointsAfterRedeem = (await client.GetFromJsonAsync<CustomerPointsResponse>(pointsPath))!;
        Assert.Equal(300000 - redeem, pointsAfterRedeem.LoyaltyPoints);

        await using var state = source.CreateCommand("""
            SELECT (SELECT balance = 300000 - @points FROM identity.shop_loyalty_balances WHERE customer_id = @customer AND seller_id = @sellerA)
              AND (SELECT balance = 50000 FROM identity.shop_loyalty_balances WHERE customer_id = @customer AND seller_id = @sellerB)
              AND (SELECT loyalty_points = 999999 FROM identity.users WHERE id = @customer)
              AND (SELECT count(*) = 1 FROM ordering.orders WHERE checkout_id = @checkout AND seller_id = @sellerA
                      AND discount_amount = @points AND points_redeemed = @points
                      AND total_amount = 600000 - @points AND subtotal = 600000
                      AND points_earned = 0 AND loyalty_policy = 'SHOP_POLICY');
            """);
        state.Parameters.AddWithValue("points", redeem);
        state.Parameters.AddWithValue("customer", customer);
        state.Parameters.AddWithValue("sellerA", sellerA);
        state.Parameters.AddWithValue("sellerB", sellerB);
        state.Parameters.AddWithValue("checkout", checkout);
        Assert.Equal(true, await state.ExecuteScalarAsync());

        // Target this checkout only: a global expiry sweep could affect concurrent fixtures.
        await using var expire = source.CreateCommand("""
            UPDATE payment.payments SET status = 'EXPIRED' WHERE checkout_id = @checkout AND status = 'PENDING';
            SELECT flow.release_checkout(@checkout, 'EXPIRED');
            SELECT flow.release_checkout(@checkout, 'EXPIRED');
            """);
        expire.Parameters.AddWithValue("checkout", checkout);
        await expire.ExecuteNonQueryAsync();

        await using var restored = source.CreateCommand("""
            SELECT (SELECT balance = 300000 FROM identity.shop_loyalty_balances WHERE customer_id = @customer AND seller_id = @sellerA)
              AND (SELECT balance = 50000 FROM identity.shop_loyalty_balances WHERE customer_id = @customer AND seller_id = @sellerB)
              AND (SELECT loyalty_points = 999999 FROM identity.users WHERE id = @customer)
              AND (SELECT count(*) = 1 FROM ordering.orders WHERE checkout_id = @checkout AND status = 'EXPIRED')
              AND (SELECT count(*) = 2 FROM kiosk_ops.slots WHERE kiosk_id = @kioskA AND status = 'STOCKED' AND hold_order_id IS NULL);
            """);
        restored.Parameters.AddWithValue("customer", customer);
        restored.Parameters.AddWithValue("sellerA", sellerA);
        restored.Parameters.AddWithValue("sellerB", sellerB);
        restored.Parameters.AddWithValue("checkout", checkout);
        restored.Parameters.AddWithValue("kioskA", kioskA);
        Assert.Equal(true, await restored.ExecuteScalarAsync());

        var pointsAfterRestore = (await client.GetFromJsonAsync<CustomerPointsResponse>(pointsPath))!;
        Assert.Equal(300000, pointsAfterRestore.LoyaltyPoints);

        // Verify legitimate claim to Kiosk B yields isolated Shop B balance
        using var clientB = factory.CreateClient();
        clientB.DefaultRequestHeaders.Authorization = new("Bearer", tokenService.Issue(
            member,
            sellerStatus: shopOwner ? "ACTIVE" : null,
            kioskId: kioskB.ToString()));

        var responseB = await clientB.GetAsync($"/api/kiosks/{kioskB}/customer/points");
        responseB.EnsureSuccessStatusCode();
        var pointsB = (await responseB.Content.ReadFromJsonAsync<CustomerPointsResponse>())!;
        Assert.Equal(50000, pointsB.LoyaltyPoints);
        Assert.Equal(HttpStatusCode.Forbidden, (await clientB.GetAsync(pointsPath)).StatusCode);
    }

    [Fact]
    public async Task CustomerPointsEndpoint_ReturnsPerShopBalance_IsolatingGlobalAndOtherShops()
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();

        var customer = Guid.NewGuid();
        var inactiveCustomer = Guid.NewGuid();
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();
        var kioskA = Guid.NewGuid();
        var kioskB = Guid.NewGuid();
        var slotA = Guid.NewGuid();
        var slotB = Guid.NewGuid();
        var keyA = "key-a-" + kioskA;
        var keyB = "key-b-" + kioskB;
        var hashA = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keyA)));
        var hashB = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keyB)));

        await using var setup = source.CreateCommand("""
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
            VALUES (@sellerA, 'Shop A', '0901111111', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30),
                   (@sellerB, 'Shop B', '0902222222', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30);

            INSERT INTO identity.users(id, email, full_name, role, status, loyalty_points)
            VALUES (@customer, @customer::text || '@example.invalid', 'Customer Active', 'CUSTOMER', 'ACTIVE', 999999),
                   (@inactiveCustomer, @inactiveCustomer::text || '@example.invalid', 'Customer Inactive', 'CUSTOMER', 'DISABLED', 100000);

            INSERT INTO identity.shop_loyalty_balances(customer_id, seller_id, balance, debt)
            VALUES (@customer, @sellerA, 300000, 0),
                   (@customer, @sellerB, 50000, 0);

            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status, api_key_hash)
            VALUES (@kioskA, @kioskA::text, 'Kiosk A', 'Address A', 'HCM', @kioskA::text, @kioskA::text, 'ONLINE', @hashA),
                   (@kioskB, @kioskB::text, 'Kiosk B', 'Address B', 'HCM', @kioskB::text, @kioskB::text, 'ONLINE', @hashB);

            INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel, zone)
            VALUES (@slotA, @kioskA, 'A01', 0, 'WALK_IN'),
                   (@slotB, @kioskB, 'B01', 0, 'WALK_IN');

            SELECT flow.assign_kiosk(@kioskA, @sellerA, '10000000-0000-0000-0000-000000000001');
            SELECT flow.assign_kiosk(@kioskB, @sellerB, '10000000-0000-0000-0000-000000000001');
            """);
        setup.Parameters.AddWithValue("sellerA", sellerA);
        setup.Parameters.AddWithValue("sellerB", sellerB);
        setup.Parameters.AddWithValue("customer", customer);
        setup.Parameters.AddWithValue("inactiveCustomer", inactiveCustomer);
        setup.Parameters.AddWithValue("kioskA", kioskA);
        setup.Parameters.AddWithValue("kioskB", kioskB);
        setup.Parameters.AddWithValue("hashA", hashA);
        setup.Parameters.AddWithValue("hashB", hashB);
        setup.Parameters.AddWithValue("slotA", slotA);
        setup.Parameters.AddWithValue("slotB", slotB);
        await setup.ExecuteNonQueryAsync();

        var member = new User { Id = customer, Role = "CUSTOMER", FullName = "Customer Active", Status = "ACTIVE" };

        // 1. Visit Kiosk A: should return Shop A balance (300,000), NOT global (999,999) or Shop B (50,000)
        using var clientA = factory.CreateClient();
        clientA.DefaultRequestHeaders.Authorization = new("Bearer", tokenService.Issue(member, kioskId: kioskA.ToString()));

        var responseA = await clientA.GetAsync($"/api/kiosks/{kioskA}/customer/points");
        responseA.EnsureSuccessStatusCode();
        Assert.True(responseA.Headers.CacheControl?.NoStore);
        var pointsA = (await responseA.Content.ReadFromJsonAsync<CustomerPointsResponse>())!;
        Assert.Equal(300000, pointsA.LoyaltyPoints);
        await using var capCommand = source.CreateCommand("SELECT flow.cfg('points_max_redeem_percent')");
        Assert.Equal((decimal)(await capCommand.ExecuteScalarAsync())!, pointsA.MaxRedeemPercent);

        // 2. Visit Kiosk B: should return Shop B balance (50,000)
        using var clientB = factory.CreateClient();
        clientB.DefaultRequestHeaders.Authorization = new("Bearer", tokenService.Issue(member, kioskId: kioskB.ToString()));

        var responseB = await clientB.GetAsync($"/api/kiosks/{kioskB}/customer/points");
        responseB.EnsureSuccessStatusCode();
        Assert.True(responseB.Headers.CacheControl?.NoStore);
        var pointsB = (await responseB.Content.ReadFromJsonAsync<CustomerPointsResponse>())!;
        Assert.Equal(50000, pointsB.LoyaltyPoints);

        // 3. Foreign / Cross-kiosk claim-to-route protection
        var forbiddenB = await clientA.GetAsync($"/api/kiosks/{kioskB}/customer/points");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenB.StatusCode);
        var forbiddenRandom = await clientA.GetAsync($"/api/kiosks/{Guid.NewGuid()}/customer/points");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRandom.StatusCode);

        // 4. Inactive caller rejected with 401 Unauthorized
        var inactiveUser = new User { Id = inactiveCustomer, Role = "CUSTOMER", FullName = "Customer Inactive", Status = "DISABLED" };
        using var inactiveClient = factory.CreateClient();
        inactiveClient.DefaultRequestHeaders.Authorization = new("Bearer", tokenService.Issue(inactiveUser, kioskId: kioskA.ToString()));
        var inactiveResponse = await inactiveClient.GetAsync($"/api/kiosks/{kioskA}/customer/points");
        Assert.Equal(HttpStatusCode.Unauthorized, inactiveResponse.StatusCode);

        // 5. Unauthorized callers rejected
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/kiosks/{kioskA}/customer/points")).StatusCode);

        using var device = factory.CreateClient();
        device.DefaultRequestHeaders.Add("X-Kiosk-Key", keyA);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync($"/api/kiosks/{kioskA}/customer/points")).StatusCode);

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/kiosks/{kioskA}/customer/points")).StatusCode);

        // 6. Debt semantics: active debt blocks redemption, available points becomes 0
        await using var debtCmd = source.CreateCommand("UPDATE identity.shop_loyalty_balances SET debt = 250 WHERE customer_id = @customer AND seller_id = @sellerA");
        debtCmd.Parameters.AddWithValue("customer", customer);
        debtCmd.Parameters.AddWithValue("sellerA", sellerA);
        await debtCmd.ExecuteNonQueryAsync();

        var responseWithDebt = await clientA.GetAsync($"/api/kiosks/{kioskA}/customer/points");
        responseWithDebt.EnsureSuccessStatusCode();
        var pointsDebt = (await responseWithDebt.Content.ReadFromJsonAsync<CustomerPointsResponse>())!;
        Assert.Equal(0, pointsDebt.LoyaltyPoints);

        // Shop B remains isolated and unaffected by Shop A debt
        var responseBStill50k = await clientB.GetAsync($"/api/kiosks/{kioskB}/customer/points");
        responseBStill50k.EnsureSuccessStatusCode();
        Assert.Equal(50000, (await responseBStill50k.Content.ReadFromJsonAsync<CustomerPointsResponse>())!.LoyaltyPoints);

        // 7. SELLER role as buyer supported when visiting kiosk
        var sellerBuyer = Guid.NewGuid();
        await using var sellerBuyerCmd = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, role, status, seller_id, password_hash)
            VALUES (@id, @id::text || '@example.invalid', 'Seller Buyer', 'SELLER', 'ACTIVE', @sellerB, '!unprovisioned');
            INSERT INTO identity.shop_loyalty_balances(customer_id, seller_id, balance, debt)
            VALUES (@id, @sellerA, 75000, 0);
            """);
        sellerBuyerCmd.Parameters.AddWithValue("id", sellerBuyer);
        sellerBuyerCmd.Parameters.AddWithValue("sellerB", sellerB);
        sellerBuyerCmd.Parameters.AddWithValue("sellerA", sellerA);
        await sellerBuyerCmd.ExecuteNonQueryAsync();

        var sellerMember = new User { Id = sellerBuyer, Role = "SELLER", FullName = "Seller Buyer", SellerId = sellerB, Status = "ACTIVE", PasswordHash = "!unprovisioned" };
        using var sellerClient = factory.CreateClient();
        sellerClient.DefaultRequestHeaders.Authorization = new("Bearer", tokenService.Issue(sellerMember, kioskId: kioskA.ToString()));

        var sellerPointsResponse = await sellerClient.GetAsync($"/api/kiosks/{kioskA}/customer/points");
        sellerPointsResponse.EnsureSuccessStatusCode();
        Assert.Equal(75000, (await sellerPointsResponse.Content.ReadFromJsonAsync<CustomerPointsResponse>())!.LoyaltyPoints);
    }
}
