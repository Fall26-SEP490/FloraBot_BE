using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FloraBot.ShopLoyalty.Tests;

public sealed class ShopLoyaltyTestFactory : WebApplicationFactory<Program>
{
    public static string RequireDatabaseUrl()
    {
        var dbUrl = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(dbUrl))
        {
            Assert.Fail("TEST_DATABASE_URL is required for DB tests but was not provided. Absence FAILS as required.");
        }
        return dbUrl;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());

        var dbUrl = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(dbUrl))
        {
            builder.UseSetting("DATABASE_URL", dbUrl);
        }

        var valkeyUrl = Environment.GetEnvironmentVariable("TEST_VALKEY_URL") ?? "127.0.0.1:51640,defaultDatabase=11";
        builder.UseSetting("VALKEY_URL", valkeyUrl);

        builder.UseSetting("JWT_SIGNING_KEY", "test-only-key-at-least-thirty-two-bytes-long-not-production");
        builder.UseSetting("JOBS_ENABLED", "false");
        builder.UseSetting("MQTT_ENABLED", "false");
    }

    public string CreateToken(Guid userId, string role, Guid? sellerId = null)
    {
        using var scope = Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();
        var user = new User
        {
            Id = userId,
            Role = role,
            FullName = "Test Actor",
            SellerId = (role == "SELLER" || role == "SELLER_STAFF") ? sellerId : null,
            Status = "ACTIVE"
        };
        return tokenService.Issue(user, "APPROVED");
    }
}

[Trait("Category", "Database")]
public sealed class ShopLoyaltyIntegrationTests : IClassFixture<ShopLoyaltyTestFactory>
{
    private readonly ShopLoyaltyTestFactory _factory;

    public ShopLoyaltyIntegrationTests(ShopLoyaltyTestFactory factory)
    {
        _factory = factory;
    }

    private static string GetDatabaseUrlOrDie() => ShopLoyaltyTestFactory.RequireDatabaseUrl();

