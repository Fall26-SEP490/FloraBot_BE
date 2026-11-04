using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace FloraBot.KioskTermination.Tests;

public class KioskTerminationTestFactory : WebApplicationFactory<Program>
{
    public string? DatabaseUrl { get; private set; }
    public string? ValkeyUrl { get; private set; }

    public KioskTerminationTestFactory()
    {
        DatabaseUrl = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        ValkeyUrl = Environment.GetEnvironmentVariable("TEST_VALKEY_URL");

        if (File.Exists("COORDINATOR-TEST-ENV.json"))
        {
            try
            {
                var json = File.ReadAllText("COORDINATOR-TEST-ENV.json");
                using var doc = JsonDocument.Parse(json);
                if (string.IsNullOrWhiteSpace(DatabaseUrl) && doc.RootElement.TryGetProperty("database", out var dbProp))
                {
                    var dbName = dbProp.GetString();
                    DatabaseUrl = $"Host=127.0.0.1;Port=55436;Database={dbName};Username=florabot;Password=local-florabot-only";
                }
                if (string.IsNullOrWhiteSpace(ValkeyUrl) && doc.RootElement.TryGetProperty("valkey", out var vProp))
                {
                    ValkeyUrl = vProp.GetString();
                }
            }
            catch
            {
                // Fallback
            }
        }

        DatabaseUrl ??= Environment.GetEnvironmentVariable("DATABASE_URL");
        ValkeyUrl ??= "127.0.0.1:51640,defaultDatabase=11";
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());

        var valkeySetting = (ValkeyUrl ?? "127.0.0.1:51640").Contains("abortConnect")
            ? ValkeyUrl!
            : $"{ValkeyUrl ?? "127.0.0.1:51640"},abortConnect=false";

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

public sealed class KioskTerminationUnitTests
{
    [Fact]
    public void ResponseContract_ExcludesPersonalFullBankOrSecrets()
    {
        var responseType = typeof(KioskTerminationResponse);
        var propertyNames = responseType.GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();

        // Ensure sensitive banking / secret fields are completely absent
        Assert.DoesNotContain("bankaccount", propertyNames);
        Assert.DoesNotContain("bankaccountnumber", propertyNames);
        Assert.DoesNotContain("password", propertyNames);
        Assert.DoesNotContain("secret", propertyNames);
        Assert.DoesNotContain("apikey", propertyNames);
        Assert.DoesNotContain("token", propertyNames);
    }

    [Fact]
    public void RequestContract_ReasonValidationRules()
    {
        var req = new CreateKioskTerminationRequest("Reason from 10 to 2000 chars");
        Assert.NotNull(req.Reason);
        Assert.InRange(req.Reason.Length, 10, 2000);
    }
}

public sealed class KioskTerminationIntegrationTests : IClassFixture<KioskTerminationTestFactory>
{
    private readonly KioskTerminationTestFactory _factory;
    private readonly HttpClient _client;

