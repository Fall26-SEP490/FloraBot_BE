using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Ordering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace FloraBot.PreorderCapacity.Tests;

public class PreorderCapacityTestFactory : WebApplicationFactory<Program>
{
    public string DatabaseUrl { get; }
    public string ValkeyUrl { get; }

    public PreorderCapacityTestFactory()
    {
        DatabaseUrl = RequireDatabaseUrl();
        ValkeyUrl = RequireValkeyUrl();
    }

    public static string RequireDatabaseUrl()
    {
        var dbUrl = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(dbUrl))
        {
            throw new InvalidOperationException("TEST_DATABASE_URL environment variable is required and must not be empty.");
        }
        return dbUrl;
    }

    public static string RequireValkeyUrl()
    {
        var valkeyUrl = Environment.GetEnvironmentVariable("TEST_VALKEY_URL");
        if (string.IsNullOrWhiteSpace(valkeyUrl))
        {
            throw new InvalidOperationException("TEST_VALKEY_URL environment variable is required and must not be empty.");
        }
        return valkeyUrl;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());

        var valkeySetting = ValkeyUrl.Contains("abortConnect")
            ? ValkeyUrl
            : $"{ValkeyUrl},abortConnect=false";

        var settings = new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = DatabaseUrl,
            ["VALKEY_URL"] = valkeySetting,
            ["JWT_SIGNING_KEY"] = "test-only-key-at-least-thirty-two-bytes-long-not-production",
            ["PAYOS_CHECKSUM_KEY"] = "test-only-checksum-key",
            ["JOBS_ENABLED"] = "false",
            ["MQTT_ENABLED"] = "false",
        };
        foreach (var pair in settings) builder.UseSetting(pair.Key, pair.Value);

        // Production Program.cs already maps MapPreorderCapacity; no duplicate TestStartupFilter.
    }

    public string CreateToken(string role, Guid userId, Guid? sellerId = null, string status = "APPROVED")
    {
        using var scope = Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();
        var user = new User
        {
            Id = userId,
            Role = role,
            FullName = $"{role} User",
            SellerId = role is "SELLER" or "SELLER_STAFF" ? sellerId : null
        };
        return tokenService.Issue(user, status);
    }
}

public sealed class PreorderCapacityUnitTests
{
    [Fact]
    public void ResponseContract_ExcludesCustomerIdentityBankTokens()
    {
        var responseType = typeof(KioskPreorderAvailabilityResponse);
        var propertyNames = responseType.GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();

        Assert.DoesNotContain("customer", propertyNames);
        Assert.DoesNotContain("bank", propertyNames);
        Assert.DoesNotContain("account", propertyNames);
        Assert.DoesNotContain("token", propertyNames);
        Assert.DoesNotContain("secret", propertyNames);
        Assert.DoesNotContain("password", propertyNames);
        Assert.DoesNotContain("apikey", propertyNames);
    }

    [Fact]
    public void AvailabilityResponse_PropertiesMatchContract()
    {
        var kioskId = Guid.CreateVersion7();
        var resp = new KioskPreorderAvailabilityResponse(
            KioskId: kioskId,
            KioskCode: "K01",
            KioskName: "Kiosk Name",
            Available: true,
            TotalPreorderCapacity: 4,
            OccupiedPreorderCount: 1,
            AvailablePreorderCapacity: 3,
            Status: "AVAILABLE",
            Message: "Kiosk sẵn sàng nhận đơn đặt trước (ước tính khả dụng).",
            IsEstimateOnly: true
        );

        Assert.Equal(kioskId, resp.KioskId);
        Assert.True(resp.Available);
        Assert.Equal(4, resp.TotalPreorderCapacity);
        Assert.Equal(1, resp.OccupiedPreorderCount);
        Assert.Equal(3, resp.AvailablePreorderCapacity);
        Assert.Equal("AVAILABLE", resp.Status);
        Assert.True(resp.IsEstimateOnly);
    }
}

public sealed class PreorderCapacityIntegrationTests : IClassFixture<PreorderCapacityTestFactory>
{
    private readonly PreorderCapacityTestFactory _factory;
    private readonly HttpClient _client;