    private static async Task<NpgsqlConnection> OpenDbConnectionAsync()
    {
        var connStr = GetDatabaseUrlOrDie();
        var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task SeedSellerAsync(NpgsqlConnection conn, Guid sellerId, string shopName, string phone)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO identity.sellers (
                id, shop_name, phone, address, status, package_id,
                package_expires_at, package_expires_at_ts,
                brand_tone, bank_name, bank_account_no_enc, bank_holder
            ) VALUES (
                @id, @name, @phone, '123 Test Street, HCM', 'ACTIVE', '20000000-0000-0000-0000-00000000000a'::uuid,
                '2099-12-31'::date, public.app_now() + interval '365 days',
                'Friendly', 'Vietcombank', 'enc:v1:QUJDMTIzNDU2', 'NGUYEN VAN AN'
            );
            """;
        cmd.Parameters.AddWithValue("id", sellerId);
        cmd.Parameters.AddWithValue("name", shopName);
        cmd.Parameters.AddWithValue("phone", phone);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedUserAsync(NpgsqlConnection conn, Guid userId, string fullName, string email, string phone, string role, string status, Guid? sellerId = null, long globalPoints = 0)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO identity.users (id, full_name, email, phone, role, status, seller_id, loyalty_points)
            VALUES (@id, @name, @email, @phone, @role, @status, @sellerId, @globalPoints);
            """;
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("name", fullName);
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("phone", phone);
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("sellerId", (object?)sellerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("globalPoints", globalPoints);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ExistingSameDayPending_PreservedAfterMigration()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var kioskId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var legacyOrderId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"Legacy Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        // Customer has 5,000 legacy global points
        await SeedUserAsync(conn, customerId, "Legacy Customer", $"legacy-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE", globalPoints: 5000);

        // Insert order explicitly marked LEGACY_GLOBAL (representing pre-migration orders from same day)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, loyalty_policy, created_at, updated_at
                ) VALUES (
                    @legacyOrderId, @orderCode, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                    'DISPENSING', 200000, 0, 200000, 0, 2000,
                    @trackingToken, 'LEGACY_GLOBAL', public.app_now(), public.app_now()
                );

                -- Complete legacy order: must credit into identity.users.loyalty_points
                SELECT flow.complete_order(@legacyOrderId, 'SYSTEM', 'Door closed');
                """;
            cmd.Parameters.AddWithValue("legacyOrderId", legacyOrderId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("kioskId", kioskId);
            cmd.Parameters.AddWithValue("orderCode", $"FBL-{suffix}");
            cmd.Parameters.AddWithValue("trackingToken", $"trk-l-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        // Verify: Global points incremented by 2,000 (5,000 -> 7,000)
        await using (var checkUser = conn.CreateCommand())
        {
            checkUser.CommandText = "SELECT loyalty_points FROM identity.users WHERE id = @customerId;";
            checkUser.Parameters.AddWithValue("customerId", customerId);
            var userPoints = (long)(await checkUser.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(7000, userPoints);
        }

        // Verify: Shop loyalty balance was NOT touched (remains 0)
        await using (var checkShop = conn.CreateCommand())
        {
            checkShop.CommandText = "SELECT coalesce((SELECT balance FROM identity.shop_loyalty_balances WHERE customer_id = @customerId AND seller_id = @sellerId), 0);";
            checkShop.Parameters.AddWithValue("customerId", customerId);
            checkShop.Parameters.AddWithValue("sellerId", sellerId);
            var shopBalance = (long)(await checkShop.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(0, shopBalance);
        }
    }

    [Fact]
    public async Task NewOrderBackdated_StillShopPolicy()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var kioskId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var backdatedOrderId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"Backdated Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, customerId, "Backdated Customer", $"backdated-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE", globalPoints: 1000);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.seller_loyalty_settings (seller_id, enabled, earn_rate_percent)
                VALUES (@sellerId, true, 1.00);

                -- Insert new order with backdated created_at (-5 days), omitting loyalty_policy to take DEFAULT
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @backdatedOrderId, @orderCode, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                    'DISPENSING', 400000, 0, 400000, 0, 0,
                    @trackingToken, public.app_now() - interval '5 days', public.app_now() - interval '5 days'
                );

                -- Link preorder web_request
                INSERT INTO ordering.web_requests (
                    id, customer_id, seller_id, product_id, kiosk_id, kind, instructions,
                    pickup_at, pickup_before, quoted_price, state, order_id, created_at, updated_at
                ) VALUES (
                    @requestId, @customerId, @sellerId, '30000000-0000-0000-0000-000000000001'::uuid, @kioskId, 'STOCK', 'Preorder note',
                    public.app_now(), public.app_now() + interval '2 hours', 400000, 'ORDERED', @backdatedOrderId,
                    public.app_now(), public.app_now()
                );

                SELECT flow.snapshot_order_loyalty(@backdatedOrderId);
                SELECT flow.complete_order(@backdatedOrderId, 'SYSTEM', 'Door closed');
                """;
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("kioskId", kioskId);
            cmd.Parameters.AddWithValue("backdatedOrderId", backdatedOrderId);
            cmd.Parameters.AddWithValue("requestId", requestId);
            cmd.Parameters.AddWithValue("orderCode", $"FBB-{suffix}");
            cmd.Parameters.AddWithValue("trackingToken", $"trk-b-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        // Verify: Provenance is SHOP_POLICY, NOT LEGACY_GLOBAL despite backdated created_at
        await using (var checkOrder = conn.CreateCommand())
        {
            checkOrder.CommandText = "SELECT loyalty_policy FROM ordering.orders WHERE id = @backdatedOrderId;";
            checkOrder.Parameters.AddWithValue("backdatedOrderId", backdatedOrderId);
            var policy = (string?)(await checkOrder.ExecuteScalarAsync());
            Assert.Equal("SHOP_POLICY", policy);
        }

        // Verify: Shop loyalty balance received 4,000 points
        await using (var checkShop = conn.CreateCommand())
        {
            checkShop.CommandText = "SELECT balance FROM identity.shop_loyalty_balances WHERE customer_id = @customerId AND seller_id = @sellerId;";
            checkShop.Parameters.AddWithValue("customerId", customerId);
            checkShop.Parameters.AddWithValue("sellerId", sellerId);
            var balance = (long)(await checkShop.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(4000, balance);
        }

        // Verify: User legacy global points remain unchanged (1,000)
        await using (var checkUser = conn.CreateCommand())
        {
            checkUser.CommandText = "SELECT loyalty_points FROM identity.users WHERE id = @customerId;";
            checkUser.Parameters.AddWithValue("customerId", customerId);
            var userPoints = (long)(await checkUser.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(1000, userPoints);
        }
    }

    [Fact]
    public async Task OwnerCanGetAndPutLoyaltySettings_AuditRecorded()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sellerPhone = $"09{Random.Shared.Next(10000000, 99999999)}";
        var userPhone = $"09{Random.Shared.Next(10000000, 99999999)}";
        var userEmail = $"owner-{suffix}@florabot.test";

        await SeedSellerAsync(conn, sellerId, $"Owner Shop {suffix}", sellerPhone);
        await SeedUserAsync(conn, ownerId, "Owner Seller", userEmail, userPhone, "SELLER", "ACTIVE", sellerId);

        using var client = _factory.CreateClient();
        var token = _factory.CreateToken(ownerId, "SELLER", sellerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. GET default settings: default OFF opt-in, fixed 1.00%
        var getResp = await client.GetAsync($"/api/sellers/{sellerId}/loyalty-settings");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var settings = await getResp.Content.ReadFromJsonAsync<SellerLoyaltySettingsResponse>();
        Assert.NotNull(settings);
        Assert.Equal(sellerId, settings.SellerId);
        Assert.False(settings.Enabled);
        Assert.Equal(1.00m, settings.EarnRatePercent);

        // 2. PUT toggle to enabled: true
        var putResp = await client.PutAsJsonAsync($"/api/sellers/{sellerId}/loyalty-settings", new UpdateSellerLoyaltySettingsRequest(true));
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var updated = await putResp.Content.ReadFromJsonAsync<SellerLoyaltySettingsResponse>();
        Assert.NotNull(updated);
        Assert.True(updated.Enabled);
        Assert.Equal(1.00m, updated.EarnRatePercent);

        // 3. Verify atomic audit logged in notify.audit_logs
        await using (var auditCmd = conn.CreateCommand())
        {
            auditCmd.CommandText = """
                SELECT count(*) FROM notify.audit_logs
                WHERE action = 'SELLER_LOYALTY_SETTINGS_UPDATED'
                  AND actor_id = @ownerId
                  AND entity_id = @sellerId;
                """;
            auditCmd.Parameters.AddWithValue("ownerId", ownerId);
            auditCmd.Parameters.AddWithValue("sellerId", sellerId);
            var auditCount = (long)(await auditCmd.ExecuteScalarAsync() ?? 0L);
            Assert.True(auditCount >= 1, "Audit record for loyalty settings update must exist.");
        }
    }

    [Fact]
    public async Task ForeignRoleAndDisabledActor_Rejected()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();
        var foreignOwnerId = Guid.NewGuid();
        var disabledOwnerId = Guid.NewGuid();
        var suffixA = Guid.NewGuid().ToString("N")[..8];
        var suffixB = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerA, $"Shop A {suffixA}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedSellerAsync(conn, sellerB, $"Shop B {suffixB}", $"09{Random.Shared.Next(10000000, 99999999)}");

        await SeedUserAsync(conn, foreignOwnerId, "Foreign Owner", $"foreign-{suffixB}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "SELLER", "ACTIVE", sellerB);
        await SeedUserAsync(conn, disabledOwnerId, "Disabled Owner", $"disabled-{suffixA}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "SELLER", "DISABLED", sellerA);

        using var client = _factory.CreateClient();

        // Foreign seller trying to access seller A's loyalty settings -> rejected
        var foreignToken = _factory.CreateToken(foreignOwnerId, "SELLER", sellerB);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", foreignToken);
        var foreignResp = await client.GetAsync($"/api/sellers/{sellerA}/loyalty-settings");
        Assert.True(foreignResp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"Foreign seller should be rejected with 403 or 404, got {foreignResp.StatusCode}");

        // Disabled seller trying to access seller A's loyalty settings -> rejected by active DB check
        var disabledToken = _factory.CreateToken(disabledOwnerId, "SELLER", sellerA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", disabledToken);
        var disabledResp = await client.GetAsync($"/api/sellers/{sellerA}/loyalty-settings");
        Assert.True(disabledResp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"Disabled actor should be rejected with 401 or 403, got {disabledResp.StatusCode}");
    }

    [Fact]
    public async Task TwoShopsBalances_Isolated()
    {
        await using var conn = await OpenDbConnectionAsync();

        var shopA = Guid.NewGuid();
        var shopB = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, shopA, $"Isolation Shop A {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedSellerAsync(conn, shopB, $"Isolation Shop B {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");

        await SeedUserAsync(conn, customerId, "Isolated Customer", $"isolated-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        // Customer has 1500 points at Shop A, 0 at Shop B
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.shop_loyalty_balances (customer_id, seller_id, balance, debt)
                VALUES (@customerId, @shopA, 1500, 0);
                """;
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("shopA", shopA);
            await cmd.ExecuteNonQueryAsync();
        }

        using var client = _factory.CreateClient();
        var token = _factory.CreateToken(customerId, "CUSTOMER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // GET member balance for Shop A: balance = 1500
        var respA = await client.GetAsync($"/api/member/loyalty/{shopA}");
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
        var balA = await respA.Content.ReadFromJsonAsync<ShopLoyaltyBalanceItem>();
        Assert.NotNull(balA);
        Assert.Equal(1500, balA.Balance);
        Assert.Equal(0, balA.Debt);

        // GET member balance for Shop B: balance = 0
        var respB = await client.GetAsync($"/api/member/loyalty/{shopB}");
        Assert.Equal(HttpStatusCode.OK, respB.StatusCode);
        var balB = await respB.Content.ReadFromJsonAsync<ShopLoyaltyBalanceItem>();
        Assert.NotNull(balB);
        Assert.Equal(0, balB.Balance);
        Assert.Equal(0, balB.Debt);

        // GET /api/member/loyalty paged list: returns Shop A
        var pageResp = await client.GetAsync("/api/member/loyalty?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, pageResp.StatusCode);
        var page = await pageResp.Content.ReadFromJsonAsync<MemberLoyaltyPageResponse>();
        Assert.NotNull(page);
        Assert.Contains(page.Items, item => item.SellerId == shopA && item.Balance == 1500);
    }

    [Fact]
    public async Task Preorder_Earns1PercentOnCompletion()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var kioskId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var orderId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"Preorder Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, customerId, "Preorder Customer", $"preorder-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.seller_loyalty_settings (seller_id, enabled, earn_rate_percent)
                VALUES (@sellerId, true, 1.00);

                -- Insert PREORDER order (total_amount = 300,000 VND)
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @orderId, @orderCode, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                    'DISPENSING', 300000, 0, 300000, 0, 0,
                    @trackingToken, public.app_now(), public.app_now()
                );

                -- Link preorder web_request
                INSERT INTO ordering.web_requests (
                    id, customer_id, seller_id, product_id, kiosk_id, kind, instructions,
                    pickup_at, pickup_before, quoted_price, state, order_id, created_at, updated_at
                ) VALUES (
                    @requestId, @customerId, @sellerId, '30000000-0000-0000-0000-000000000001'::uuid, @kioskId, 'STOCK', 'Preorder note',
                    public.app_now(), public.app_now() + interval '2 hours', 300000, 'ORDERED', @orderId,
                    public.app_now(), public.app_now()
                );

                -- Snapshot order loyalty
                SELECT flow.snapshot_order_loyalty(@orderId);

                -- Complete order
                SELECT flow.complete_order(@orderId, 'SYSTEM', 'Door closed');
                """;
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("kioskId", kioskId);
            cmd.Parameters.AddWithValue("orderId", orderId);
            cmd.Parameters.AddWithValue("requestId", requestId);
            cmd.Parameters.AddWithValue("orderCode", $"FBP-{suffix}");
            cmd.Parameters.AddWithValue("trackingToken", $"trk-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        // Verify customer received 1% of 300,000 = 3,000 points
        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = """
                SELECT balance, debt FROM identity.shop_loyalty_balances
                WHERE customer_id = @customerId AND seller_id = @sellerId;
                """;
            checkCmd.Parameters.AddWithValue("customerId", customerId);
            checkCmd.Parameters.AddWithValue("sellerId", sellerId);
            await using var reader = await checkCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3000, reader.GetInt64(0));
            Assert.Equal(0, reader.GetInt64(1));
        }
    }

    [Fact]
    public async Task WalkIn_EarnsZero()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var kioskId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var orderId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"WalkIn Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, customerId, "WalkIn Customer", $"walkin-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.seller_loyalty_settings (seller_id, enabled, earn_rate_percent)
                VALUES (@sellerId, true, 1.00);

                -- Insert WALK_IN order (no web_requests linked)
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @orderId, @orderCode, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                    'DISPENSING', 500000, 0, 500000, 0, 0,
                    @trackingToken, public.app_now(), public.app_now()
                );

                SELECT flow.snapshot_order_loyalty(@orderId);

                -- Complete order
                SELECT flow.complete_order(@orderId, 'SYSTEM', 'Door closed');
                """;
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("kioskId", kioskId);
            cmd.Parameters.AddWithValue("orderId", orderId);
            cmd.Parameters.AddWithValue("orderCode", $"FBW-{suffix}");
            cmd.Parameters.AddWithValue("trackingToken", $"trkw-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        // Verify customer received 0 points for WALK_IN
        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = """
                SELECT coalesce((SELECT balance FROM identity.shop_loyalty_balances WHERE customer_id = @customerId AND seller_id = @sellerId), 0);
                """;
            checkCmd.Parameters.AddWithValue("customerId", customerId);
            checkCmd.Parameters.AddWithValue("sellerId", sellerId);
            var balance = (long)(await checkCmd.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(0, balance);
        }
    }

    [Fact]
    public async Task NoAward_Unpaid_Canceled_SelfOrder()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var selfCustomerId = Guid.NewGuid();
        var regularCustomerId = Guid.NewGuid();
        var kioskId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var unpaidOrderId = Guid.NewGuid();
        var canceledOrderId = Guid.NewGuid();
        var selfOrderId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"SelfOrder Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, selfCustomerId, "Self Seller Customer", $"self-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "SELLER", "ACTIVE", sellerId);
        await SeedUserAsync(conn, regularCustomerId, "Regular Customer", $"reg-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.seller_loyalty_settings (seller_id, enabled, earn_rate_percent)
                VALUES (@sellerId, true, 1.00);

                -- 1. Unpaid order
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @unpaidOrderId, @unpaidCode, gen_random_uuid(), @regularCustomerId, @sellerId, @kioskId,
                    'AWAITING_PAYMENT', 200000, 0, 200000, 0, 0,
                    @unpaidToken, public.app_now(), public.app_now()
                );
                SELECT flow.snapshot_order_loyalty(@unpaidOrderId);
                SELECT flow.credit_points(@regularCustomerId, 2000, @unpaidOrderId);

                -- 2. Canceled order
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @canceledOrderId, @canceledCode, gen_random_uuid(), @regularCustomerId, @sellerId, @kioskId,
                    'CANCELLED', 200000, 0, 200000, 0, 0,
                    @canceledToken, public.app_now(), public.app_now()
                );
                SELECT flow.snapshot_order_loyalty(@canceledOrderId);
                SELECT flow.credit_points(@regularCustomerId, 2000, @canceledOrderId);

                -- 3. Self order
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @selfOrderId, @selfCode, gen_random_uuid(), @selfCustomerId, @sellerId, @kioskId,
                    'DISPENSING', 200000, 0, 200000, 0, 0,
                    @selfToken, public.app_now(), public.app_now()
                );
                SELECT flow.snapshot_order_loyalty(@selfOrderId);
                SELECT flow.complete_order(@selfOrderId, 'SYSTEM', 'Door closed');
                """;
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("selfCustomerId", selfCustomerId);
            cmd.Parameters.AddWithValue("regularCustomerId", regularCustomerId);
            cmd.Parameters.AddWithValue("kioskId", kioskId);
            cmd.Parameters.AddWithValue("unpaidOrderId", unpaidOrderId);
            cmd.Parameters.AddWithValue("canceledOrderId", canceledOrderId);
            cmd.Parameters.AddWithValue("selfOrderId", selfOrderId);
            cmd.Parameters.AddWithValue("unpaidCode", $"FB-U-{suffix}");
            cmd.Parameters.AddWithValue("canceledCode", $"FB-C-{suffix}");
            cmd.Parameters.AddWithValue("selfCode", $"FB-S-{suffix}");
            cmd.Parameters.AddWithValue("unpaidToken", $"trk-u-{suffix}");
            cmd.Parameters.AddWithValue("canceledToken", $"trk-c-{suffix}");
            cmd.Parameters.AddWithValue("selfToken", $"trk-s-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        // Verify neither customer received any points
        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = """
                SELECT
                    coalesce((SELECT balance FROM identity.shop_loyalty_balances WHERE customer_id = @regularCustomerId AND seller_id = @sellerId), 0),
                    coalesce((SELECT balance FROM identity.shop_loyalty_balances WHERE customer_id = @selfCustomerId AND seller_id = @sellerId), 0);
                """;
            checkCmd.Parameters.AddWithValue("regularCustomerId", regularCustomerId);
            checkCmd.Parameters.AddWithValue("selfCustomerId", selfCustomerId);
            checkCmd.Parameters.AddWithValue("sellerId", sellerId);
            await using var reader = await checkCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0, reader.GetInt64(0));
            Assert.Equal(0, reader.GetInt64(1));
        }
    }

    [Fact]
    public async Task DuplicateAward_AndOverspend_Blocked()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var kioskId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var orderId = Guid.NewGuid();
        var order2Id = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"DupAward Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, customerId, "Dup Customer", $"dup-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.seller_loyalty_settings (seller_id, enabled, earn_rate_percent)
                VALUES (@sellerId, true, 1.00);

                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @orderId, @orderCode, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                    'DISPENSING', 100000, 0, 100000, 0, 0,
                    @trackingToken, public.app_now(), public.app_now()
                );

                INSERT INTO ordering.web_requests (
                    id, customer_id, seller_id, product_id, kiosk_id, kind, instructions,
                    pickup_at, pickup_before, quoted_price, state, order_id, created_at, updated_at
                ) VALUES (
                    @requestId, @customerId, @sellerId, '30000000-0000-0000-0000-000000000001'::uuid, @kioskId, 'STOCK', 'Preorder note',
                    public.app_now(), public.app_now() + interval '2 hours', 100000, 'ORDERED', @orderId,
                    public.app_now(), public.app_now()
                );

                SELECT flow.snapshot_order_loyalty(@orderId);

                -- First completion awards 1,000 points
                SELECT flow.complete_order(@orderId, 'SYSTEM', 'Door closed');

                -- Retry: calling credit_points directly must NOT double award
                SELECT flow.credit_points(@customerId, 1000, @orderId);
                """;
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("kioskId", kioskId);
            cmd.Parameters.AddWithValue("orderId", orderId);
            cmd.Parameters.AddWithValue("requestId", requestId);
            cmd.Parameters.AddWithValue("orderCode", $"FB-D-{suffix}");
            cmd.Parameters.AddWithValue("trackingToken", $"trk-d-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        // Verify balance is 1,000 (not 2,000)
        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT balance FROM identity.shop_loyalty_balances WHERE customer_id = @customerId AND seller_id = @sellerId;";
            checkCmd.Parameters.AddWithValue("customerId", customerId);
            checkCmd.Parameters.AddWithValue("sellerId", sellerId);
            var balance = (long)(await checkCmd.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(1000, balance);
        }

        // Attempt overspend (trying to redeem 1,500 points when having 1,000)
        await using (var overspendCmd = conn.CreateCommand())
        {
            overspendCmd.CommandText = """
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @order2Id, @order2Code, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                    'AWAITING_PAYMENT', 200000, 0, 200000, 0, 0,
                    @order2Token, public.app_now(), public.app_now()
                );

                SELECT flow.redeem_points(@customerId, 1500, @order2Id);
                """;
            overspendCmd.Parameters.AddWithValue("order2Id", order2Id);
            overspendCmd.Parameters.AddWithValue("order2Code", $"FB-D2-{suffix}");
            overspendCmd.Parameters.AddWithValue("order2Token", $"trk-d2-{suffix}");
            overspendCmd.Parameters.AddWithValue("customerId", customerId);
            overspendCmd.Parameters.AddWithValue("sellerId", sellerId);
            overspendCmd.Parameters.AddWithValue("kioskId", kioskId);

            var ex = await Assert.ThrowsAsync<PostgresException>(() => overspendCmd.ExecuteNonQueryAsync());
            Assert.Contains("không đủ", ex.MessageText);
        }
    }

    [Fact]
    public async Task ConcurrentRedeem_PreventsOverspend()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var kioskId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var order1Id = Guid.NewGuid();
        var order2Id = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"Concurrent Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, customerId, "Concurrent Customer", $"concurrent-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        // Set customer balance to exactly 500 points
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.shop_loyalty_balances (customer_id, seller_id, balance, debt)
                VALUES (@customerId, @sellerId, 500, 0);

                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES
                (@order1Id, @code1, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                 'AWAITING_PAYMENT', 100000, 0, 100000, 0, 0, @token1, public.app_now(), public.app_now()),
                (@order2Id, @code2, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                 'AWAITING_PAYMENT', 100000, 0, 100000, 0, 0, @token2, public.app_now(), public.app_now());
                """;
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("kioskId", kioskId);
            cmd.Parameters.AddWithValue("order1Id", order1Id);
            cmd.Parameters.AddWithValue("order2Id", order2Id);
            cmd.Parameters.AddWithValue("code1", $"FB-CON1-{suffix}");
            cmd.Parameters.AddWithValue("code2", $"FB-CON2-{suffix}");
            cmd.Parameters.AddWithValue("token1", $"trk-con1-{suffix}");
            cmd.Parameters.AddWithValue("token2", $"trk-con2-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        // Run two concurrent transactions each trying to redeem 500 points
        var task1 = Task.Run(async () =>
        {
            await using var c = await OpenDbConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            await using var redeemCmd = new NpgsqlCommand("SELECT flow.redeem_points(@c, 500, @o)", c, tx);
            redeemCmd.Parameters.AddWithValue("c", customerId);
            redeemCmd.Parameters.AddWithValue("o", order1Id);
            try
            {
                await redeemCmd.ExecuteNonQueryAsync();
                await tx.CommitAsync();
                return true;
            }
            catch (PostgresException)
            {
                await tx.RollbackAsync();
                return false;
            }
        });

        var task2 = Task.Run(async () =>
        {
            await using var c = await OpenDbConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            await using var redeemCmd = new NpgsqlCommand("SELECT flow.redeem_points(@c, 500, @o)", c, tx);
            redeemCmd.Parameters.AddWithValue("c", customerId);
            redeemCmd.Parameters.AddWithValue("o", order2Id);
            try
            {
                await redeemCmd.ExecuteNonQueryAsync();
                await tx.CommitAsync();
                return true;
            }
            catch (PostgresException)
            {
                await tx.RollbackAsync();
                return false;
            }
        });

        var results = await Task.WhenAll(task1, task2);
        Assert.Single(results, r => r == true);  // Exactly one succeeded
        Assert.Single(results, r => r == false); // Exactly one failed

        // Verify remaining balance is 0
        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT balance FROM identity.shop_loyalty_balances WHERE customer_id = @customerId AND seller_id = @sellerId;";
            checkCmd.Parameters.AddWithValue("customerId", customerId);
            checkCmd.Parameters.AddWithValue("sellerId", sellerId);
            var balance = (long)(await checkCmd.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(0, balance);
        }
    }

    [Fact]
    public async Task SnapshotThroughDisable_MaintainsPromisedEarn()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var kioskId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var promisedOrderId = Guid.NewGuid();
        var promisedRequestId = Guid.NewGuid();
        var newOrderId = Guid.NewGuid();
        var newRequestId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"Snapshot Disable Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, customerId, "Snapshot Customer", $"snapshot-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                -- Seller initially ENABLES loyalty
                INSERT INTO identity.seller_loyalty_settings (seller_id, enabled, earn_rate_percent)
                VALUES (@sellerId, true, 1.00);

                -- Order 1: Created WHILE loyalty is enabled
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @promisedOrderId, @promCode, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                    'DISPENSING', 200000, 0, 200000, 0, 0,
                    @promToken, public.app_now(), public.app_now()
                );