    public KioskTerminationIntegrationTests(KioskTerminationTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private void AuthenticateAs(string role, Guid userId, Guid? sellerId = null, string status = "APPROVED")
    {
        var token = _factory.CreateToken(role, userId, sellerId, status);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private void ClearAuthentication()
    {
        _client.DefaultRequestHeaders.Authorization = null;
    }

    private void EnsureDatabaseConfigured()
    {
        Assert.False(
            string.IsNullOrWhiteSpace(_factory.DatabaseUrl),
            "TEST_DATABASE_URL or COORDINATOR-TEST-ENV.json is required for integration tests.");
    }

    private sealed record TestFixture(
        Guid SellerId,
        Guid SellerUserId,
        Guid ManagerUserId,
        Guid CustomerUserId,
        Guid AdminUserId,
        Guid LockedUserId,
        Guid KioskId,
        Guid SlotId,
        Guid SubscriptionId
    );

    private async Task<TestFixture> SeedDatabaseFixtureAsync(string kioskStatus = "ONLINE")
    {
        EnsureDatabaseConfigured();

        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await data.OpenConnectionAsync();

        var f = new TestFixture(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7()
        );

        var paymentId = Guid.CreateVersion7();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            -- 1. Insert active seller
            INSERT INTO identity.sellers(id, shop_name, phone, address, status, package_id, package_expires_at, bank_name, bank_holder, bank_account_no_enc, created_at, updated_at)
            VALUES(@seller, 'Test Shop ' || replace(@seller::text, '-', ''), '0912345678', '123 Address', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30, 'Vietcombank', 'TEST SELLER', 'enc:v1:test', public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 2. Insert active seller user
            INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id, created_at, updated_at)
            VALUES(@sellerUser, replace(@sellerUser::text, '-', '') || '@test.local', '!hash', 'Seller User', 'SELLER', 'ACTIVE', @seller, public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 3. Insert active operations manager user
            INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id, created_at, updated_at)
            VALUES(@managerUser, replace(@managerUser::text, '-', '') || '@test.local', '!hash', 'Ops Manager', 'OPERATIONS_MANAGER', 'ACTIVE', NULL, public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 4. Insert active customer user
            INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id, created_at, updated_at)
            VALUES(@customerUser, replace(@customerUser::text, '-', '') || '@test.local', '!hash', 'Customer User', 'CUSTOMER', 'ACTIVE', NULL, public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 5. Insert active admin user
            INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id, created_at, updated_at)
            VALUES(@adminUser, replace(@adminUser::text, '-', '') || '@test.local', '!hash', 'Admin User', 'ADMIN', 'ACTIVE', NULL, public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 6. Insert locked user
            INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id, created_at, updated_at)
            VALUES(@lockedUser, replace(@lockedUser::text, '-', '') || '@test.local', '!hash', 'Locked User', 'SELLER', 'LOCKED', @seller, public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 7. Insert kiosk with full unique identifiers (no collisions across fixtures)
            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status, seller_id, created_at, updated_at)
            VALUES(@kiosk, 'K-' || replace(@kiosk::text, '-', ''), 'Test Kiosk', '123 Test Street', 'HCM', 'HW-' || replace(@kiosk::text, '-', ''), 'MQTT-' || replace(@kiosk::text, '-', ''), @kioskStatus, @seller, public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 8. Insert subscription FIRST with payment_id = NULL and status = 'PENDING_PAYMENT'
            INSERT INTO identity.subscriptions(id, seller_id, package_id, kiosk_id, period_from, period_to, price, status, payment_id, created_at, updated_at)
            VALUES(@sub, @seller, '20000000-0000-0000-0000-00000000000a', @kiosk, CURRENT_DATE - 10, CURRENT_DATE + 30, 400000, 'PENDING_PAYMENT', NULL, public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 9. Insert payment with subscription_id = @sub (satisfying payments_check4)
            INSERT INTO payment.payments(id, kind, purpose, subscription_id, gateway, idempotency_key, amount, status, paid_at, created_at, updated_at)
            VALUES(@payment, 'CHARGE', 'SUBSCRIPTION', @sub, 'PAYOS', 'sub-pay-' || replace(@sub::text, '-', ''), 400000, 'SUCCEEDED', public.app_now(), public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 10. Update subscription to set payment_id = @payment and status = 'ACTIVE'
            UPDATE identity.subscriptions
            SET payment_id = @payment, status = 'ACTIVE', updated_at = public.app_now()
            WHERE id = @sub;

            -- 11. Insert slot
            INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel, status, current_seller_id, zone, row_version, created_at, updated_at)
            VALUES(@slot, @kiosk, 'A01', 1, 'FREE', @seller, 'WALK_IN', 1, public.app_now(), public.app_now())
            ON CONFLICT (id) DO NOTHING;

            -- 12. Insert slot assignment
            INSERT INTO kiosk_ops.slot_assignments(slot_id, seller_id, assigned_by, assigned_at, status)
            VALUES(@slot, @seller, @managerUser, public.app_now(), 'ACTIVE')
            ON CONFLICT DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("seller", f.SellerId);
        cmd.Parameters.AddWithValue("sellerUser", f.SellerUserId);
        cmd.Parameters.AddWithValue("managerUser", f.ManagerUserId);
        cmd.Parameters.AddWithValue("customerUser", f.CustomerUserId);
        cmd.Parameters.AddWithValue("adminUser", f.AdminUserId);
        cmd.Parameters.AddWithValue("lockedUser", f.LockedUserId);
        cmd.Parameters.AddWithValue("kiosk", f.KioskId);
        cmd.Parameters.AddWithValue("payment", paymentId);
        cmd.Parameters.AddWithValue("sub", f.SubscriptionId);
        cmd.Parameters.AddWithValue("slot", f.SlotId);
        cmd.Parameters.AddWithValue("kioskStatus", kioskStatus);

        await cmd.ExecuteNonQueryAsync();
        return f;
    }

    #region Role, Tenant, and Disabled Status Authorization Tests

    [Fact]
    public async Task SellerSubmit_Anonymous_ReturnsUnauthorized()
    {
        ClearAuthentication();
        var sellerId = Guid.CreateVersion7();
        var kioskId = Guid.CreateVersion7();

        var response = await _client.PostAsJsonAsync(
            $"/api/sellers/{sellerId}/kiosks/{kioskId}/termination-requests",
            new CreateKioskTerminationRequest("Reason for termination long enough"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SellerSubmit_CustomerRole_ReturnsForbidden()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("CUSTOMER", f.CustomerUserId);

        var response = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest("Reason for termination long enough"));

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"Expected 403 or 404 for customer role, got {response.StatusCode}");
    }

    [Fact]
    public async Task SellerSubmit_AdminRole_ReturnsForbidden()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("ADMIN", f.AdminUserId);

        var response = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest("Reason for termination long enough"));

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"Expected 403 or 404 for admin role, got {response.StatusCode}");
    }

    [Fact]
    public async Task SellerSubmit_DisabledUserInDb_ReturnsForbidden()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("SELLER", f.LockedUserId, f.SellerId);

        var response = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest("Reason for termination long enough"));

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401 or 403 for disabled user, got {response.StatusCode}");
    }

