using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.Ordering;
using FloraBot.Api.Modules.Payment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FloraBot.SellerRefunds.Tests;

public sealed class SellerRefundsStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapSellerRefunds();
            });
            next(app);
        };
    }
}

public sealed class SellerRefundsTestFactory : WebApplicationFactory<Program>
{
    public string DatabaseUrl { get; }
    public string ValkeyUrl { get; }

    public SellerRefundsTestFactory()
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
        builder.ConfigureLogging(logging => logging.ClearProviders());

        builder.UseSetting("DATABASE_URL", DatabaseUrl);
        builder.UseSetting("VALKEY_URL", ValkeyUrl);

        builder.UseSetting("JWT_SIGNING_KEY", "test-only-key-at-least-thirty-two-bytes-long-not-production");
        builder.UseSetting("JOBS_ENABLED", "false");
        builder.UseSetting("MQTT_ENABLED", "false");

        builder.ConfigureServices(services =>
        {
        });
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
public sealed class SellerRefundsIntegrationTests : IClassFixture<SellerRefundsTestFactory>
{
    private readonly SellerRefundsTestFactory _factory;

    public SellerRefundsIntegrationTests(SellerRefundsTestFactory factory)
    {
        _factory = factory;
    }

    private static string GetDatabaseUrlOrDie() => SellerRefundsTestFactory.RequireDatabaseUrl();

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