                INSERT INTO ordering.web_requests (
                    id, customer_id, seller_id, product_id, kiosk_id, kind, instructions,
                    pickup_at, pickup_before, quoted_price, state, order_id, created_at, updated_at
                ) VALUES (
                    @promisedRequestId, @customerId, @sellerId, '30000000-0000-0000-0000-000000000001'::uuid, @kioskId, 'STOCK', 'Preorder note',
                    public.app_now(), public.app_now() + interval '2 hours', 200000, 'ORDERED', @promisedOrderId,
                    public.app_now(), public.app_now()
                );

                SELECT flow.snapshot_order_loyalty(@promisedOrderId);

                -- Seller now DISABLES loyalty
                UPDATE identity.seller_loyalty_settings SET enabled = false, updated_at = public.app_now()
                WHERE seller_id = @sellerId;

                -- Order 2: Created AFTER loyalty was disabled
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @newOrderId, @newCode, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                    'DISPENSING', 200000, 0, 200000, 0, 0,
                    @newToken, public.app_now(), public.app_now()
                );

                INSERT INTO ordering.web_requests (
                    id, customer_id, seller_id, product_id, kiosk_id, kind, instructions,
                    pickup_at, pickup_before, quoted_price, state, order_id, created_at, updated_at
                ) VALUES (
                    @newRequestId, @customerId, @sellerId, '30000000-0000-0000-0000-000000000001'::uuid, @kioskId, 'STOCK', 'Preorder note',
                    public.app_now(), public.app_now() + interval '2 hours', 200000, 'ORDERED', @newOrderId,
                    public.app_now(), public.app_now()
                );

                SELECT flow.snapshot_order_loyalty(@newOrderId);

                -- Complete BOTH orders
                SELECT flow.complete_order(@promisedOrderId, 'SYSTEM', 'Door closed');
                SELECT flow.complete_order(@newOrderId, 'SYSTEM', 'Door closed');
                """;
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("kioskId", kioskId);
            cmd.Parameters.AddWithValue("promisedOrderId", promisedOrderId);
            cmd.Parameters.AddWithValue("promisedRequestId", promisedRequestId);
            cmd.Parameters.AddWithValue("newOrderId", newOrderId);
            cmd.Parameters.AddWithValue("newRequestId", newRequestId);
            cmd.Parameters.AddWithValue("promCode", $"FB-P1-{suffix}");
            cmd.Parameters.AddWithValue("newCode", $"FB-P2-{suffix}");
            cmd.Parameters.AddWithValue("promToken", $"trk-p1-{suffix}");
            cmd.Parameters.AddWithValue("newToken", $"trk-p2-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        // Verify: Customer receives points from promised order (2,000 points), but 0 from new order
        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT balance FROM identity.shop_loyalty_balances WHERE customer_id = @customerId AND seller_id = @sellerId;";
            checkCmd.Parameters.AddWithValue("customerId", customerId);
            checkCmd.Parameters.AddWithValue("sellerId", sellerId);
            var balance = (long)(await checkCmd.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(2000, balance);
        }
    }

    [Fact]
    public async Task RefundAndReplay_HandlesEarnSpent_PreservesDebt_BlocksRedemption()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var kioskId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var order1Id = Guid.NewGuid();
        var order1ReqId = Guid.NewGuid();
        var order2Id = Guid.NewGuid();
        var order3Id = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"Debt Refund Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, customerId, "Debt Customer", $"debt-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.seller_loyalty_settings (seller_id, enabled, earn_rate_percent)
                VALUES (@sellerId, true, 1.00);

                -- Step 1: Customer completes Order 1 and earns 1,000 points (total_amount = 100,000)
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @order1Id, @code1, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                    'DISPENSING', 100000, 0, 100000, 0, 0,
                    @token1, public.app_now(), public.app_now()
                );

                INSERT INTO ordering.web_requests (
                    id, customer_id, seller_id, product_id, kiosk_id, kind, instructions,
                    pickup_at, pickup_before, quoted_price, state, order_id, created_at, updated_at
                ) VALUES (
                    @order1ReqId, @customerId, @sellerId, '30000000-0000-0000-0000-000000000001'::uuid, @kioskId, 'STOCK', 'Preorder note',
                    public.app_now(), public.app_now() + interval '2 hours', 100000, 'ORDERED', @order1Id,
                    public.app_now(), public.app_now()
                );

                SELECT flow.snapshot_order_loyalty(@order1Id);
                SELECT flow.complete_order(@order1Id, 'SYSTEM', 'Door closed');

                -- Step 2: Customer spends all 1,000 points on Order 2 (balance becomes 0)
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @order2Id, @code2, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                    'AWAITING_PAYMENT', 50000, 1000, 49000, 1000, 0,
                    @token2, public.app_now(), public.app_now()
                );

                SELECT flow.snapshot_order_loyalty(@order2Id);
                SELECT flow.redeem_points(@customerId, 1000, @order2Id);

                -- Step 3: Order 1 is refunded 100,000 VND!
                -- Earn points (1,000) are already spent!
                -- Deficit must be recorded as DEBT = 1,000, balance = 0, no negative cash!
                SELECT flow.refund_shop_loyalty(@order1Id, 100000);

                -- Step 4: Replay of refund on Order 1 must be idempotent (exactly once)
                SELECT flow.refund_shop_loyalty(@order1Id, 100000);
                """;
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("kioskId", kioskId);
            cmd.Parameters.AddWithValue("order1Id", order1Id);
            cmd.Parameters.AddWithValue("order1ReqId", order1ReqId);
            cmd.Parameters.AddWithValue("order2Id", order2Id);
            cmd.Parameters.AddWithValue("code1", $"FB-R1-{suffix}");
            cmd.Parameters.AddWithValue("code2", $"FB-R2-{suffix}");
            cmd.Parameters.AddWithValue("token1", $"trk-r1-{suffix}");
            cmd.Parameters.AddWithValue("token2", $"trk-r2-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        // Verify balance = 0 and debt = 1,000
        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT balance, debt FROM identity.shop_loyalty_balances WHERE customer_id = @customerId AND seller_id = @sellerId;";
            checkCmd.Parameters.AddWithValue("customerId", customerId);
            checkCmd.Parameters.AddWithValue("sellerId", sellerId);
            await using var reader = await checkCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0, reader.GetInt64(0));
            Assert.Equal(1000, reader.GetInt64(1));
        }

        // Verify redemption is blocked due to active debt
        await using (var redeemCmd = conn.CreateCommand())
        {
            redeemCmd.CommandText = """
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at
                ) VALUES (
                    @order3Id, @code3, gen_random_uuid(), @customerId, @sellerId, @kioskId,
                    'AWAITING_PAYMENT', 50000, 0, 50000, 0, 0,
                    @token3, public.app_now(), public.app_now()
                );

                SELECT flow.redeem_points(@customerId, 100, @order3Id);
                """;
            redeemCmd.Parameters.AddWithValue("order3Id", order3Id);
            redeemCmd.Parameters.AddWithValue("code3", $"FB-R3-{suffix}");
            redeemCmd.Parameters.AddWithValue("token3", $"trk-r3-{suffix}");
            redeemCmd.Parameters.AddWithValue("customerId", customerId);
            redeemCmd.Parameters.AddWithValue("sellerId", sellerId);
            redeemCmd.Parameters.AddWithValue("kioskId", kioskId);

            var ex = await Assert.ThrowsAsync<PostgresException>(() => redeemCmd.ExecuteNonQueryAsync());
            Assert.Contains("nợ", ex.MessageText);
        }
    }
}