    public PreorderCapacityIntegrationTests(PreorderCapacityTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private void EnsureDatabaseConfigured()
    {
        Assert.False(
            string.IsNullOrWhiteSpace(_factory.DatabaseUrl),
            "TEST_DATABASE_URL is required for integration tests.");
    }

    private sealed record CapacityFixture(
        Guid SellerId,
        Guid SellerUserId,
        Guid CustomerUserId,
        Guid ProductId,
        Guid KioskId,
        Guid SubscriptionId,
        List<Guid> PreorderSlotIds
    );

    private async Task<CapacityFixture> SeedCapacityFixtureAsync(int preorderSlotsCount = 2, bool activeLease = true)
    {
        EnsureDatabaseConfigured();

        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await data.OpenConnectionAsync();

        var f = new CapacityFixture(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Enumerable.Range(0, preorderSlotsCount).Select(_ => Guid.CreateVersion7()).ToList()
        );

        var paymentId = Guid.CreateVersion7();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            -- 1. Insert seller
            INSERT INTO identity.sellers(id, shop_name, phone, address, status, package_id, package_expires_at, bank_name, bank_holder, bank_account_no_enc, created_at, updated_at)
            VALUES(@seller, 'Cap Shop ' || replace(@seller::text, '-', ''), '0912345678', '123 Address', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30, 'Vietcombank', 'TEST SELLER', 'enc:v1:test', public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 2. Insert seller user
            INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id, created_at, updated_at)
            VALUES(@sellerUser, replace(@sellerUser::text, '-', '') || '@test.local', '!hash', 'Seller User', 'SELLER', 'ACTIVE', @seller, public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 3. Insert customer user
            INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id, created_at, updated_at)
            VALUES(@customerUser, replace(@customerUser::text, '-', '') || '@test.local', '!hash', 'Customer User', 'CUSTOMER', 'ACTIVE', NULL, public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 4. Insert active flower product
            INSERT INTO catalog.flower_products(id, seller_id, name, price, shelf_life_hours, status, created_at, updated_at)
            VALUES(@product, @seller, 'Cap Product', 50000, 48, 'ACTIVE', public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 5. Insert kiosk with location and seller (if activeLease)
            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, latitude, longitude, status, seller_id, created_at, updated_at)
            VALUES(@kiosk, 'K-' || replace(@kiosk::text, '-', ''), 'Capacity Kiosk', '123 Test Street', 'HCM', 'HW-' || replace(@kiosk::text, '-', ''), 'MQTT-' || replace(@kiosk::text, '-', ''), 10.762622, 106.660172, 'ONLINE', CASE WHEN @activeLease THEN @seller ELSE NULL END, public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("seller", f.SellerId);
        cmd.Parameters.AddWithValue("sellerUser", f.SellerUserId);
        cmd.Parameters.AddWithValue("customerUser", f.CustomerUserId);
        cmd.Parameters.AddWithValue("product", f.ProductId);
        cmd.Parameters.AddWithValue("kiosk", f.KioskId);
        cmd.Parameters.AddWithValue("activeLease", activeLease);
        await cmd.ExecuteNonQueryAsync();

        if (activeLease)
        {
            await using var subCmd = conn.CreateCommand();
            subCmd.CommandText = """
                -- 6. Insert subscription FIRST with payment_id = NULL
                INSERT INTO identity.subscriptions(id, seller_id, package_id, kiosk_id, period_from, period_to, price, status, payment_id, created_at, updated_at)
                VALUES(@sub, @seller, '20000000-0000-0000-0000-00000000000a', @kiosk, CURRENT_DATE - 10, CURRENT_DATE + 30, 400000, 'PENDING_PAYMENT', NULL, public.app_now(), public.app_now())
                ON CONFLICT (id) DO NOTHING;

                -- 7. Insert payment with subscription_id = @sub
                INSERT INTO payment.payments(id, kind, purpose, subscription_id, gateway, idempotency_key, amount, status, paid_at, created_at, updated_at)
                VALUES(@payment, 'CHARGE', 'SUBSCRIPTION', @sub, 'PAYOS', 'sub-pay-' || replace(@sub::text, '-', ''), 400000, 'SUCCEEDED', public.app_now(), public.app_now(), public.app_now())
                ON CONFLICT (id) DO NOTHING;

                -- 8. Update subscription to ACTIVE
                UPDATE identity.subscriptions
                SET payment_id = @payment, status = 'ACTIVE', updated_at = public.app_now()
                WHERE id = @sub;
                """;
            subCmd.Parameters.AddWithValue("seller", f.SellerId);
            subCmd.Parameters.AddWithValue("kiosk", f.KioskId);
            subCmd.Parameters.AddWithValue("sub", f.SubscriptionId);
            subCmd.Parameters.AddWithValue("payment", paymentId);
            await subCmd.ExecuteNonQueryAsync();
        }

        // 9. Insert PREORDER slots
        // Active assignment requires actual kiosk owner; when activeLease is false, slots remain FREE with no assignment.
        for (int i = 0; i < f.PreorderSlotIds.Count; i++)
        {
            var slotId = f.PreorderSlotIds[i];
            await using var slotCmd = conn.CreateCommand();
            if (activeLease)
            {
                slotCmd.CommandText = """
                    INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel, status, current_seller_id, zone, row_version, created_at, updated_at)
                    VALUES(@slot, @kiosk, 'P0' || @idx, @idx, 'RENTED_EMPTY', @seller, 'PREORDER', 1, public.app_now(), public.app_now())
                    ON CONFLICT (id) DO NOTHING;

                    INSERT INTO kiosk_ops.slot_assignments(slot_id, seller_id, assigned_by, assigned_at, status)
                    VALUES(@slot, @seller, @sellerUser, public.app_now(), 'ACTIVE')
                    ON CONFLICT DO NOTHING;
                    """;
                slotCmd.Parameters.AddWithValue("seller", f.SellerId);
                slotCmd.Parameters.AddWithValue("sellerUser", f.SellerUserId);
            }
            else
            {
                slotCmd.CommandText = """
                    INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel, status, current_seller_id, zone, row_version, created_at, updated_at)
                    VALUES(@slot, @kiosk, 'P0' || @idx, @idx, 'FREE', NULL, 'PREORDER', 1, public.app_now(), public.app_now())
                    ON CONFLICT (id) DO NOTHING;
                    """;
            }
            slotCmd.Parameters.AddWithValue("slot", slotId);
            slotCmd.Parameters.AddWithValue("kiosk", f.KioskId);
            slotCmd.Parameters.AddWithValue("idx", i + 1);
            await slotCmd.ExecuteNonQueryAsync();
        }

        return f;
    }

    private async Task<Guid> CreateQuotedPreorderAsync(CapacityFixture f, DateTimeOffset pickupAt, long price = 50000)
    {
        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await data.OpenConnectionAsync();

        var reqId = Guid.CreateVersion7();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ordering.web_requests(id, customer_id, seller_id, product_id, kiosk_id, kind, instructions, pickup_at, pickup_before, quoted_price, quote_note, quote_expires_at, state, created_at, updated_at)
            VALUES(@id, @customer, @seller, @product, @kiosk, 'CUSTOM', 'Please prepare nice bouquet', @pickup, @pickup + interval '2 hours', @price, 'Ready for you', public.app_now() + interval '24 hours', 'QUOTED', public.app_now(), public.app_now());
            """;
        cmd.Parameters.AddWithValue("id", reqId);
        cmd.Parameters.AddWithValue("customer", f.CustomerUserId);
        cmd.Parameters.AddWithValue("seller", f.SellerId);
        cmd.Parameters.AddWithValue("product", f.ProductId);
        cmd.Parameters.AddWithValue("kiosk", f.KioskId);
        cmd.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
        cmd.Parameters.AddWithValue("price", price);
        await cmd.ExecuteNonQueryAsync();
        return reqId;
    }

    #region Discovery API Tests

    [Fact]
    public async Task Availability_NoFakeLeaseData_WhenNoActiveLease_ReturnsUnavailable()
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 2, activeLease: false);

        var resp = await _client.GetAsync($"/api/kiosks/{f.KioskId}/preorder-availability");
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"Expected OK, got {resp.StatusCode}: {raw}");

        var body = JsonSerializer.Deserialize<KioskPreorderAvailabilityResponse>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(body);
        Assert.False(body.Available);
        Assert.Equal("NO_ACTIVE_LEASE", body.Status);
    }

    [Fact]
    public async Task Availability_WrongTime_ReturnsBadRequest()
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 2);

        // Past time must be rejected
        var pastTime = DateTimeOffset.UtcNow.AddHours(-1);
        var respPast = await _client.GetAsync($"/api/kiosks/{f.KioskId}/preorder-availability?pickupAt={Uri.EscapeDataString(pastTime.ToString("O"))}");
        Assert.Equal(HttpStatusCode.BadRequest, respPast.StatusCode);
    }

    [Fact]
    public async Task Availability_FuturePickup_NotArbitrarilyRejectedBy30DayCap()
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 2);

        // Future pickup date (> 30 days) must NOT be rejected by discovery availability endpoint (estimate only)
        var farFuture = DateTimeOffset.UtcNow.AddDays(45);
        var respFuture = await _client.GetAsync($"/api/kiosks/{f.KioskId}/preorder-availability?pickupAt={Uri.EscapeDataString(farFuture.ToString("O"))}");
        var raw = await respFuture.Content.ReadAsStringAsync();
        Assert.True(respFuture.StatusCode == HttpStatusCode.OK, $"Expected OK, got {respFuture.StatusCode}: {raw}");

        var body = JsonSerializer.Deserialize<KioskPreorderAvailabilityResponse>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(body);
        Assert.True(body.Available);
        Assert.True(body.IsEstimateOnly);
    }

    [Fact]
    public async Task Availability_HappyPath_ReturnsAvailableCapacity()
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 3);

        var pickupAt = DateTimeOffset.UtcNow.AddDays(2);
        var resp = await _client.GetAsync($"/api/kiosks/{f.KioskId}/preorder-availability?pickupAt={Uri.EscapeDataString(pickupAt.ToString("O"))}");
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"Expected OK, got {resp.StatusCode}: {raw}");

        var body = JsonSerializer.Deserialize<KioskPreorderAvailabilityResponse>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(body);
        Assert.True(body.Available);
        Assert.Equal(3, body.TotalPreorderCapacity);
        Assert.Equal(0, body.OccupiedPreorderCount);
        Assert.Equal(3, body.AvailablePreorderCapacity);
        Assert.Equal("AVAILABLE", body.Status);
    }

    #endregion

    #region Atomic Capacity Commitment & Concurrency Tests

    [Fact]
    public async Task WebAccept_Idempotent_ReturnsSameOrder()
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 1);
        var pickupAt = DateTimeOffset.UtcNow.AddDays(1);
        var reqId = await CreateQuotedPreorderAsync(f, pickupAt);

        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await data.OpenConnectionAsync();

        // First accept
        await using var cmd1 = conn.CreateCommand();
        cmd1.CommandText = "SELECT flow.web_accept(@id, @customer, @price, @pickup, @note)";
        cmd1.Parameters.AddWithValue("id", reqId);
        cmd1.Parameters.AddWithValue("customer", f.CustomerUserId);
        cmd1.Parameters.AddWithValue("price", 50000L);
        cmd1.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
        cmd1.Parameters.AddWithValue("note", "Ready for you");
        var order1 = (Guid)(await cmd1.ExecuteScalarAsync())!;

        // Second accept (idempotent call)
        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT flow.web_accept(@id, @customer, @price, @pickup, @note)";
        cmd2.Parameters.AddWithValue("id", reqId);
        cmd2.Parameters.AddWithValue("customer", f.CustomerUserId);
        cmd2.Parameters.AddWithValue("price", 50000L);
        cmd2.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
        cmd2.Parameters.AddWithValue("note", "Ready for you");
        var order2 = (Guid)(await cmd2.ExecuteScalarAsync())!;

        Assert.Equal(order1, order2);
    }

    [Fact]
    public async Task WebAccept_NoOversell_WhenCapacityFull_ReturnsConflict()
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 1);
        var pickupAt = DateTimeOffset.UtcNow.AddDays(1);
        var req1 = await CreateQuotedPreorderAsync(f, pickupAt);
        var req2 = await CreateQuotedPreorderAsync(f, pickupAt);

        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await data.OpenConnectionAsync();

        // First accept consumes the 1 slot
        await using var cmd1 = conn.CreateCommand();
        cmd1.CommandText = "SELECT flow.web_accept(@id, @customer, @price, @pickup, @note)";
        cmd1.Parameters.AddWithValue("id", req1);
        cmd1.Parameters.AddWithValue("customer", f.CustomerUserId);
        cmd1.Parameters.AddWithValue("price", 50000L);
        cmd1.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
        cmd1.Parameters.AddWithValue("note", "Ready for you");
        await cmd1.ExecuteNonQueryAsync();

        // Second accept attempts to oversell -> MUST throw P0001 (409 Conflict)
        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT flow.web_accept(@id, @customer, @price, @pickup, @note)";
        cmd2.Parameters.AddWithValue("id", req2);
        cmd2.Parameters.AddWithValue("customer", f.CustomerUserId);
        cmd2.Parameters.AddWithValue("price", 50000L);
        cmd2.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
        cmd2.Parameters.AddWithValue("note", "Ready for you");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd2.ExecuteNonQueryAsync());
        Assert.Equal("P0001", ex.SqlState);
    }

    [Fact]
    public async Task WebAccept_LastUnitRace_TwoAccepts_OneWins_One409()
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 1);
        var pickupAt = DateTimeOffset.UtcNow.AddDays(1);
        var req1 = await CreateQuotedPreorderAsync(f, pickupAt);
        var req2 = await CreateQuotedPreorderAsync(f, pickupAt);

        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        async Task<bool> TryAcceptAsync(Guid reqId)
        {
            try
            {
                await using var conn = await data.OpenConnectionAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT flow.web_accept(@id, @customer, @price, @pickup, @note)";
                cmd.Parameters.AddWithValue("id", reqId);
                cmd.Parameters.AddWithValue("customer", f.CustomerUserId);
                cmd.Parameters.AddWithValue("price", 50000L);
                cmd.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
                cmd.Parameters.AddWithValue("note", "Ready for you");
                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return false;
            }
        }

        var results = await Task.WhenAll(TryAcceptAsync(req1), TryAcceptAsync(req2));

        // Exactly one must win, one must receive 409 (P0001)
        Assert.Single(results, r => r);
        Assert.Single(results, r => !r);
    }

    [Fact]
    public async Task Cancellation_ReleasesReservationOnce()
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 1);
        var pickupAt = DateTimeOffset.UtcNow.AddDays(1);
        var req1 = await CreateQuotedPreorderAsync(f, pickupAt);
        var req2 = await CreateQuotedPreorderAsync(f, pickupAt);

        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await data.OpenConnectionAsync();

        // 1. Accept req1
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT flow.web_accept(@id, @customer, @price, @pickup, @note)";
            cmd.Parameters.AddWithValue("id", req1);
            cmd.Parameters.AddWithValue("customer", f.CustomerUserId);
            cmd.Parameters.AddWithValue("price", 50000L);
            cmd.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
            cmd.Parameters.AddWithValue("note", "Ready for you");
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Cancel req1
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT flow.web_cancel(@id, @customer, 'Customer changed plans')";
            cmd.Parameters.AddWithValue("id", req1);
            cmd.Parameters.AddWithValue("customer", f.CustomerUserId);
            await cmd.ExecuteNonQueryAsync();
        }

        // 3. Now req2 can be accepted successfully
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT flow.web_accept(@id, @customer, @price, @pickup, @note)";
            cmd.Parameters.AddWithValue("id", req2);
            cmd.Parameters.AddWithValue("customer", f.CustomerUserId);
            cmd.Parameters.AddWithValue("price", 50000L);
            cmd.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
            cmd.Parameters.AddWithValue("note", "Ready for you");
            var orderId = await cmd.ExecuteScalarAsync();
            Assert.NotNull(orderId);
        }
    }

    [Fact]
    public async Task Fulfill_DoesNotDoubleCountStock()
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 1);
        var pickupAt = DateTimeOffset.UtcNow.AddDays(1);
        var reqId = await CreateQuotedPreorderAsync(f, pickupAt);

        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await data.OpenConnectionAsync();

        // 1. Accept quote
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT flow.web_accept(@id, @customer, @price, @pickup, @note)";
            cmd.Parameters.AddWithValue("id", reqId);
            cmd.Parameters.AddWithValue("customer", f.CustomerUserId);
            cmd.Parameters.AddWithValue("price", 50000L);
            cmd.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
            cmd.Parameters.AddWithValue("note", "Ready for you");
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Mark payment SUCCEEDED and order PAID so fulfillment can proceed
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE payment.payments SET status = 'SUCCEEDED', paid_at = public.app_now()
                WHERE checkout_id = (SELECT checkout_id FROM ordering.orders WHERE id = (SELECT order_id FROM ordering.web_requests WHERE id = @id));

                UPDATE ordering.orders SET status = 'PAID'
                WHERE id = (SELECT order_id FROM ordering.web_requests WHERE id = @id);
                """;
            cmd.Parameters.AddWithValue("id", reqId);
            await cmd.ExecuteNonQueryAsync();
        }

        // 3. Check availability before fulfill: 1 total, 1 occupied, 0 available
        var respBefore = await _client.GetAsync($"/api/kiosks/{f.KioskId}/preorder-availability");
        var rawBefore = await respBefore.Content.ReadAsStringAsync();
        Assert.True(respBefore.StatusCode == HttpStatusCode.OK, $"Expected OK, got {respBefore.StatusCode}: {rawBefore}");
        var bodyBefore = JsonSerializer.Deserialize<KioskPreorderAvailabilityResponse>(rawBefore, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(bodyBefore);
        Assert.Equal(1, bodyBefore.OccupiedPreorderCount);
        Assert.Equal(0, bodyBefore.AvailablePreorderCapacity);

        // 4. Fulfill the order into the PREORDER slot
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT flow.web_fulfill(@id, @seller, @actor, @slot, 'QR-TEST-FULFILL')";
            cmd.Parameters.AddWithValue("id", reqId);
            cmd.Parameters.AddWithValue("seller", f.SellerId);
            cmd.Parameters.AddWithValue("actor", f.SellerUserId);
            cmd.Parameters.AddWithValue("slot", f.PreorderSlotIds[0]);
            await cmd.ExecuteNonQueryAsync();
        }

        // 5. Check availability after fulfill: occupied count MUST STILL BE 1 (NOT 2)
        var respAfter = await _client.GetAsync($"/api/kiosks/{f.KioskId}/preorder-availability");
        var rawAfter = await respAfter.Content.ReadAsStringAsync();
        Assert.True(respAfter.StatusCode == HttpStatusCode.OK, $"Expected OK, got {respAfter.StatusCode}: {rawAfter}");
        var bodyAfter = JsonSerializer.Deserialize<KioskPreorderAvailabilityResponse>(rawAfter, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(bodyAfter);
        Assert.Equal(1, bodyAfter.OccupiedPreorderCount);
        Assert.Equal(0, bodyAfter.AvailablePreorderCapacity);
    }

    [Fact]
    public async Task NoCrossKioskZone()
    {
        var f1 = await SeedCapacityFixtureAsync(preorderSlotsCount: 1);
        var f2 = await SeedCapacityFixtureAsync(preorderSlotsCount: 1);

        var pickupAt = DateTimeOffset.UtcNow.AddDays(1);
        var req1 = await CreateQuotedPreorderAsync(f1, pickupAt);

        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await data.OpenConnectionAsync();

        // Accept on Kiosk 1
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT flow.web_accept(@id, @customer, @price, @pickup, @note)";
            cmd.Parameters.AddWithValue("id", req1);
            cmd.Parameters.AddWithValue("customer", f1.CustomerUserId);
            cmd.Parameters.AddWithValue("price", 50000L);
            cmd.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
            cmd.Parameters.AddWithValue("note", "Ready for you");
            await cmd.ExecuteNonQueryAsync();
        }

        // Kiosk 2 capacity is untouched (1 total, 0 occupied, 1 available)
        var resp2 = await _client.GetAsync($"/api/kiosks/{f2.KioskId}/preorder-availability");
        var raw2 = await resp2.Content.ReadAsStringAsync();
        Assert.True(resp2.StatusCode == HttpStatusCode.OK, $"Expected OK, got {resp2.StatusCode}: {raw2}");
        var body2 = JsonSerializer.Deserialize<KioskPreorderAvailabilityResponse>(raw2, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(body2);
        Assert.True(body2.Available);
        Assert.Equal(1, body2.TotalPreorderCapacity);
        Assert.Equal(0, body2.OccupiedPreorderCount);
        Assert.Equal(1, body2.AvailablePreorderCapacity);
    }

    [Fact]
    public async Task TerminatedKiosk_BlockedFromNewCommit()
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 1);
        var pickupAt = DateTimeOffset.UtcNow.AddDays(1);
        var reqId = await CreateQuotedPreorderAsync(f, pickupAt);

        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await data.OpenConnectionAsync();

        // Mark kiosk as terminating (ACCEPTED status in kiosk_terminations with review_reason >= 10 chars)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO kiosk_ops.kiosk_terminations(id, kiosk_id, seller_id, requested_by, reason, status, version, requested_at, review_action, review_reason, reviewed_at, reviewed_by)
                VALUES(public.uuid_v7(), @kiosk, @seller, @sellerUser, 'Terminating kiosk test', 'ACCEPTED', 2, public.app_now(), 'ACCEPT', 'Approved for handover termination', public.app_now(), @sellerUser)
                ON CONFLICT (kiosk_id) WHERE status IN ('REQUESTED', 'ACCEPTED')
                DO UPDATE SET status = 'ACCEPTED', review_reason = 'Approved for handover termination';
                """;
            cmd.Parameters.AddWithValue("kiosk", f.KioskId);
            cmd.Parameters.AddWithValue("seller", f.SellerId);
            cmd.Parameters.AddWithValue("sellerUser", f.SellerUserId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Attempt accept -> MUST fail with P0001
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT flow.web_accept(@id, @customer, @price, @pickup, @note)";
            cmd.Parameters.AddWithValue("id", reqId);
            cmd.Parameters.AddWithValue("customer", f.CustomerUserId);
            cmd.Parameters.AddWithValue("price", 50000L);
            cmd.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
            cmd.Parameters.AddWithValue("note", "Ready for you");

            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("P0001", ex.SqlState);
        }
    }

    [Fact]
    public async Task FixedRentalLayout_RejectsCapacityReductionWhileReservationsRemain()
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 1);
        var pickupAt = DateTimeOffset.UtcNow.AddDays(1);
        var reqId = await CreateQuotedPreorderAsync(f, pickupAt);

        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await data.OpenConnectionAsync();

        // Accept the preorder obligation
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT flow.web_accept(@id, @customer, @price, @pickup, @note)";
            cmd.Parameters.AddWithValue("id", reqId);
            cmd.Parameters.AddWithValue("customer", f.CustomerUserId);
            cmd.Parameters.AddWithValue("price", 50000L);
            cmd.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
            cmd.Parameters.AddWithValue("note", "Ready for you");
            await cmd.ExecuteNonQueryAsync();
        }

        // ADR0060 retires zone changes, including reductions that would invalidate reservations.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT flow.seller_set_slot_zone(@kiosk, @slot, @actor, 'WALK_IN')";
            cmd.Parameters.AddWithValue("kiosk", f.KioskId);
            cmd.Parameters.AddWithValue("slot", f.PreorderSlotIds[0]);
            cmd.Parameters.AddWithValue("actor", f.SellerUserId);

            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
            Assert.Contains("fixed at rental", ex.MessageText);
        }
    }

    #endregion
}