    private static async Task SeedUserAsync(NpgsqlConnection conn, Guid userId, string fullName, string email, string phone, string role, string status, Guid? sellerId = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO identity.users (id, full_name, email, phone, role, status, seller_id, loyalty_points, password_hash)
            VALUES (@id, @name, @email, @phone, @role, @status, @sellerId, 0, 'test-only-unused-password-hash');
            """;
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("name", fullName);
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("phone", phone);
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("sellerId", (object?)sellerId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string EncryptAccount(IDataProtectionProvider protection, string account) =>
        protection.CreateProtector("bank-account-v1").Protect(account);

    [Fact]
    public async Task ListAndGetRefunds_OwnSellerAllowed_ForeignRoleRejected()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var parentChargeId = Guid.NewGuid();
        var suffixA = Guid.NewGuid().ToString("N")[..8];
        var suffixB = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerA, $"Shop A {suffixA}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedSellerAsync(conn, sellerB, $"Shop B {suffixB}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, ownerA, "Owner A", $"owner-{suffixA}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "SELLER", "ACTIVE", sellerA);
        await SeedUserAsync(conn, ownerB, "Owner B", $"owner-{suffixB}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "SELLER", "ACTIVE", sellerB);
        await SeedUserAsync(conn, customerId, "Customer", $"cust-{suffixA}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        var checkoutId = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, created_at, updated_at, completed_at
                ) VALUES (
                    @orderId, @orderCode, @checkoutId, @customerId, @sellerA, '40000000-0000-0000-0000-000000000001'::uuid,
                    'COMPLETED', 200000, 0, 200000, 0, 0,
                    @trackingToken, public.app_now(), public.app_now(), public.app_now()
                );

                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, gateway, idempotency_key,
                    amount, status, paid_at, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @parentChargeId, 'CHARGE', 'ORDER_CHECKOUT', @checkoutId, @orderId, 'PAYOS', @chargeIdem,
                    200000, 'SUCCEEDED', public.app_now(), 'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );

                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key,
                    amount, status, approved_by, reason, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @refundId, 'REFUND', 'ORDER_CHECKOUT', @checkoutId, @orderId, @parentChargeId, 'MANUAL', @refundIdem,
                    200000, 'PENDING', '10000000-0000-0000-0000-000000000001'::uuid, 'Customer requested refund',
                    'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );
                """;
            cmd.Parameters.AddWithValue("orderId", orderId);
            cmd.Parameters.AddWithValue("orderCode", $"FBO-{suffixA}");
            cmd.Parameters.AddWithValue("checkoutId", checkoutId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("sellerA", sellerA);
            cmd.Parameters.AddWithValue("trackingToken", $"trk-{suffixA}");
            cmd.Parameters.AddWithValue("parentChargeId", parentChargeId);
            cmd.Parameters.AddWithValue("chargeIdem", $"chg-{suffixA}");
            cmd.Parameters.AddWithValue("refundId", refundId);
            cmd.Parameters.AddWithValue("refundIdem", $"ref-{suffixA}");
            await cmd.ExecuteNonQueryAsync();
        }

        using var client = _factory.CreateClient();

        // 1. Owner A can list refunds: sees refundId
        var tokenA = _factory.CreateToken(ownerA, "SELLER", sellerA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        var listResp = await client.GetAsync($"/api/sellers/{sellerA}/refunds");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var page = await listResp.Content.ReadFromJsonAsync<SellerRefundPageResponse>();
        Assert.NotNull(page);
        Assert.Contains(page.Items, item => item.Id == refundId);

        // 2. Owner A can get detail of refundId
        var getResp = await client.GetAsync($"/api/sellers/{sellerA}/refunds/{refundId}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var detail = await getResp.Content.ReadFromJsonAsync<SellerRefundDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal(refundId, detail.Id);
        Assert.Equal(200000, detail.Amount);

        // 3. Foreign seller B is rejected with 403 / 404
        var tokenB = _factory.CreateToken(ownerB, "SELLER", sellerB);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var foreignListResp = await client.GetAsync($"/api/sellers/{sellerA}/refunds");
        Assert.True(foreignListResp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);

        var foreignGetResp = await client.GetAsync($"/api/sellers/{sellerA}/refunds/{refundId}");
        Assert.True(foreignGetResp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BankDetails_OnlyApprovedRefund_AuditRecorded()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var approvedRefundId = Guid.NewGuid();
        var unapprovedRefundId = Guid.NewGuid();
        var parentChargeId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"Bank Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, ownerId, "Bank Owner", $"bankowner-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "SELLER", "ACTIVE", sellerId);
        await SeedUserAsync(conn, customerId, "Bank Customer", $"bankcust-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        var protection = _factory.Services.GetRequiredService<IDataProtectionProvider>();
        var encryptedAccount = EncryptAccount(protection, "9876543210");

        var checkoutId = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, refund_bank_name, refund_bank_holder, refund_bank_account_enc,
                    created_at, updated_at, completed_at
                ) VALUES (
                    @orderId, @orderCode, @checkoutId, @customerId, @sellerId, '40000000-0000-0000-0000-000000000001'::uuid,
                    'COMPLETED', 150000, 0, 150000, 0, 0,
                    @trackingToken, 'Techcombank', 'TRAN VAN B', @encAccount,
                    public.app_now(), public.app_now(), public.app_now()
                );

                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, gateway, idempotency_key,
                    amount, status, paid_at, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @parentChargeId, 'CHARGE', 'ORDER_CHECKOUT', @checkoutId, @orderId, 'PAYOS', @chargeIdem,
                    150000, 'SUCCEEDED', public.app_now(), 'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );

                -- Approved refund (approved_by = Admin seed 10000000-0000-0000-0000-000000000001)
                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key,
                    amount, status, approved_by, reason, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @approvedRefundId, 'REFUND', 'ORDER_CHECKOUT', @checkoutId, @orderId, @parentChargeId, 'MANUAL', @apprIdem,
                    75000, 'PENDING', '10000000-0000-0000-0000-000000000001'::uuid, 'Approved return',
                    'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );

                -- Unapproved refund (approved_by = NULL or non-admin)
                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key,
                    amount, status, approved_by, reason, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @unapprovedRefundId, 'REFUND', 'ORDER_CHECKOUT', @checkoutId, @orderId, @parentChargeId, 'MANUAL', @unapprIdem,
                    75000, 'PENDING', '10000000-0000-0000-0000-0000000000ff'::uuid, 'Unapproved system refund',
                    'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );
                """;
            cmd.Parameters.AddWithValue("orderId", orderId);
            cmd.Parameters.AddWithValue("orderCode", $"FB-BK-{suffix}");
            cmd.Parameters.AddWithValue("checkoutId", checkoutId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("trackingToken", $"trk-bk-{suffix}");
            cmd.Parameters.AddWithValue("encAccount", encryptedAccount);
            cmd.Parameters.AddWithValue("parentChargeId", parentChargeId);
            cmd.Parameters.AddWithValue("chargeIdem", $"chg-bk-{suffix}");
            cmd.Parameters.AddWithValue("approvedRefundId", approvedRefundId);
            cmd.Parameters.AddWithValue("apprIdem", $"appr-{suffix}");
            cmd.Parameters.AddWithValue("unapprovedRefundId", unapprovedRefundId);
            cmd.Parameters.AddWithValue("unapprIdem", $"unappr-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        using var client = _factory.CreateClient();
        var token = _factory.CreateToken(ownerId, "SELLER", sellerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Unapproved refund bank-details returns 409 Conflict
        var unapprResp = await client.PostAsJsonAsync(
            $"/api/sellers/{sellerId}/refunds/{unapprovedRefundId}/bank-details",
            new SellerBankAccessRequest("Check customer account to refund"));
        Assert.Equal(HttpStatusCode.Conflict, unapprResp.StatusCode);

        // 2. Approved refund bank-details with short reason (<5 chars) returns 400 Bad Request
        var shortReasonResp = await client.PostAsJsonAsync(
            $"/api/sellers/{sellerId}/refunds/{approvedRefundId}/bank-details",
            new SellerBankAccessRequest("no"));
        Assert.Equal(HttpStatusCode.BadRequest, shortReasonResp.StatusCode);

        // 3. Approved refund bank-details with valid reason succeeds and returns decrypted bank
        var validResp = await client.PostAsJsonAsync(
            $"/api/sellers/{sellerId}/refunds/{approvedRefundId}/bank-details",
            new SellerBankAccessRequest("Chuyển khoản hoàn tiền cho khách"));
        Assert.Equal(HttpStatusCode.OK, validResp.StatusCode);
        var bankData = await validResp.Content.ReadFromJsonAsync<RefundBankResponse>();
        Assert.NotNull(bankData);
        Assert.Equal("Techcombank", bankData.BankName);
        Assert.Equal("TRAN VAN B", bankData.Holder);
        Assert.Equal("9876543210", bankData.Account);

        // 4. Verify atomic audit logged in notify.audit_logs
        await using (var auditCmd = conn.CreateCommand())
        {
            auditCmd.CommandText = """
                SELECT count(*) FROM notify.audit_logs
                WHERE action = 'SELLER_REFUND_BANK_VIEWED'
                  AND actor_id = @ownerId
                  AND entity_id = @refundId;
                """;
            auditCmd.Parameters.AddWithValue("ownerId", ownerId);
            auditCmd.Parameters.AddWithValue("refundId", approvedRefundId);
            var auditCount = (long)(await auditCmd.ExecuteScalarAsync() ?? 0L);
            Assert.True(auditCount >= 1, "Audit record for viewing refund bank details must exist.");
        }
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("not-pending", HttpStatusCode.Conflict)]
    [InlineData("not-approved", HttpStatusCode.Conflict)]
    [InlineData("no-bank", HttpStatusCode.Conflict)]
    public async Task SubmitTransfer_Preconditions_ReturnDomainErrorsWithoutReaderFailure(string mode, HttpStatusCode expected)
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var parentChargeId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"Submit Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, ownerId, "Submit Owner", $"subowner-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "SELLER", "ACTIVE", sellerId);
        await SeedUserAsync(conn, customerId, "Submit Customer", $"subcust-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        var protection = _factory.Services.GetRequiredService<IDataProtectionProvider>();
        var encryptedAccount = EncryptAccount(protection, "1122334455");

        var checkoutId = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, refund_bank_name, refund_bank_holder, refund_bank_account_enc,
                    created_at, updated_at, completed_at
                ) VALUES (
                    @orderId, @orderCode, @checkoutId, @customerId, @sellerId, '40000000-0000-0000-0000-000000000001'::uuid,
                    'COMPLETED', 100000, 0, 100000, 0, 0,
                    @trackingToken, 'ACB', 'LE THI C', @encAccount,
                    public.app_now(), public.app_now(), public.app_now()
                );

                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, gateway, idempotency_key,
                    amount, status, paid_at, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @parentChargeId, 'CHARGE', 'ORDER_CHECKOUT', @checkoutId, @orderId, 'PAYOS', @chargeIdem,
                    100000, 'SUCCEEDED', public.app_now(), 'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );

                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key,
                    amount, status, approved_by, reason, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @refundId, 'REFUND', 'ORDER_CHECKOUT', @checkoutId, @orderId, @parentChargeId, 'MANUAL', @refIdem,
                    100000, 'PENDING', '10000000-0000-0000-0000-000000000001'::uuid, 'Direct refund test',
                    'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );
                """;
            cmd.Parameters.AddWithValue("orderId", orderId);
            cmd.Parameters.AddWithValue("orderCode", $"FB-SUB-{suffix}");
            cmd.Parameters.AddWithValue("checkoutId", checkoutId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("trackingToken", $"trk-sub-{suffix}");
            cmd.Parameters.AddWithValue("encAccount", encryptedAccount);
            cmd.Parameters.AddWithValue("parentChargeId", parentChargeId);
            cmd.Parameters.AddWithValue("chargeIdem", $"chg-sub-{suffix}");
            cmd.Parameters.AddWithValue("refundId", refundId);
            cmd.Parameters.AddWithValue("refIdem", $"ref-sub-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        var targetRefundId = mode == "missing" ? Guid.NewGuid() : refundId;
        await using (var condition = conn.CreateCommand())
        {
            condition.CommandText = mode switch
            {
                "not-pending" => "UPDATE payment.payments SET status='FAILED' WHERE id=@id",
                "not-approved" => "UPDATE payment.payments SET approved_by=NULL WHERE id=@id",
                "no-bank" => "UPDATE ordering.orders SET refund_bank_name=NULL, refund_bank_holder=NULL, refund_bank_account_enc=NULL WHERE id=@id",
                _ => "SELECT 1 WHERE @id IS NOT NULL"
            };
            condition.Parameters.AddWithValue("id", mode == "no-bank" ? orderId : refundId);
            await condition.ExecuteNonQueryAsync();
        }

        // Issue a correctly bound test ticket so validation reaches the guarded database lookup.
        var privateEvidence = _factory.Services.GetRequiredService<PrivateEvidence>();
        var binding = $"seller_refund:{ownerId}:{sellerId}:{targetRefundId}";
        var verifiedMedia = new VerifiedMedia($"https://res.cloudinary.com/demo/image/upload/proof-{suffix}.jpg", $"proof-{suffix}", "image/jpeg", 1024, "abcd1234abcd1234");
        var ticketResp = privateEvidence.Issue("seller_direct_refund", binding, verifiedMedia);

        using var client = _factory.CreateClient();
        var token = _factory.CreateToken(ownerId, "SELLER", sellerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.PostAsJsonAsync(
            $"/api/sellers/{sellerId}/refunds/{targetRefundId}/submit-transfer",
            new SellerSubmitTransferRequest(100000, "VND", "ACB", "LE THI C", "1122334455",
                $"TXN-{suffix}", DateTimeOffset.UtcNow.AddMinutes(-5), ticketResp.Reference));
        Assert.Equal(expected, response.StatusCode);
        await using var verify = conn.CreateCommand();
        verify.CommandText = "SELECT count(*) FROM payment.seller_refund_transfers WHERE refund_id=@id";
        verify.Parameters.AddWithValue("id", refundId);
        Assert.Equal(0L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SubmitTransfer_WrongAmount_OrWrongBank_Rejected()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var parentChargeId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"Submit Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, ownerId, "Submit Owner", $"subowner-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "SELLER", "ACTIVE", sellerId);
        await SeedUserAsync(conn, customerId, "Submit Customer", $"subcust-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        var protection = _factory.Services.GetRequiredService<IDataProtectionProvider>();
        var encryptedAccount = EncryptAccount(protection, "1122334455");

        var checkoutId = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, refund_bank_name, refund_bank_holder, refund_bank_account_enc,
                    created_at, updated_at, completed_at
                ) VALUES (
                    @orderId, @orderCode, @checkoutId, @customerId, @sellerId, '40000000-0000-0000-0000-000000000001'::uuid,
                    'COMPLETED', 100000, 0, 100000, 0, 0,
                    @trackingToken, 'ACB', 'LE THI C', @encAccount,
                    public.app_now(), public.app_now(), public.app_now()
                );

                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, gateway, idempotency_key,
                    amount, status, paid_at, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @parentChargeId, 'CHARGE', 'ORDER_CHECKOUT', @checkoutId, @orderId, 'PAYOS', @chargeIdem,
                    100000, 'SUCCEEDED', public.app_now(), 'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );

                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key,
                    amount, status, approved_by, reason, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @refundId, 'REFUND', 'ORDER_CHECKOUT', @checkoutId, @orderId, @parentChargeId, 'MANUAL', @refIdem,
                    100000, 'PENDING', '10000000-0000-0000-0000-000000000001'::uuid, 'Direct refund test',
                    'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );
                """;
            cmd.Parameters.AddWithValue("orderId", orderId);
            cmd.Parameters.AddWithValue("orderCode", $"FB-SUB-{suffix}");
            cmd.Parameters.AddWithValue("checkoutId", checkoutId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("trackingToken", $"trk-sub-{suffix}");
            cmd.Parameters.AddWithValue("encAccount", encryptedAccount);
            cmd.Parameters.AddWithValue("parentChargeId", parentChargeId);
            cmd.Parameters.AddWithValue("chargeIdem", $"chg-sub-{suffix}");
            cmd.Parameters.AddWithValue("refundId", refundId);
            cmd.Parameters.AddWithValue("refIdem", $"ref-sub-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        // Issue a valid mock evidence ticket
        var privateEvidence = _factory.Services.GetRequiredService<PrivateEvidence>();
        var binding = $"seller_refund:{ownerId}:{sellerId}:{refundId}";
        var verifiedMedia = new VerifiedMedia($"https://res.cloudinary.com/demo/image/upload/proof-{suffix}.jpg", $"proof-{suffix}", "image/jpeg", 1024, "abcd1234abcd1234");
        var ticketResp = privateEvidence.Issue("seller_direct_refund", binding, verifiedMedia);

        using var client = _factory.CreateClient();
        var token = _factory.CreateToken(ownerId, "SELLER", sellerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Wrong amount (expected 100,000, submitted 90,000)
        var wrongAmountResp = await client.PostAsJsonAsync(
            $"/api/sellers/{sellerId}/refunds/{refundId}/submit-transfer",
            new SellerSubmitTransferRequest(
                Amount: 90000,
                Currency: "VND",
                BankName: "ACB",
                BankHolder: "LE THI C",
                AccountNumber: "1122334455",
                ProviderReference: $"TXN-{suffix}",
                TransferredAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                EvidenceTicket: ticketResp.Reference));
        Assert.Equal(HttpStatusCode.BadRequest, wrongAmountResp.StatusCode);

        // 2. Wrong bank receiver (submitted account doesn't match customer's)
        var wrongBankResp = await client.PostAsJsonAsync(
            $"/api/sellers/{sellerId}/refunds/{refundId}/submit-transfer",
            new SellerSubmitTransferRequest(
                Amount: 100000,
                Currency: "VND",
                BankName: "ACB",
                BankHolder: "LE THI C",
                AccountNumber: "9999999999", // mismatch
                ProviderReference: $"TXN-{suffix}",
                TransferredAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                EvidenceTicket: ticketResp.Reference));
        Assert.Equal(HttpStatusCode.BadRequest, wrongBankResp.StatusCode);

        // 3. Non-VND currency rejected
        var wrongCurrencyResp = await client.PostAsJsonAsync(
            $"/api/sellers/{sellerId}/refunds/{refundId}/submit-transfer",
            new SellerSubmitTransferRequest(
                Amount: 100000,
                Currency: "USD",
                BankName: "ACB",
                BankHolder: "LE THI C",
                AccountNumber: "1122334455",
                ProviderReference: $"TXN-{suffix}",
                TransferredAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                EvidenceTicket: ticketResp.Reference));
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrencyResp.StatusCode);
    }

    [Fact]
    public async Task SubmitTransfer_Valid_RecordsPendingReview_IdempotentOnRetry()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var parentChargeId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"Idem Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, ownerId, "Idem Owner", $"idemowner-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "SELLER", "ACTIVE", sellerId);
        await SeedUserAsync(conn, customerId, "Idem Customer", $"idemcust-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        var protection = _factory.Services.GetRequiredService<IDataProtectionProvider>();
        var encryptedAccount = EncryptAccount(protection, "5566778899");

        var checkoutId = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, refund_bank_name, refund_bank_holder, refund_bank_account_enc,
                    created_at, updated_at, completed_at
                ) VALUES (
                    @orderId, @orderCode, @checkoutId, @customerId, @sellerId, '40000000-0000-0000-0000-000000000001'::uuid,
                    'COMPLETED', 250000, 0, 250000, 0, 0,
                    @trackingToken, 'MBBank', 'HOANG VAN D', @encAccount,
                    public.app_now(), public.app_now(), public.app_now()
                );

                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, gateway, idempotency_key,
                    amount, status, paid_at, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @parentChargeId, 'CHARGE', 'ORDER_CHECKOUT', @checkoutId, @orderId, 'PAYOS', @chargeIdem,
                    250000, 'SUCCEEDED', public.app_now(), 'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );

                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key,
                    amount, status, approved_by, reason, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @refundId, 'REFUND', 'ORDER_CHECKOUT', @checkoutId, @orderId, @parentChargeId, 'MANUAL', @refIdem,
                    250000, 'PENDING', '10000000-0000-0000-0000-000000000001'::uuid, 'Customer refund',
                    'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );
                """;
            cmd.Parameters.AddWithValue("orderId", orderId);
            cmd.Parameters.AddWithValue("orderCode", $"FB-IDEM-{suffix}");
            cmd.Parameters.AddWithValue("checkoutId", checkoutId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("trackingToken", $"trk-idem-{suffix}");
            cmd.Parameters.AddWithValue("encAccount", encryptedAccount);
            cmd.Parameters.AddWithValue("parentChargeId", parentChargeId);
            cmd.Parameters.AddWithValue("chargeIdem", $"chg-idem-{suffix}");
            cmd.Parameters.AddWithValue("refundId", refundId);
            cmd.Parameters.AddWithValue("refIdem", $"ref-idem-{suffix}");
            await cmd.ExecuteNonQueryAsync();
        }

        var privateEvidence = _factory.Services.GetRequiredService<PrivateEvidence>();
        var binding = $"seller_refund:{ownerId}:{sellerId}:{refundId}";
        var verifiedMedia = new VerifiedMedia($"https://res.cloudinary.com/demo/image/upload/proof-idem-{suffix}.jpg", $"proof-idem-{suffix}", "image/jpeg", 2048, "hashidem12345678");
        var ticketResp = privateEvidence.Issue("seller_direct_refund", binding, verifiedMedia);

        using var client = _factory.CreateClient();
        var token = _factory.CreateToken(ownerId, "SELLER", sellerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = new SellerSubmitTransferRequest(
            Amount: 250000,
            Currency: "VND",
            BankName: "MBBank",
            BankHolder: "HOANG VAN D",
            AccountNumber: "5566778899",
            ProviderReference: $"MB-{suffix}",
            TransferredAt: DateTimeOffset.UtcNow.AddMinutes(-2),
            EvidenceTicket: ticketResp.Reference,
            Note: "Transferred via MB app");

        // 1. Initial submit succeeds and records PENDING_REVIEW
        var firstResp = await client.PostAsJsonAsync($"/api/sellers/{sellerId}/refunds/{refundId}/submit-transfer", request);
        Assert.Equal(HttpStatusCode.OK, firstResp.StatusCode);
        var sub1 = await firstResp.Content.ReadFromJsonAsync<SellerRefundTransferSubmissionResponse>();
        Assert.NotNull(sub1);
        Assert.Equal("PENDING_REVIEW", sub1.Status);
        Assert.Equal($"MB-{suffix}", sub1.ProviderReference);

        // 2. Retry with same reference is idempotent
        var secondResp = await client.PostAsJsonAsync($"/api/sellers/{sellerId}/refunds/{refundId}/submit-transfer", request);
        Assert.Equal(HttpStatusCode.OK, secondResp.StatusCode);
        var sub2 = await secondResp.Content.ReadFromJsonAsync<SellerRefundTransferSubmissionResponse>();
        Assert.NotNull(sub2);
        Assert.Equal(sub1.Id, sub2.Id);

        // 3. Retry with different reference causes 409 Conflict
        var conflictRequest = request with { ProviderReference = $"MB-DIFF-{suffix}" };
        var conflictResp = await client.PostAsJsonAsync($"/api/sellers/{sellerId}/refunds/{refundId}/submit-transfer", conflictRequest);
        Assert.Equal(HttpStatusCode.Conflict, conflictResp.StatusCode);
    }

    [Fact]
    public async Task AdminConfirm_DirectRefund_NoPlatformLedger_UpdatesOrder()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var parentChargeId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"Direct Confirm Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        await SeedUserAsync(conn, ownerId, "Direct Owner", $"dirowner-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "SELLER", "ACTIVE", sellerId);
        await SeedUserAsync(conn, customerId, "Direct Customer", $"dircust-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "CUSTOMER", "ACTIVE");

        var protection = _factory.Services.GetRequiredService<IDataProtectionProvider>();
        var encryptedAccount = EncryptAccount(protection, "9988776655");

        var checkoutId = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, refund_bank_name, refund_bank_holder, refund_bank_account_enc,
                    created_at, updated_at, completed_at
                ) VALUES (
                    @orderId, @orderCode, @checkoutId, @customerId, @sellerId, '40000000-0000-0000-0000-000000000001'::uuid,
                    'COMPLETED', 180000, 0, 180000, 0, 0,
                    @trackingToken, 'VietinBank', 'VU VAN E', @encAccount,
                    public.app_now(), public.app_now(), public.app_now()
                );

                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, gateway, idempotency_key,
                    amount, status, paid_at, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @parentChargeId, 'CHARGE', 'ORDER_CHECKOUT', @checkoutId, @orderId, 'PAYOS', @chargeIdem,
                    180000, 'SUCCEEDED', public.app_now(), 'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );

                -- Approved by Admin 1 (seed: 10000000-0000-0000-0000-000000000001)
                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key,
                    amount, status, approved_by, reason, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @refundId, 'REFUND', 'ORDER_CHECKOUT', @checkoutId, @orderId, @parentChargeId, 'MANUAL', @refIdem,
                    180000, 'PENDING', '10000000-0000-0000-0000-000000000001'::uuid, 'Direct refund complete',
                    'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );

                -- Seller submitted transfer PENDING_REVIEW
                INSERT INTO payment.seller_refund_transfers (
                    refund_id, seller_id, order_id, submitted_by, amount, currency,
                    receiver_bank_name, receiver_bank_holder, receiver_account_no_enc,
                    provider_reference, transferred_at, evidence_url, status
                ) VALUES (
                    @refundId, @sellerId, @orderId, @ownerId, 180000, 'VND',
                    'VietinBank', 'VU VAN E', @encAccount,
                    @providerRef, public.app_now() - interval '1 hour', @evidenceUrl, 'PENDING_REVIEW'
                );
                """;
            cmd.Parameters.AddWithValue("orderId", orderId);
            cmd.Parameters.AddWithValue("orderCode", $"FB-DIR-{suffix}");
            cmd.Parameters.AddWithValue("checkoutId", checkoutId);
            cmd.Parameters.AddWithValue("customerId", customerId);
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("trackingToken", $"trk-dir-{suffix}");
            cmd.Parameters.AddWithValue("encAccount", encryptedAccount);
            cmd.Parameters.AddWithValue("parentChargeId", parentChargeId);
            cmd.Parameters.AddWithValue("chargeIdem", $"chg-dir-{suffix}");
            cmd.Parameters.AddWithValue("refundId", refundId);
            cmd.Parameters.AddWithValue("refIdem", $"ref-dir-{suffix}");
            cmd.Parameters.AddWithValue("ownerId", ownerId);
            cmd.Parameters.AddWithValue("providerRef", $"VTB-{suffix}");
            cmd.Parameters.AddWithValue("evidenceUrl", $"https://res.cloudinary.com/proof-dir-{suffix}.jpg");
            await cmd.ExecuteNonQueryAsync();
        }

        // Count ledger entries before confirmation
        long ledgerBefore;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = "SELECT count(*) FROM payment.ledger_entries WHERE ref_id = @refundId;";
            countCmd.Parameters.AddWithValue("refundId", refundId);
            ledgerBefore = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);
        }

        // Admin 2 (seed: 10000000-0000-0000-0000-000000000002) confirms the refund
        await using (var confirmCmd = conn.CreateCommand())
        {
            confirmCmd.CommandText = """
                SELECT flow.confirm_refund(@refundId, '10000000-0000-0000-0000-000000000002'::uuid, @proofUrl);
                """;
            confirmCmd.Parameters.AddWithValue("refundId", refundId);
            confirmCmd.Parameters.AddWithValue("proofUrl", $"https://res.cloudinary.com/admin-proof-{suffix}.jpg");
            await confirmCmd.ExecuteNonQueryAsync();
        }

        // 1. Verify refund status is SUCCEEDED
        await using (var checkPay = conn.CreateCommand())
        {
            checkPay.CommandText = "SELECT status, paid_by FROM payment.payments WHERE id = @refundId;";
            checkPay.Parameters.AddWithValue("refundId", refundId);
            await using var reader = await checkPay.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("SUCCEEDED", reader.GetString(0));
            Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000002"), reader.GetGuid(1));
        }

        // 2. Verify order status is REFUNDED (100% refunded)
        await using (var checkOrder = conn.CreateCommand())
        {
            checkOrder.CommandText = "SELECT status, refund_bank_name FROM ordering.orders WHERE id = @orderId;";
            checkOrder.Parameters.AddWithValue("orderId", orderId);
            await using var reader = await checkOrder.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("REFUNDED", reader.GetString(0));
            Assert.True(reader.IsDBNull(1), "Refund bank details must be cleared on order completion.");
        }

        // 3. Verify zero platform ledger entries were written for direct refund
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = "SELECT count(*) FROM payment.ledger_entries WHERE ref_id = @refundId;";
            countCmd.Parameters.AddWithValue("refundId", refundId);
            var ledgerAfter = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(ledgerBefore, ledgerAfter); // Zero new ledger entries!
        }
    }

    [Fact]
    public async Task AdminConfirm_MakerChecker_SubmitterOrApproverCannotConfirm()
    {
        await using var conn = await OpenDbConnectionAsync();

        var sellerId = Guid.NewGuid();
        var ownerAdminId = Guid.NewGuid(); // Historical submitter now holds the Admin role
        var orderId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var parentChargeId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await SeedSellerAsync(conn, sellerId, $"MakerChecker Shop {suffix}", $"09{Random.Shared.Next(10000000, 99999999)}");
        // Historical submitter is now Admin; current role remains single-valued
        await SeedUserAsync(conn, ownerAdminId, "Dual Role User", $"dual-{suffix}@florabot.test", $"09{Random.Shared.Next(10000000, 99999999)}", "ADMIN", "ACTIVE");

        var checkoutId = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO ordering.orders (
                    id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
                    status, subtotal, discount_amount, total_amount, points_redeemed, points_earned,
                    tracking_token, refund_bank_name, refund_bank_holder, refund_bank_account_enc,
                    created_at, updated_at, completed_at
                ) VALUES (
                    @orderId, @orderCode, @checkoutId, NULL, @sellerId, '40000000-0000-0000-0000-000000000001'::uuid,
                    'COMPLETED', 100000, 0, 100000, 0, 0,
                    @trackingToken, 'VCB', 'TEST HOLDER', 'enc:fake',
                    public.app_now(), public.app_now(), public.app_now()
                );

                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, gateway, idempotency_key,
                    amount, status, paid_at, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @parentChargeId, 'CHARGE', 'ORDER_CHECKOUT', @checkoutId, @orderId, 'PAYOS', @chargeIdem,
                    100000, 'SUCCEEDED', public.app_now(), 'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );

                -- Approved by Admin 1 (seed: 10000000-0000-0000-0000-000000000001)
                INSERT INTO payment.payments (
                    id, kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key,
                    amount, status, approved_by, reason, funding_provenance, is_direct_receipt, created_at, updated_at
                ) VALUES (
                    @refundId, 'REFUND', 'ORDER_CHECKOUT', @checkoutId, @orderId, @parentChargeId, 'MANUAL', @refIdem,
                    100000, 'PENDING', '10000000-0000-0000-0000-000000000001'::uuid, 'Maker checker test',
                    'SELLER_DIRECT', true, public.app_now(), public.app_now()
                );

                INSERT INTO payment.seller_refund_transfers (
                    refund_id, seller_id, order_id, submitted_by, amount, currency,
                    receiver_bank_name, receiver_bank_holder, receiver_account_no_enc,
                    provider_reference, transferred_at, evidence_url, status
                ) VALUES (
                    @refundId, @sellerId, @orderId, @ownerAdminId, 100000, 'VND',
                    'VCB', 'TEST HOLDER', 'enc:fake',
                    @providerRef, public.app_now(), @evidenceUrl, 'PENDING_REVIEW'
                );
                """;
            cmd.Parameters.AddWithValue("orderId", orderId);
            cmd.Parameters.AddWithValue("orderCode", $"FB-MC-{suffix}");
            cmd.Parameters.AddWithValue("checkoutId", checkoutId);
            cmd.Parameters.AddWithValue("sellerId", sellerId);
            cmd.Parameters.AddWithValue("trackingToken", $"trk-mc-{suffix}");
            cmd.Parameters.AddWithValue("parentChargeId", parentChargeId);
            cmd.Parameters.AddWithValue("chargeIdem", $"chg-mc-{suffix}");
            cmd.Parameters.AddWithValue("refundId", refundId);
            cmd.Parameters.AddWithValue("refIdem", $"ref-mc-{suffix}");
            cmd.Parameters.AddWithValue("ownerAdminId", ownerAdminId);
            cmd.Parameters.AddWithValue("providerRef", $"MC-{suffix}");
            cmd.Parameters.AddWithValue("evidenceUrl", $"https://res.cloudinary.com/proof-mc-{suffix}.jpg");
            await cmd.ExecuteNonQueryAsync();
        }

        // Attempt 1: Approver (Admin 1) tries to confirm -> rejected
        await using (var failApproverCmd = conn.CreateCommand())
        {
            failApproverCmd.CommandText = "SELECT flow.confirm_refund(@refundId, '10000000-0000-0000-0000-000000000001'::uuid, 'proof');";
            failApproverCmd.Parameters.AddWithValue("refundId", refundId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => failApproverCmd.ExecuteNonQueryAsync());
            Assert.Contains("khác người duyệt", ex.MessageText);
        }

        // Attempt 2: Submitter (ownerAdminId) tries to confirm -> rejected
        await using (var failSubmitterCmd = conn.CreateCommand())
        {
            failSubmitterCmd.CommandText = "SELECT flow.confirm_refund(@refundId, @submitterId, 'proof');";
            failSubmitterCmd.Parameters.AddWithValue("refundId", refundId);
            failSubmitterCmd.Parameters.AddWithValue("submitterId", ownerAdminId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => failSubmitterCmd.ExecuteNonQueryAsync());
            Assert.Contains("khác người bán", ex.MessageText);
        }
    }
}