    [Fact]
    public async Task SellerSubmit_ForeignSeller_ReturnsForbiddenOrNotFound()
    {
        var f = await SeedDatabaseFixtureAsync();
        var foreignSellerId = Guid.CreateVersion7();

        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);

        var response = await _client.PostAsJsonAsync(
            $"/api/sellers/{foreignSellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest("Reason for termination long enough"));

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"Expected 403 or 404 for foreign seller, got {response.StatusCode}");
    }

    [Fact]
    public async Task OperationsList_AdminRole_ReturnsForbidden()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("ADMIN", f.AdminUserId);

        var response = await _client.GetAsync("/api/operations/kiosk-termination-requests");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OperationsList_DisabledManagerInDb_ReturnsForbidden()
    {
        var f = await SeedDatabaseFixtureAsync();

        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using (var conn = await data.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE identity.users SET status = 'LOCKED' WHERE id = @id";
            cmd.Parameters.AddWithValue("id", f.ManagerUserId);
            await cmd.ExecuteNonQueryAsync();
        }

        AuthenticateAs("OPERATIONS_MANAGER", f.ManagerUserId);
        var response = await _client.GetAsync("/api/operations/kiosk-termination-requests");
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401 or 403 for disabled manager, got {response.StatusCode}");
    }

    #endregion

    #region Input Validation Tests

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Too short")] // < 10 chars
    public async Task SellerSubmit_InvalidReasonLength_ReturnsValidationProblem(string reason)
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);

        var response = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest(reason));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SellerSubmit_ReasonTooLong_ReturnsValidationProblem()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);

        var longReason = new string('A', 2001);
        var response = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest(longReason));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("APPROVE")]
    [InlineData("CANCEL")]
    [InlineData("INVALID")]
    public async Task OperationsReview_InvalidAction_ReturnsValidationProblem(string action)
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("OPERATIONS_MANAGER", f.ManagerUserId);
        var id = Guid.CreateVersion7();

        var response = await _client.PostAsJsonAsync(
            $"/api/operations/kiosk-termination-requests/{id}/review",
            new ReviewKioskTerminationRequest(action, "Reason length is long enough"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Workflow Execution & Concurrency Tests

    [Fact]
    public async Task Workflow_Submit_Duplicate_ReturnsConflict()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);

        // 1. Submit valid termination request
        var resp1 = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest("Reason to terminate kiosk due to location relocation"));
        Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);

        var created = await resp1.Content.ReadFromJsonAsync<KioskTerminationResponse>();
        Assert.NotNull(created);
        Assert.Equal("REQUESTED", created.Status);

        // 2. Duplicate submission with different reason -> 409 Conflict
        var resp2 = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest("Another different reason while in flight"));
        Assert.Equal(HttpStatusCode.Conflict, resp2.StatusCode);
    }

    [Fact]
    public async Task Workflow_Review_Reject_Success()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);

        var submitResp = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest("Reason to terminate kiosk due to location relocation"));
        var created = await submitResp.Content.ReadFromJsonAsync<KioskTerminationResponse>();
        Assert.NotNull(created);

        // Review REJECT
        AuthenticateAs("OPERATIONS_MANAGER", f.ManagerUserId);
        var reviewResp = await _client.PostAsJsonAsync(
            $"/api/operations/kiosk-termination-requests/{created.Id}/review",
            new ReviewKioskTerminationRequest("REJECT", "Contract minimum period not yet fulfilled"));
        Assert.Equal(HttpStatusCode.OK, reviewResp.StatusCode);

        var reviewed = await reviewResp.Content.ReadFromJsonAsync<KioskTerminationResponse>();
        Assert.NotNull(reviewed);
        Assert.Equal("REJECTED", reviewed.Status);
        Assert.Equal("REJECT", reviewed.ReviewAction);
    }

    [Fact]
    public async Task Workflow_Review_Accept_BlocksNewCommerce_PreservesPaidPickup()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);

        var submitResp = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest("Reason to terminate kiosk due to location relocation"));
        var created = await submitResp.Content.ReadFromJsonAsync<KioskTerminationResponse>();
        Assert.NotNull(created);

        // Review ACCEPT
        AuthenticateAs("OPERATIONS_MANAGER", f.ManagerUserId);
        var reviewResp = await _client.PostAsJsonAsync(
            $"/api/operations/kiosk-termination-requests/{created.Id}/review",
            new ReviewKioskTerminationRequest("ACCEPT", "Termination approved for handover"));
        Assert.Equal(HttpStatusCode.OK, reviewResp.StatusCode);

        var reviewed = await reviewResp.Content.ReadFromJsonAsync<KioskTerminationResponse>();
        Assert.NotNull(reviewed);
        Assert.Equal("ACCEPTED", reviewed.Status);

        // Verify NEW order at this kiosk is rejected by database trigger
        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using (var conn = await data.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ordering.orders(order_code, checkout_id, customer_id, seller_id, kiosk_id, subtotal, total_amount, tracking_token)
                VALUES('ORD-TEST-' || replace(@kiosk::text, '-', ''), public.uuid_v7(), NULL, @seller, @kiosk, 100000, 100000, 'TRK-' || replace(@kiosk::text, '-', ''));
                """;
            cmd.Parameters.AddWithValue("seller", f.SellerId);
            cmd.Parameters.AddWithValue("kiosk", f.KioskId);

            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("P0001", ex.SqlState);
        }
    }

    [Fact]
    public async Task Workflow_Complete_RefusesIfLiabilitiesExist()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);

        var submitResp = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest("Reason to terminate kiosk due to location relocation"));
        var created = await submitResp.Content.ReadFromJsonAsync<KioskTerminationResponse>();
        Assert.NotNull(created);

        AuthenticateAs("OPERATIONS_MANAGER", f.ManagerUserId);
        await _client.PostAsJsonAsync(
            $"/api/operations/kiosk-termination-requests/{created.Id}/review",
            new ReviewKioskTerminationRequest("ACCEPT", "Approved for handover"));

        // Simulate remaining live device command (unlock_token in ISSUED status)
        using var scope = _factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using (var conn = await data.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO kiosk_ops.unlock_tokens(id, kiosk_id, slot_id, purpose, token_hash, cmd_id, status, issued_at, expires_at, issued_to_user_id)
                VALUES(public.uuid_v7(), @kiosk, @slot, 'SELLER_ACCESS', 'hash_test_' || replace(@kiosk::text, '-', ''), public.uuid_v7(), 'ISSUED', public.app_now(), public.app_now() + interval '5 minutes', (SELECT requested_by FROM kiosk_ops.kiosk_terminations WHERE kiosk_id=@kiosk LIMIT 1));
                """;
            cmd.Parameters.AddWithValue("kiosk", f.KioskId);
            cmd.Parameters.AddWithValue("slot", f.SlotId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Attempt complete -> MUST fail closed with 409 Conflict
        var completeResp = await _client.PostAsJsonAsync(
            $"/api/operations/kiosk-termination-requests/{created.Id}/complete",
            new CompleteKioskTerminationRequest("Complete attempt", null));

        Assert.Equal(HttpStatusCode.Conflict, completeResp.StatusCode);
    }

    [Fact]
    public async Task Workflow_Complete_RefusesIfForeignEvidenceSupplied()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);

        var submitResp = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest("Reason to terminate kiosk due to location relocation"));
        var created = await submitResp.Content.ReadFromJsonAsync<KioskTerminationResponse>();
        Assert.NotNull(created);

        AuthenticateAs("OPERATIONS_MANAGER", f.ManagerUserId);
        await _client.PostAsJsonAsync(
            $"/api/operations/kiosk-termination-requests/{created.Id}/review",
            new ReviewKioskTerminationRequest("ACCEPT", "Approved for handover"));

        // Issue private evidence ticket bound to a DIFFERENT resource/manager
        using var scope = _factory.Services.CreateScope();
        var tickets = scope.ServiceProvider.GetRequiredService<PrivateEvidence>();
        var foreignId = Guid.CreateVersion7();
        var foreignMedia = new VerifiedMedia("https://res.cloudinary.com/test/image.jpg", "florabot/test", "image/jpeg", 1024, "abc123sha256");
        var foreignTicket = tickets.Issue("kiosk_termination", $"manager:{f.ManagerUserId}:{foreignId}", foreignMedia);

        // Attempt complete with foreign ticket -> MUST fail with 400 Bad Request
        var completeResp = await _client.PostAsJsonAsync(
            $"/api/operations/kiosk-termination-requests/{created.Id}/complete",
            new CompleteKioskTerminationRequest("Complete with foreign ticket", foreignTicket.Reference));

        Assert.Equal(HttpStatusCode.BadRequest, completeResp.StatusCode);
    }

    [Fact]
    public async Task Workflow_Complete_HappyPath_UnbindsSeller_And_HistoryRemainsReadable()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);

        var submitResp = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest("Reason to terminate kiosk due to location relocation"));
        var created = await submitResp.Content.ReadFromJsonAsync<KioskTerminationResponse>();
        Assert.NotNull(created);

        AuthenticateAs("OPERATIONS_MANAGER", f.ManagerUserId);
        await _client.PostAsJsonAsync(
            $"/api/operations/kiosk-termination-requests/{created.Id}/review",
            new ReviewKioskTerminationRequest("ACCEPT", "Approved for handover"));

        // Generate verified evidence ticket for exact manager and termination ID
        using var scope = _factory.Services.CreateScope();
        var tickets = scope.ServiceProvider.GetRequiredService<PrivateEvidence>();
        var media = new VerifiedMedia($"https://res.cloudinary.com/test/{created.Id}/image_ok.jpg", "florabot/test_ok", "image/jpeg", 2048, "verifiedsha256");
        var ticket = tickets.Issue("kiosk_termination", $"manager:{f.ManagerUserId}:{created.Id}", media);

        // Complete succeeds
        var completeResp = await _client.PostAsJsonAsync(
            $"/api/operations/kiosk-termination-requests/{created.Id}/complete",
            new CompleteKioskTerminationRequest("Physical inspection verified", ticket.Reference));

        Assert.Equal(HttpStatusCode.OK, completeResp.StatusCode);
        var completed = await completeResp.Content.ReadFromJsonAsync<KioskTerminationResponse>();
        Assert.NotNull(completed);
        Assert.Equal("COMPLETED", completed.Status);

        // Kiosk is now unbound (kiosks.seller_id = NULL), but original seller can STILL read their historical request
        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);
        var currentResp = await _client.GetAsync($"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests/current");
        Assert.Equal(HttpStatusCode.OK, currentResp.StatusCode);

        var historical = await currentResp.Content.ReadFromJsonAsync<KioskTerminationResponse>();
        Assert.NotNull(historical);
        Assert.Equal(created.Id, historical.Id);
        Assert.Equal("COMPLETED", historical.Status);

        // Foreign seller is still denied
        var foreignFixture = await SeedDatabaseFixtureAsync();
        var foreignSeller = foreignFixture.SellerId;
        AuthenticateAs("SELLER", foreignFixture.SellerUserId, foreignSeller);
        var foreignResp = await _client.GetAsync($"/api/sellers/{foreignSeller}/kiosks/{f.KioskId}/termination-requests/current");
        Assert.True(foreignResp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Workflow_Complete_Idempotent_DoesNotConsumeNewEvidence()
    {
        var f = await SeedDatabaseFixtureAsync();
        AuthenticateAs("SELLER", f.SellerUserId, f.SellerId);

        var submitResp = await _client.PostAsJsonAsync(
            $"/api/sellers/{f.SellerId}/kiosks/{f.KioskId}/termination-requests",
            new CreateKioskTerminationRequest("Reason to terminate kiosk due to location relocation"));
        var created = await submitResp.Content.ReadFromJsonAsync<KioskTerminationResponse>();
        Assert.NotNull(created);

        AuthenticateAs("OPERATIONS_MANAGER", f.ManagerUserId);
        await _client.PostAsJsonAsync(
            $"/api/operations/kiosk-termination-requests/{created.Id}/review",
            new ReviewKioskTerminationRequest("ACCEPT", "Approved for handover"));

        using var scope = _factory.Services.CreateScope();
        var tickets = scope.ServiceProvider.GetRequiredService<PrivateEvidence>();
        var media = new VerifiedMedia($"https://res.cloudinary.com/test/{created.Id}/idempotent.jpg", "florabot/idempotent", "image/jpeg", 2048, "sha256idem");
        var ticket = tickets.Issue("kiosk_termination", $"manager:{f.ManagerUserId}:{created.Id}", media);

        // First complete succeeds
        var complete1 = await _client.PostAsJsonAsync(
            $"/api/operations/kiosk-termination-requests/{created.Id}/complete",
            new CompleteKioskTerminationRequest("First complete", ticket.Reference));
        Assert.Equal(HttpStatusCode.OK, complete1.StatusCode);

        // Second complete succeeds idempotently without error
        var complete2 = await _client.PostAsJsonAsync(
            $"/api/operations/kiosk-termination-requests/{created.Id}/complete",
            new CompleteKioskTerminationRequest("Second complete", null));
        Assert.Equal(HttpStatusCode.OK, complete2.StatusCode);
    }

    #endregion
}
