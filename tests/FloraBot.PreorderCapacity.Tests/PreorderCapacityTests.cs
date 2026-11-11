using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FloraBot.Api.Auth;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.Ordering;
using FloraBot.Api.ReadModels;
using FloraBot.Api.Realtime;
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
    public TestEmailSender Email { get; } = new();

    public sealed class TestEmailSender : ITransactionalEmailSender
    {
        public List<TransactionalEmail> Messages { get; } = [];

        public Task SendAsync(TransactionalEmail email, CancellationToken ct)
        {
            Messages.Add(email);
            return Task.CompletedTask;
        }
    }

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
            ["EMAIL_PROVIDER"] = "Brevo",
            ["EMAIL_SENDER"] = "noreply@test.local",
            ["BREVO_API_KEY"] = "test-only-replaced-sender",
        };
        foreach (var pair in settings) builder.UseSetting(pair.Key, pair.Value);
        builder.ConfigureServices(services => services.AddSingleton<ITransactionalEmailSender>(Email));

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
            INSERT INTO catalog.flower_products(id, seller_id, name, price, shelf_life_hours, status, created_at, updated_at, quantity)
            VALUES(@product, @seller, 'Cap Product', 50000, 48, 'ACTIVE', public.app_now(), public.app_now(), 1)
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
    public async Task WebRequest_RejectsNewRequestWhenFullButAllowsExistingRetry()
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 1);
        var pickupAt = DateTimeOffset.UtcNow.AddDays(1);
        var quoted = await CreateQuotedPreorderAsync(f, pickupAt);
        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await data.OpenConnectionAsync();
        await using var request = connection.CreateCommand();
        request.CommandText = """
            SELECT flow.web_request(@id, @customer, 'CUSTOM', @product, @kiosk,
                NULL::uuid, @pickup, 'Please prepare a nice bouquet')
            """;
        var first = Guid.CreateVersion7();
        request.Parameters.AddWithValue("id", first);
        request.Parameters.AddWithValue("customer", f.CustomerUserId);
        request.Parameters.AddWithValue("product", f.ProductId);
        request.Parameters.AddWithValue("kiosk", f.KioskId);
        request.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
        Assert.Equal(first, (Guid)(await request.ExecuteScalarAsync())!);

        var catalogPath = $"/api/shop/catalog?search={f.SellerId:N}";
        var eligiblePath = catalogPath + "&availablePreorderOnly=true";
        Assert.Equal(f.KioskId, Assert.Single((await _client.GetFromJsonAsync<WebCatalogPage>(eligiblePath))!.Items).KioskId);

        await using var accept = connection.CreateCommand();
        accept.CommandText = "SELECT flow.web_accept(@id, @customer, 50000::bigint, @pickup, 'Ready for you')";
        accept.Parameters.AddWithValue("id", quoted);
        accept.Parameters.AddWithValue("customer", f.CustomerUserId);
        accept.Parameters.AddWithValue("pickup", pickupAt.UtcDateTime);
        await accept.ExecuteScalarAsync();

        // Keep product stock positive so the catalog assertion tests kiosk capacity.
        await using var restock = connection.CreateCommand();
        restock.CommandText = "UPDATE catalog.flower_products SET quantity = 1 WHERE id = @product";
        restock.Parameters.AddWithValue("product", f.ProductId);
        await restock.ExecuteNonQueryAsync();

        Assert.Equal(first, (Guid)(await request.ExecuteScalarAsync())!);
        request.Parameters["id"].Value = Guid.CreateVersion7();
        var failure = await Assert.ThrowsAsync<PostgresException>(() => request.ExecuteScalarAsync());
        Assert.Equal("P0001", failure.SqlState);
        Assert.Empty((await _client.GetFromJsonAsync<WebCatalogPage>(eligiblePath))!.Items);
        Assert.Equal(f.KioskId, Assert.Single((await _client.GetFromJsonAsync<WebCatalogPage>(catalogPath))!.Items).KioskId);

        await using var cancel = connection.CreateCommand();
        cancel.CommandText = "SELECT flow.web_cancel(@id, @customer, 'Customer changed plans')";
        cancel.Parameters.AddWithValue("id", quoted);
        cancel.Parameters.AddWithValue("customer", f.CustomerUserId);
        await cancel.ExecuteNonQueryAsync();
        Assert.Equal(f.KioskId, Assert.Single((await _client.GetFromJsonAsync<WebCatalogPage>(eligiblePath))!.Items).KioskId);
    }

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
    public async Task AssignStaffTask_BindsPaidPreorderToOneSlotAndShopStaff()
    {
        var f = await SeedCapacityFixtureAsync();
        var other = await SeedCapacityFixtureAsync();
        var pickup = DateTimeOffset.UtcNow.AddDays(1);
        var requestId = await CreateQuotedPreorderAsync(f, pickup);
        var staffId = Guid.NewGuid();
        var externalStaffId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await data.OpenConnectionAsync();
        await using var setup = connection.CreateCommand();
        setup.CommandText = """
            INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id)
            VALUES (@staff, @staff::text || '@test.local', '!hash', 'Assigned staff', 'SELLER_STAFF', 'ACTIVE', @seller),
                   (@external, @external::text || '@test.local', '!hash', 'External staff', 'SELLER_STAFF', 'ACTIVE', @otherSeller);
            SELECT flow.web_accept(@request, @customer, 50000::bigint, @pickup, 'Ready for you');
            """;
        setup.Parameters.AddWithValue("staff", staffId);
        setup.Parameters.AddWithValue("external", externalStaffId);
        setup.Parameters.AddWithValue("seller", f.SellerId);
        setup.Parameters.AddWithValue("otherSeller", other.SellerId);
        setup.Parameters.AddWithValue("request", requestId);
        setup.Parameters.AddWithValue("customer", f.CustomerUserId);
        setup.Parameters.AddWithValue("pickup", pickup.UtcDateTime);
        await setup.ExecuteNonQueryAsync();

        await using var assign = connection.CreateCommand();
        assign.CommandText = "SELECT flow.assign_preorder_staff_task(@id, @actor, @staff, @request, @slot, 'Load the paid bouquet')";
        var taskId = Guid.NewGuid();
        assign.Parameters.AddWithValue("id", taskId);
        assign.Parameters.AddWithValue("actor", f.SellerUserId);
        assign.Parameters.AddWithValue("staff", staffId);
        assign.Parameters.AddWithValue("request", requestId);
        assign.Parameters.AddWithValue("slot", f.PreorderSlotIds[0]);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => assign.ExecuteScalarAsync())).SqlState);

        await using var payment = connection.CreateCommand();
        payment.CommandText = """
            UPDATE payment.payments SET status = 'SUCCEEDED', paid_at = public.app_now()
            WHERE checkout_id = (SELECT checkout_id FROM ordering.orders WHERE id =
                (SELECT order_id FROM ordering.web_requests WHERE id = @request));
            UPDATE ordering.orders SET status = 'PAID'
            WHERE id = (SELECT order_id FROM ordering.web_requests WHERE id = @request);
            """;
        payment.Parameters.AddWithValue("request", requestId);
        await payment.ExecuteNonQueryAsync();

        assign.Parameters["slot"].Value = other.PreorderSlotIds[0];
        Assert.Equal("P0002", (await Assert.ThrowsAsync<PostgresException>(() => assign.ExecuteScalarAsync())).SqlState);
        assign.Parameters["slot"].Value = f.PreorderSlotIds[0];
        assign.Parameters["staff"].Value = externalStaffId;
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => assign.ExecuteScalarAsync())).SqlState);
        assign.Parameters["staff"].Value = staffId;

        using var sellerClient = _factory.CreateClient();
        sellerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken("SELLER", f.SellerUserId, f.SellerId));
        var assignmentPath = $"/api/sellers/{f.SellerId}/preorders/{requestId}/assignment";
        using var result = await sellerClient.PostAsJsonAsync(assignmentPath,
            new AssignStaffPreorderInput(taskId, staffId, f.PreorderSlotIds[0], "Load the paid bouquet"));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(taskId, (await result.Content.ReadFromJsonAsync<StaffTaskResult>())!.Id);

        Assert.Equal(taskId, (Guid)(await assign.ExecuteScalarAsync())!);

        assign.Parameters["id"].Value = Guid.NewGuid();
        assign.Parameters["slot"].Value = f.PreorderSlotIds[1];
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => assign.ExecuteScalarAsync())).SqlState);

        using var staffClient = _factory.CreateClient();
        staffClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken("SELLER_STAFF", staffId, f.SellerId));
        await using var start = connection.CreateCommand();
        start.CommandText = "SELECT flow.transition_staff_task(@task, @staff, 1, 'IN_PROGRESS', 'Started loading task')";
        start.Parameters.AddWithValue("task", taskId);
        start.Parameters.AddWithValue("staff", staffId);
        await start.ExecuteScalarAsync();
        var options = await staffClient.GetFromJsonAsync<StaffPreorderOptions>($"/api/staff/tasks/{taskId}/preorders");
        Assert.Equal(requestId, Assert.Single(options!.Preorders).RequestId);
        Assert.Equal(f.PreorderSlotIds[0], Assert.Single(options.Slots).Id);
        using var directFulfill = await staffClient.PostAsJsonAsync(
            $"/api/staff/tasks/{taskId}/preorders/{requestId}/fulfill",
            new StaffPreorderFulfillInput(f.PreorderSlotIds[0], $"QR-TEST-{requestId:N}"));
        Assert.Equal(HttpStatusCode.NotFound, directFulfill.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingOtp_RequiresRightKioskAndFreshPresenceBeforePickup(bool assignedStaff)
    {
        var f = await SeedCapacityFixtureAsync(preorderSlotsCount: 1);
        var other = await SeedCapacityFixtureAsync(preorderSlotsCount: 1);
        var pickup = DateTimeOffset.UtcNow.AddDays(1);
        var requestId = await CreateQuotedPreorderAsync(f, pickup);
        var slot = f.PreorderSlotIds[0];
        var actor = assignedStaff ? Guid.NewGuid() : f.SellerUserId;
        var task = Guid.NewGuid();
        var kioskKey = Guid.NewGuid().ToString("N");
        var otherKey = Guid.NewGuid().ToString("N");
        using var services = _factory.Services.CreateScope();
        var data = services.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await data.OpenConnectionAsync();

        async Task<object?> Run(string sql, params (string Name, object Value)[] args)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
            return await command.ExecuteScalarAsync();
        }

        await Run("""
            SELECT flow.web_accept(@request,@customer,50000::bigint,@pickup,'Ready for you');
            UPDATE payment.payments SET status='SUCCEEDED',paid_at=public.app_now()
            WHERE checkout_id=(SELECT o.checkout_id FROM ordering.orders o JOIN ordering.web_requests w ON w.order_id=o.id WHERE w.id=@request);
            UPDATE ordering.orders SET status='PAID'
            WHERE id=(SELECT order_id FROM ordering.web_requests WHERE id=@request);
            UPDATE kiosk_ops.kiosks SET last_heartbeat_at=clock_timestamp(),api_key_hash=@key WHERE id=@kiosk;
            UPDATE kiosk_ops.kiosks SET last_heartbeat_at=clock_timestamp(),api_key_hash=@otherKey WHERE id=@other;
            INSERT INTO kiosk_ops.slot_sensor_capabilities(slot_id,kiosk_id,relay_channel,enabled,door_verified,presence_verified)
            VALUES(@slot,@kiosk,1,true,false,true);
            INSERT INTO kiosk_ops.flower_presence(kiosk_id,event_id,observed_at,states,received_at)
            VALUES(@kiosk,gen_random_uuid(),(extract(epoch FROM clock_timestamp())*1000)::bigint,ARRAY[-1,0,-1,-1],clock_timestamp());
            """, ("request", requestId), ("customer", f.CustomerUserId), ("pickup", pickup.UtcDateTime),
            ("slot", slot), ("kiosk", f.KioskId), ("other", other.KioskId),
            ("key", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(kioskKey)))),
            ("otherKey", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(otherKey)))));
        if (assignedStaff)
        {
            await Run("""
                INSERT INTO identity.users(id,email,password_hash,full_name,role,status,seller_id)
                VALUES(@actor,@actor::text || '@test.local','!hash','Loading Staff','SELLER_STAFF','ACTIVE',@seller);
                SELECT flow.assign_preorder_staff_task(@task,@sellerUser,@actor,@request,@slot,'Load the paid bouquet');
                SELECT flow.transition_staff_task(@task,@actor,1,'IN_PROGRESS','Started loading task');
                """, ("actor", actor), ("seller", f.SellerId), ("task", task),
                ("sellerUser", f.SellerUserId), ("request", requestId), ("slot", slot));
        }

        using var sellerClient = _factory.CreateClient();
        sellerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken("SELLER", f.SellerUserId, f.SellerId));
        using var staffClient = _factory.CreateClient();
        staffClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken("SELLER_STAFF", actor, f.SellerId));
        using var kioskClient = _factory.CreateClient();
        kioskClient.DefaultRequestHeaders.Add("X-Kiosk-Key", kioskKey);
        using var wrongKiosk = _factory.CreateClient();
        wrongKiosk.DefaultRequestHeaders.Add("X-Kiosk-Key", otherKey);

        var loader = assignedStaff ? staffClient : sellerClient;
        var issuePath = assignedStaff
            ? $"/api/staff/tasks/{task}/preorders/{requestId}/loading-access"
            : $"/api/me/preorders/{requestId}/loading-access";
        var fulfillPath = assignedStaff
            ? $"/api/staff/tasks/{task}/preorders/{requestId}/fulfill"
            : $"/api/sellers/{f.SellerId}/preorders/{requestId}/fulfill";
        var pickupPath = $"/api/me/preorders/{requestId}/pickup-otp";

        using var issue = await loader.PostAsJsonAsync(issuePath, new { slotId = slot });
        Assert.True(issue.IsSuccessStatusCode, await issue.Content.ReadAsStringAsync());
        var issued = JsonSerializer.Deserialize<JsonElement>(await issue.Content.ReadAsStringAsync());
        Assert.False(issued.TryGetProperty("code", out _));
        var access = issued.GetProperty("id").GetGuid();
        var email = _factory.Email.Messages.Last();
        Assert.Equal((assignedStaff ? actor.ToString() : actor.ToString("N")) + "@test.local", email.Recipient);
        var code = Regex.Match(email.Text, @"\b[0-9]{6}\b").Value;
        Assert.NotEmpty(code);

        using var resend = await loader.PostAsJsonAsync(issuePath, new { slotId = slot });
        Assert.Equal(HttpStatusCode.TooManyRequests, resend.StatusCode);
        var confirmPath = $"/api/kiosks/{f.KioskId}/maintenance-access/{access}/confirm";
        using var crossKiosk = await wrongKiosk.PostAsJsonAsync(confirmPath, new { code });
        Assert.Equal(HttpStatusCode.Forbidden, crossKiosk.StatusCode);
        using var badCode = await kioskClient.PostAsJsonAsync(confirmPath,
            new { code = code == "000000" ? "000001" : "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, badCode.StatusCode);
        using var unopened = await loader.PostAsJsonAsync(fulfillPath,
            new { slotId = slot, qrCode = $"QR-LOAD-{requestId:N}", loadingAccessId = access });
        Assert.Equal(HttpStatusCode.NotFound, unopened.StatusCode);
        using var earlyPickup = await sellerClient.PostAsync(pickupPath, null);
        Assert.Equal(HttpStatusCode.Conflict, earlyPickup.StatusCode);

        using var opened = await kioskClient.PostAsJsonAsync(confirmPath, new { code });
        Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync());
        using var resumed = await loader.PostAsJsonAsync(issuePath, new { slotId = slot });
        Assert.True(resumed.IsSuccessStatusCode, await resumed.Content.ReadAsStringAsync());
        var resumedBody = JsonSerializer.Deserialize<JsonElement>(await resumed.Content.ReadAsStringAsync());
        Assert.Equal(access, resumedBody.GetProperty("id").GetGuid());
        Assert.False(resumedBody.GetProperty("requiresCode").GetBoolean());
        if (!assignedStaff)
        {
            var replacementStaff = Guid.NewGuid();
            await Run("""
                INSERT INTO identity.users(id,email,password_hash,full_name,role,status,seller_id)
                VALUES(@staff,@staff::text || '@test.local','!hash','Replacement Staff','SELLER_STAFF','ACTIVE',@seller)
                """, ("staff", replacementStaff), ("seller", f.SellerId));
            var busyAssignment = await Assert.ThrowsAsync<PostgresException>(() => Run(
                "SELECT flow.assign_preorder_staff_task(@task,@actor,@staff,@request,@slot,'Load the paid bouquet')",
                ("task", Guid.NewGuid()), ("actor", f.SellerUserId), ("staff", replacementStaff),
                ("request", requestId), ("slot", slot)));
            Assert.Equal("40001", busyAssignment.SqlState);
        }
        using var replayOtp = await kioskClient.PostAsJsonAsync(confirmPath, new { code });
        Assert.Equal(HttpStatusCode.NotFound, replayOtp.StatusCode);

        var commandId = (Guid)(await Run(
            "SELECT command_id FROM kiosk_ops.preorder_loading_sessions WHERE challenge_id=@id", ("id", access)))!;
        // Exercise signed command dispatch, durable ACK processing and signed presence without a broker.
        var hardware = "HW-" + f.KioskId.ToString("N");
        var deviceKey = RandomNumberGenerator.GetBytes(32);
        var ring = new DeviceKeyRing(new Dictionary<string, byte[]> { [hardware] = deviceKey });
        var publisher = services.ServiceProvider.GetRequiredService<ICapPublisher>();
        var dispatcher = new DeviceCommandDispatcher(data, publisher, ring);
        var inbox = new DeviceEventInbox(data, publisher, ring,
            services.ServiceProvider.GetRequiredService<PortalNotifier>());
        var prepared = await dispatcher.PrepareAsync(commandId, CancellationToken.None);
        Assert.NotNull(prepared);
        Assert.Equal(slot, prepared.Command.SlotId);
        Assert.Equal(1, prepared.Command.RelayChannel);
        var ack = DeviceProtocol.Sign(new SignedDeviceEvent(1, hardware, Guid.NewGuid(), commandId,
            "ACK", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ""), deviceKey);
        Assert.Equal(DeviceReceipt.Rejected, await inbox.ReceiveAsync(hardware,
            ack with { Signature = new string('0', 64) }, CancellationToken.None));
        Assert.True(await dispatcher.DeliverAsync(prepared, async (_, ct) =>
        {
            Assert.Equal(DeviceReceipt.Stored, await inbox.ReceiveAsync(hardware, ack, ct));
        }, CancellationToken.None));
        Assert.True(await inbox.ProcessAsync(ack.EventId, CancellationToken.None));
        var requestBody = new { slotId = slot, qrCode = $"QR-LOAD-{requestId:N}", loadingAccessId = access };
        using var empty = await loader.PostAsJsonAsync(fulfillPath, requestBody);
        Assert.Equal(HttpStatusCode.Conflict, empty.StatusCode);
        await Run("""
            UPDATE kiosk_ops.flower_presence SET states=ARRAY[1,0,-1,-1],
                observed_at=(extract(epoch FROM clock_timestamp())*1000)::bigint,
                received_at=clock_timestamp() WHERE kiosk_id=@kiosk
            """, ("kiosk", f.KioskId));
        using var wrongRelay = await loader.PostAsJsonAsync(fulfillPath, requestBody);
        Assert.Equal(HttpStatusCode.Conflict, wrongRelay.StatusCode);
        await Run("""
            UPDATE kiosk_ops.flower_presence SET states=ARRAY[-1,1,-1,-1],
                observed_at=(extract(epoch FROM clock_timestamp()-interval '60 seconds')*1000)::bigint,
                received_at=clock_timestamp()-interval '60 seconds' WHERE kiosk_id=@kiosk
            """, ("kiosk", f.KioskId));
        using var stalePresence = await loader.PostAsJsonAsync(fulfillPath, requestBody);
        Assert.Equal(HttpStatusCode.Conflict, stalePresence.StatusCode);
        var presence = new FlowerPresenceReceiver(data, ring);
        var reading = FlowerPresenceProtocol.Sign(new SignedFlowerPresence(1, hardware, Guid.NewGuid(),
            "FLOWER_PRESENCE", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [-1, 1, -1, -1], ""), deviceKey);
        Assert.False(await presence.ReceiveAsync(hardware,
            reading with { Signature = new string('0', 64) }, CancellationToken.None));
        Assert.True(await presence.ReceiveAsync(hardware, reading, CancellationToken.None));
        using var beforeExplicit = await sellerClient.PostAsync(pickupPath, null);
        Assert.Equal(HttpStatusCode.Conflict, beforeExplicit.StatusCode);
        using var loaded = await loader.PostAsJsonAsync(fulfillPath, requestBody);
        Assert.True(loaded.IsSuccessStatusCode, await loaded.Content.ReadAsStringAsync());
        using var replayLoad = await loader.PostAsJsonAsync(fulfillPath, requestBody);
        Assert.Equal(await loaded.Content.ReadAsStringAsync(), await replayLoad.Content.ReadAsStringAsync());
        using var changedQr = await loader.PostAsJsonAsync(fulfillPath,
            new { slotId = slot, qrCode = $"DIFFERENT-{requestId:N}", loadingAccessId = access });
        Assert.Equal(HttpStatusCode.Conflict, changedQr.StatusCode);
        using var pickupAllowed = await sellerClient.PostAsync(pickupPath, null);
        Assert.True(pickupAllowed.IsSuccessStatusCode, await pickupAllowed.Content.ReadAsStringAsync());
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
            cmd.CommandText = "SELECT flow.web_fulfill(@id, @seller, @actor, @slot, @qr)";
            cmd.Parameters.AddWithValue("id", reqId);
            cmd.Parameters.AddWithValue("seller", f.SellerId);
            cmd.Parameters.AddWithValue("actor", f.SellerUserId);
            cmd.Parameters.AddWithValue("slot", f.PreorderSlotIds[0]);
            cmd.Parameters.AddWithValue("qr", $"QR-TEST-{reqId:N}");
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
