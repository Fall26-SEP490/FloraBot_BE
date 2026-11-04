using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Payment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

[Trait("Category", "Integration")]
public sealed class PayOsChannelIntegrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public PayOsChannelIntegrationTests(ApiFactory factory)
    {
        this.factory = factory;
        var dbUrl = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(dbUrl))
        {
            throw new InvalidOperationException("TEST_DATABASE_URL is required for executing PayOsChannelIntegrationTests against isolated DB. Fails closed per specification.");
        }
    }

    private static readonly Guid Seller1Id = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Seller2Id = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid AdminUserId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Seller1OwnerId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid Seller2OwnerId = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly Guid CustomerUserId = Guid.Parse("10000000-0000-0000-0000-000000000008");

    private static readonly Dictionary<string, string?> TestConfig = new()
    {
        ["PAYOS_RETURN_URL"] = "https://example.invalid/return",
        ["PAYOS_CANCEL_URL"] = "https://example.invalid/cancel",
        ["PAYOS_PUBLIC_BASE_URL"] = "https://api.florabot.test",

        ["PAYOS_CHANNELS:seller-1-valid:SellerId"] = Seller1Id.ToString(),
        ["PAYOS_CHANNELS:seller-1-valid:ClientId"] = "client-1",
        ["PAYOS_CHANNELS:seller-1-valid:ApiKey"] = "api-1",
        ["PAYOS_CHANNELS:seller-1-valid:ChecksumKey"] = "checksum-1-32bytes-secret-key-ok!",
        ["PAYOS_CHANNELS:seller-1-valid:ExpectedBin"] = "970415",
        ["PAYOS_CHANNELS:seller-1-valid:ExpectedAccount"] = "1010099887766",
        ["PAYOS_CHANNELS:seller-1-valid:ExpectedName"] = "TIEM HOA SO 1",

        ["PAYOS_CHANNELS:seller-2-valid:SellerId"] = Seller2Id.ToString(),
        ["PAYOS_CHANNELS:seller-2-valid:ClientId"] = "client-2",
        ["PAYOS_CHANNELS:seller-2-valid:ApiKey"] = "api-2",
        ["PAYOS_CHANNELS:seller-2-valid:ChecksumKey"] = "checksum-2-32bytes-secret-key-ok!",
        ["PAYOS_CHANNELS:seller-2-valid:ExpectedBin"] = "970422",
        ["PAYOS_CHANNELS:seller-2-valid:ExpectedAccount"] = "999988887777",
        ["PAYOS_CHANNELS:seller-2-valid:ExpectedName"] = "TIEM HOA SO 2",

        ["PAYOS_CHANNELS:invalid-recipient:SellerId"] = Seller1Id.ToString(),
        ["PAYOS_CHANNELS:invalid-recipient:ClientId"] = "client-bad",
        ["PAYOS_CHANNELS:invalid-recipient:ApiKey"] = "api-bad",
        ["PAYOS_CHANNELS:invalid-recipient:ChecksumKey"] = "checksum-bad-32bytes-secret-key!",
        ["PAYOS_CHANNELS:invalid-recipient:ExpectedBin"] = "invalid_bin",
        ["PAYOS_CHANNELS:invalid-recipient:ExpectedAccount"] = "1010099887766"
    };

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateTestApp(HttpMessageHandler? payOsHandler = null)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            foreach (var kvp in TestConfig) builder.UseSetting(kvp.Key, kvp.Value);
            if (payOsHandler is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IPayOsClientResolver>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfiguration>();
                        var httpClient = new HttpClient(payOsHandler) { Timeout = TimeSpan.FromSeconds(10) };
                        return PayOsClientResolver.ForTest(httpClient, config);
                    });
                });
            }
        });
    }

    [Fact]
    public async Task Anonymous_RequestsAreRejectedWith401()
    {
        using var app = CreateTestApp();
        using var client = app.CreateClient();

        var getRes = await client.GetAsync($"/api/sellers/{Seller1Id}/payment-channel");
        Assert.Equal(HttpStatusCode.Unauthorized, getRes.StatusCode);

        var putRes = await client.PutAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel", new BindPaymentChannelRequest("seller-1-valid"));
        Assert.Equal(HttpStatusCode.Unauthorized, putRes.StatusCode);

        var postRes = await client.PostAsync($"/api/sellers/{Seller1Id}/payment-channel/verify", null);
        Assert.Equal(HttpStatusCode.Unauthorized, postRes.StatusCode);

        var patchRes = await client.PatchAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel/status", new UpdateChannelStatusRequest("PAUSED"));
        Assert.Equal(HttpStatusCode.Unauthorized, patchRes.StatusCode);

        var adminGetRes = await client.GetAsync("/api/admin/payment-channels");
        Assert.Equal(HttpStatusCode.Unauthorized, adminGetRes.StatusCode);
    }

    [Fact]
    public async Task ForeignRole_CustomerOrStaffCannotAccessChannelMetadata()
    {
        using var app = CreateTestApp();
        using var client = app.CreateClient();

        // Customer token
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "CUSTOMER", userId: CustomerUserId));
        var res = await client.GetAsync($"/api/sellers/{Seller1Id}/payment-channel");
        Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized);

        // Foreign seller token (Seller 2 trying to access Seller 1 channel)
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: Seller2OwnerId, sellerId: Seller2Id));
        var foreignRes = await client.GetAsync($"/api/sellers/{Seller1Id}/payment-channel");
        Assert.True(foreignRes.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DisabledActor_CannotAccessChannelMetadata()
    {
        using var app = CreateTestApp();
        using var client = app.CreateClient();
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var disabledSellerOwnerId = Guid.NewGuid();
        await using (var conn = await source.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO identity.users (id, email, full_name, role, status, seller_id, password_hash) VALUES (@id, @email, 'Disabled Seller', 'SELLER', 'DISABLED', @sellerId, 'dummy_hash')";
            cmd.Parameters.AddWithValue("id", disabledSellerOwnerId);
            cmd.Parameters.AddWithValue("email", $"disabled-{disabledSellerOwnerId:N}@example.invalid");
            cmd.Parameters.AddWithValue("sellerId", Seller1Id);
            await cmd.ExecuteNonQueryAsync();
        }

        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: disabledSellerOwnerId, sellerId: Seller1Id));
        var res = await client.GetAsync($"/api/sellers/{Seller1Id}/payment-channel");
        Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminBinding_ValidReference_CreatesImmutableVersion()
    {
        using var app = CreateTestApp();
        using var client = app.CreateClient();

        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "ADMIN", userId: AdminUserId));

        var putRes = await client.PutAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel", new BindPaymentChannelRequest("seller-1-valid"));
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var data = await putRes.Content.ReadFromJsonAsync<SellerPaymentChannelResponse>();
        Assert.NotNull(data);
        Assert.Equal(Seller1Id, data.SellerId);
        Assert.True(data.Version >= 1);
        Assert.Equal("PENDING_VERIFICATION", data.State);
        Assert.Equal("970415", data.ExpectedBankBin);
        Assert.Equal("*********7766", data.ExpectedBankAccountMasked);
        Assert.Null(data.VerifiedAt);

        // Binding again increments version and retains prior version
        var putRes2 = await client.PutAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel", new BindPaymentChannelRequest("seller-1-valid"));
        Assert.Equal(HttpStatusCode.OK, putRes2.StatusCode);
        var data2 = await putRes2.Content.ReadFromJsonAsync<SellerPaymentChannelResponse>();
        Assert.NotNull(data2);
        Assert.Equal(data.Version + 1, data2.Version);
    }

    [Fact]
    public async Task AdminBinding_ConfigSellerMismatch_RejectedWith403()
    {
        using var app = CreateTestApp();
        using var client = app.CreateClient();

        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "ADMIN", userId: AdminUserId));

        // Attempting to bind seller-2-valid to Seller1Id
        var putRes = await client.PutAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel", new BindPaymentChannelRequest("seller-2-valid"));
        Assert.Equal(HttpStatusCode.Forbidden, putRes.StatusCode);
    }

    [Fact]
    public async Task AdminBinding_MissingRecipientInConfig_RejectedWith400()
    {
        using var app = CreateTestApp();
        using var client = app.CreateClient();

        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "ADMIN", userId: AdminUserId));

        // invalid-recipient has invalid BIN format
        var putRes = await client.PutAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel", new BindPaymentChannelRequest("invalid-recipient"));
        Assert.Equal(HttpStatusCode.BadRequest, putRes.StatusCode);

        // Non-existent ref
        var notFoundRes = await client.PutAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel", new BindPaymentChannelRequest("unknown-ref"));
        Assert.Equal(HttpStatusCode.BadRequest, notFoundRes.StatusCode);
    }

    [Fact]
    public async Task SecretMask_NeverExposesSecretRefOrRawAccount()
    {
        using var app = CreateTestApp();
        using var client = app.CreateClient();

        // Bind as admin
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "ADMIN", userId: AdminUserId));
        await client.PutAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel", new BindPaymentChannelRequest("seller-1-valid"));

        // Seller GET
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: Seller1OwnerId, sellerId: Seller1Id));
        var getRes = await client.GetAsync($"/api/sellers/{Seller1Id}/payment-channel");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);

        var rawJson = await getRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain("seller-1-valid", rawJson);
        Assert.DoesNotContain("checksum-1", rawJson);
        Assert.DoesNotContain("api-1", rawJson);
        Assert.DoesNotContain("1010099887766", rawJson);
        Assert.Contains("*********7766", rawJson);

        // Admin GET list
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "ADMIN", userId: AdminUserId));
        var adminGetRes = await client.GetAsync("/api/admin/payment-channels");
        Assert.Equal(HttpStatusCode.OK, adminGetRes.StatusCode);

        var adminRawJson = await adminGetRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain("seller-1-valid", adminRawJson);
        Assert.DoesNotContain("checksum-1", adminRawJson);
        Assert.DoesNotContain("1010099887766", adminRawJson);
    }

    [Fact]
    public async Task VersionImmutability_TriggerRejectsUpdatesToIdentityAndRecipient()
    {
        using var app = CreateTestApp();
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var conn = await source.OpenConnectionAsync();

        // 1. Insert a channel version
        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO payment.payment_channel_versions (
                id, seller_id, version, secret_ref, expected_bank_bin, expected_bank_account, expected_bank_name, state
            ) VALUES (
                public.uuid_v7(), @sellerId, (SELECT COALESCE(MAX(version),0)+1 FROM payment.payment_channel_versions WHERE seller_id=@sellerId), 'test-immutability', '970415', '1010099887766', 'SHOP 1', 'PENDING_VERIFICATION'
            ) RETURNING id;
            """;
        insertCmd.Parameters.AddWithValue("sellerId", Seller1Id);
        var id = (Guid)(await insertCmd.ExecuteScalarAsync())!;

        // 2. Attempting to update secret_ref is rejected by trigger
        await using var updateSecretCmd = conn.CreateCommand();
        updateSecretCmd.CommandText = "UPDATE payment.payment_channel_versions SET secret_ref = 'tampered' WHERE id = @id";
        updateSecretCmd.Parameters.AddWithValue("id", id);
        var exSecret = await Assert.ThrowsAsync<PostgresException>(() => updateSecretCmd.ExecuteNonQueryAsync());
        Assert.Equal("P0001", exSecret.SqlState);

        // 3. Attempting to update expected_bank_account is rejected by trigger
        await using var updateAccountCmd = conn.CreateCommand();
        updateAccountCmd.CommandText = "UPDATE payment.payment_channel_versions SET expected_bank_account = '9999999999' WHERE id = @id";
        updateAccountCmd.Parameters.AddWithValue("id", id);
        var exAccount = await Assert.ThrowsAsync<PostgresException>(() => updateAccountCmd.ExecuteNonQueryAsync());
        Assert.Equal("P0001", exAccount.SqlState);

        // 4. Attempting to delete is rejected by trigger
        await using var deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM payment.payment_channel_versions WHERE id = @id";
        deleteCmd.Parameters.AddWithValue("id", id);
        var exDelete = await Assert.ThrowsAsync<PostgresException>(() => deleteCmd.ExecuteNonQueryAsync());
        Assert.Equal("P0001", exDelete.SqlState);
    }

    [Fact]
    public async Task ConcurrentBind_LocksSellerRowAndSerializesVersions()
    {
        using var app = CreateTestApp();
        using var client1 = app.CreateClient();
        using var client2 = app.CreateClient();

        client1.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "ADMIN", userId: AdminUserId));
        client2.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "ADMIN", userId: AdminUserId));

        // Concurrently bind two versions for Seller2Id
        var task1 = client1.PutAsJsonAsync($"/api/sellers/{Seller2Id}/payment-channel", new BindPaymentChannelRequest("seller-2-valid"));
        var task2 = client2.PutAsJsonAsync($"/api/sellers/{Seller2Id}/payment-channel", new BindPaymentChannelRequest("seller-2-valid"));

        await Task.WhenAll(task1, task2);

        var res1 = await task1;
        var res2 = await task2;

        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        var ver1 = (await res1.Content.ReadFromJsonAsync<SellerPaymentChannelResponse>())!.Version;
        var ver2 = (await res2.Content.ReadFromJsonAsync<SellerPaymentChannelResponse>())!.Version;

        Assert.NotEqual(ver1, ver2);
        Assert.True(Math.Abs(ver1 - ver2) == 1);
    }

    [Fact]
    public async Task PauseRollbackAudit_StatusChangeAuditedInSameTransaction()
    {
        using var app = CreateTestApp();
        using var client = app.CreateClient();

        // 1. Admin binds channel
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "ADMIN", userId: AdminUserId));
        await client.PutAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel", new BindPaymentChannelRequest("seller-1-valid"));

        // 2. Seller pauses channel
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: Seller1OwnerId, sellerId: Seller1Id));
        var patchRes = await client.PatchAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel/status", new UpdateChannelStatusRequest("PAUSED"));
        Assert.Equal(HttpStatusCode.OK, patchRes.StatusCode);
        var data = await patchRes.Content.ReadFromJsonAsync<SellerPaymentChannelResponse>();
        Assert.Equal("PAUSED", data!.State);

        // 3. Activating without verified_at fails with 409 Conflict
        var activateRes = await client.PatchAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel/status", new UpdateChannelStatusRequest("ACTIVE"));
        Assert.Equal(HttpStatusCode.Conflict, activateRes.StatusCode);
    }

    [Fact]
    public async Task Verify_WithConfirmWebhookAndSample_ActivatesChannel()
    {
        var fakePayOs = new FakePayOsTransport();
        using var app = CreateTestApp(fakePayOs);
        using var client = app.CreateClient();

        // 1. Admin binds channel
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "ADMIN", userId: AdminUserId));
        var bindRes = await client.PutAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel", new BindPaymentChannelRequest("seller-1-valid"));
        var bound = await bindRes.Content.ReadFromJsonAsync<SellerPaymentChannelResponse>();
        Assert.NotNull(bound);

        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        Guid versionId;
        await using (var conn = await source.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM payment.payment_channel_versions WHERE seller_id=@sid ORDER BY version DESC LIMIT 1";
            cmd.Parameters.AddWithValue("sid", Seller1Id);
            versionId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        // Configure fake PayOS confirm-webhook response
        fakePayOs.SetConfirmWebhookResponse("1010099887766", "TIEM HOA SO 1");

        // 2. Seller calls verify endpoint (this invokes confirm-webhook and registers attempt URL)
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: Seller1OwnerId, sellerId: Seller1Id));
        var verifyRes1 = await client.PostAsync($"/api/sellers/{Seller1Id}/payment-channel/verify", null);
        Assert.Equal(HttpStatusCode.OK, verifyRes1.StatusCode);
        var verifyData1 = await verifyRes1.Content.ReadFromJsonAsync<ChannelVerificationResponse>();
        Assert.False(verifyData1!.Verified); // Pending sample probe

        // 3. Fake PayOS calls version-bound webhook URL captured by transport
        Assert.NotNull(fakePayOs.CapturedWebhookUrl);
        var sampleData = JsonSerializer.SerializeToElement(new
        {
            orderCode = 123,
            amount = 3000,
            description = "VQRIO123"
        });
        var sampleSignature = PayOsChecksum.Sign(sampleData, "checksum-1-32bytes-secret-key-ok!");

        var sampleWebhookRes = await client.PostAsJsonAsync(fakePayOs.CapturedWebhookUrl, new PayOsWebhook(
            sampleData, sampleSignature, "00", "success", true));
        Assert.Equal(HttpStatusCode.OK, sampleWebhookRes.StatusCode);

        // 4. Channel is now ACTIVE with verified_at set
        var getRes = await client.GetAsync($"/api/sellers/{Seller1Id}/payment-channel");
        var activeChannel = await getRes.Content.ReadFromJsonAsync<SellerPaymentChannelResponse>();
        Assert.Equal("ACTIVE", activeChannel!.State);
        Assert.NotNull(activeChannel.VerifiedAt);
    }

    [Fact]
    public async Task DirectPayment_FullHttpLifecycle_PinsChannel_ProcessesWebhook_BypassesPlatformLedger()
    {
        var fakePayOs = new FakePayOsTransport();
        using var app = CreateTestApp(fakePayOs);
        using var client = app.CreateClient();

        // 1. Admin binds channel for Seller 1
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "ADMIN", userId: AdminUserId));
        await client.PutAsJsonAsync($"/api/sellers/{Seller1Id}/payment-channel", new BindPaymentChannelRequest("seller-1-valid"));

        // 2. Activate channel via verify + probe
        fakePayOs.SetConfirmWebhookResponse("1010099887766", "TIEM HOA SO 1");
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: Seller1OwnerId, sellerId: Seller1Id));
        await client.PostAsync($"/api/sellers/{Seller1Id}/payment-channel/verify", null);

        var sampleData = JsonSerializer.SerializeToElement(new { orderCode = 123, amount = 3000, description = "VQRIO123" });
        var sampleSig = PayOsChecksum.Sign(sampleData, "checksum-1-32bytes-secret-key-ok!");
        await client.PostAsJsonAsync(fakePayOs.CapturedWebhookUrl!, new PayOsWebhook(sampleData, sampleSig, "00", "success", true));

        // 3. Create checkout at kiosk for Seller 1
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var kiosk = Guid.NewGuid();
        var slot = Guid.NewGuid();
        var product = Guid.NewGuid();
        var key = "kiosk-test-" + kiosk;
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

        await using (var conn = await source.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO catalog.flower_products(id, seller_id, name, price, shelf_life_hours, status)
                VALUES (@product, @seller, 'Direct Rose', 200000, 48, 'ACTIVE');
                UPDATE identity.sellers SET status = 'ACTIVE', package_expires_at = CURRENT_DATE + 30 WHERE id = @seller;
                INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status, api_key_hash)
                VALUES (@kiosk, @kiosk::text, 'Direct Kiosk', '123 Test St', 'HCM', @kiosk::text, @kiosk::text, 'ONLINE', @hash);
                INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel, zone) VALUES (@slot, @kiosk, 'B01', 0, 'WALK_IN');
                SELECT flow.assign_kiosk(@kiosk, @seller, '10000000-0000-0000-0000-000000000001');
                """;
            cmd.Parameters.AddWithValue("product", product);
            cmd.Parameters.AddWithValue("seller", Seller1Id);
            cmd.Parameters.AddWithValue("kiosk", kiosk);
            cmd.Parameters.AddWithValue("hash", hash);
            cmd.Parameters.AddWithValue("slot", slot);
            await cmd.ExecuteNonQueryAsync();

            await using var stockCmd = conn.CreateCommand();
            stockCmd.CommandText = "SELECT flow.stock_bouquet(flow.new_batch(), @product, @slot, @sellerOwner, @qr)";
            stockCmd.Parameters.AddWithValue("product", product);
            stockCmd.Parameters.AddWithValue("slot", slot);
            stockCmd.Parameters.AddWithValue("sellerOwner", Seller1OwnerId);
            stockCmd.Parameters.AddWithValue("qr", "direct-qr-" + product);
            var bouquet = (Guid)(await stockCmd.ExecuteScalarAsync())!;

            await using var checkoutCmd = conn.CreateCommand();
            checkoutCmd.CommandText = "SELECT flow.kiosk_checkout(@kiosk, ARRAY[@bouquet]::uuid[])";
            checkoutCmd.Parameters.AddWithValue("kiosk", kiosk);
            checkoutCmd.Parameters.AddWithValue("bouquet", bouquet);
            var checkoutId = (Guid)(await checkoutCmd.ExecuteScalarAsync())!;

            // 4. Request payment link from kiosk
            using var kioskClient = app.CreateClient();
            kioskClient.DefaultRequestHeaders.Add("X-Kiosk-Key", key);

            var linkRes = await kioskClient.PostAsync($"/api/kiosks/{kiosk}/checkouts/{checkoutId}/payment-link", null);
            Assert.Equal(HttpStatusCode.OK, linkRes.StatusCode);
            var link = await linkRes.Content.ReadFromJsonAsync<PaymentLinkResponse>();
            Assert.NotNull(link);

            // 5. Verify payment row in DB has SELLER_DIRECT provenance, is_direct_receipt = true, and receiver snapshot
            Guid paymentId;
            await using (var chkCmd = conn.CreateCommand())
            {
                chkCmd.CommandText = """
                    SELECT id, funding_provenance::text, is_direct_receipt, channel_id, channel_version, receiver_snapshot
                    FROM payment.payments WHERE checkout_id = @checkoutId;
                    """;
                chkCmd.Parameters.AddWithValue("checkoutId", checkoutId);
                await using var reader = await chkCmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                paymentId = reader.GetGuid(0);
                Assert.Equal("SELLER_DIRECT", reader.GetString(1));
                Assert.True(reader.GetBoolean(2));
                Assert.False(reader.IsDBNull(3)); // channel_id pinned
                Assert.True(reader.GetInt32(4) >= 1); // version >= 1
                Assert.False(reader.IsDBNull(5)); // receiver snapshot
            }

            // 6. Post payment webhook to registered version URL with channel signature and matching receiver account
            var txnRef = "TXN-DIR-" + Guid.NewGuid().ToString("N");
            var paymentData = JsonSerializer.SerializeToElement(new
            {
                orderCode = link.OrderCode,
                amount = 200000,
                reference = txnRef,
                code = "00",
                accountNumber = "1010099887766"
            });
            var paymentSig = PayOsChecksum.Sign(paymentData, "checksum-1-32bytes-secret-key-ok!");

            var webhookRes = await client.PostAsJsonAsync(fakePayOs.CapturedWebhookUrl!, new PayOsWebhook(
                paymentData, paymentSig, "00", "success", true));
            Assert.Equal(HttpStatusCode.OK, webhookRes.StatusCode);

            // 7. Verify payment is SUCCEEDED and order is PAID
            await using (var chkPaid = conn.CreateCommand())
            {
                chkPaid.CommandText = "SELECT status FROM payment.payments WHERE id = @pid";
                chkPaid.Parameters.AddWithValue("pid", paymentId);
                Assert.Equal("SUCCEEDED", await chkPaid.ExecuteScalarAsync());

                chkPaid.CommandText = "SELECT id, status FROM ordering.orders WHERE checkout_id = @cid";
                chkPaid.Parameters.Clear();
                chkPaid.Parameters.AddWithValue("cid", checkoutId);
                Guid orderId;
                await using (var r = await chkPaid.ExecuteReaderAsync())
                {
                    Assert.True(await r.ReadAsync());
                    orderId = r.GetGuid(0);
                    Assert.Contains(r.GetString(1), new[] { "PAID", "DISPENSING", "COMPLETED" });
                }

                // 8. CRITICAL ACCOUNTING INVARIANT: Direct receipt writes ZERO platform ledger entries!
                chkPaid.CommandText = "SELECT COUNT(*) FROM payment.ledger_entries WHERE ref_id = @oid";
                chkPaid.Parameters.Clear();
                chkPaid.Parameters.AddWithValue("oid", orderId);
                var ledgerCount = (long)(await chkPaid.ExecuteScalarAsync())!;
                Assert.Equal(0, ledgerCount);
            }

            // 9. Seller queries payment detail
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: Seller1OwnerId, sellerId: Seller1Id));
            var detailRes = await client.GetAsync($"/api/sellers/{Seller1Id}/payments/{paymentId}");
            Assert.Equal(HttpStatusCode.OK, detailRes.StatusCode);
            var detail = await detailRes.Content.ReadFromJsonAsync<SellerPaymentDetailResponse>();
            Assert.NotNull(detail);
            Assert.Equal(200000, detail.Amount);
            Assert.Equal("SUCCEEDED", detail.Status);
            Assert.True(detail.Reconciled);

            // 10. Seller queries sales report
            var reportRes = await client.GetAsync($"/api/sellers/{Seller1Id}/reports/sales");
            Assert.Equal(HttpStatusCode.OK, reportRes.StatusCode);
            var report = await reportRes.Content.ReadFromJsonAsync<SellerSalesReportResponse>();
            Assert.NotNull(report);
            Assert.True(report.GrossPaid >= 200000);
            Assert.True(report.NetReceipts >= 200000);
        }
    }

    [Fact]
    public async Task DirectPayment_MissingSellerChannel_Returns503()
    {
        using var app = CreateTestApp();
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // Create seller with NO payment channel
        var newSeller = Guid.NewGuid();
        var newSellerUser = Guid.NewGuid();
        var kiosk = Guid.NewGuid();
        var slot = Guid.NewGuid();
        var product = Guid.NewGuid();
        var key = "kiosk-test-" + kiosk;
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

        await using var conn = await source.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
            VALUES (@seller, 'No Channel Shop', '0909998888', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30);
            INSERT INTO identity.users(id, email, password_hash, full_name, role, seller_id)
            VALUES (@user, @user::text || '@example.invalid', 'dummy_hash', 'No Channel Owner', 'SELLER', @seller);
            INSERT INTO catalog.flower_products(id, seller_id, name, price, shelf_life_hours, status)
            VALUES (@product, @seller, 'No Channel Rose', 150000, 48, 'ACTIVE');
            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status, api_key_hash)
            VALUES (@kiosk, @kiosk::text, 'No Channel Kiosk', '456 Test Ave', 'HCM', @kiosk::text, @kiosk::text, 'ONLINE', @hash);
            INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel, zone) VALUES (@slot, @kiosk, 'C01', 0, 'WALK_IN');
            SELECT flow.assign_kiosk(@kiosk, @seller, '10000000-0000-0000-0000-000000000001');
            """;
        cmd.Parameters.AddWithValue("seller", newSeller);
        cmd.Parameters.AddWithValue("user", newSellerUser);
        cmd.Parameters.AddWithValue("product", product);
        cmd.Parameters.AddWithValue("kiosk", kiosk);
        cmd.Parameters.AddWithValue("slot", slot);
        cmd.Parameters.AddWithValue("hash", hash);
        await cmd.ExecuteNonQueryAsync();

        await using var stockCmd = conn.CreateCommand();
        stockCmd.CommandText = "SELECT flow.stock_bouquet(flow.new_batch(), @product, @slot, @user, @qr)";
        stockCmd.Parameters.AddWithValue("product", product);
        stockCmd.Parameters.AddWithValue("slot", slot);
        stockCmd.Parameters.AddWithValue("user", newSellerUser);
        stockCmd.Parameters.AddWithValue("qr", "no-ch-qr-" + product);
        var bouquet = (Guid)(await stockCmd.ExecuteScalarAsync())!;

        await using var checkoutCmd = conn.CreateCommand();
        checkoutCmd.CommandText = "SELECT flow.kiosk_checkout(@kiosk, ARRAY[@bouquet]::uuid[])";
        checkoutCmd.Parameters.AddWithValue("kiosk", kiosk);
        checkoutCmd.Parameters.AddWithValue("bouquet", bouquet);
        var checkoutId = (Guid)(await checkoutCmd.ExecuteScalarAsync())!;

        using var kioskClient = app.CreateClient();
        kioskClient.DefaultRequestHeaders.Add("X-Kiosk-Key", key);

        var linkRes = await kioskClient.PostAsync($"/api/kiosks/{kiosk}/checkouts/{checkoutId}/payment-link", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, linkRes.StatusCode);
    }

    private sealed class FakePayOsTransport : HttpMessageHandler
    {
        private string? expectedAccount;
        private string? expectedName;
        public string? CapturedWebhookUrl { get; private set; }

        public void SetConfirmWebhookResponse(string account, string name)
        {
            expectedAccount = account;
            expectedName = name;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("confirm-webhook"))
            {
                var bodyJson = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                CapturedWebhookUrl = bodyJson.GetProperty("webhookUrl").GetString();

                var body = new
                {
                    code = "00",
                    data = new
                    {
                        webhookUrl = CapturedWebhookUrl,
                        accountNumber = expectedAccount ?? "1010099887766",
                        accountName = expectedName ?? "TIEM HOA SO 1",
                        name = "VietinBank",
                        shortName = "ICB"
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("payment-requests"))
            {
                var bodyJson = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                var code = bodyJson.GetProperty("orderCode").GetInt64();
                var amt = bodyJson.GetProperty("amount").GetInt64();

                var data = JsonSerializer.SerializeToElement(new
                {
                    orderCode = code,
                    amount = amt,
                    status = "PENDING",
                    checkoutUrl = "https://pay.payos.vn/web/124c33293c934a85be5b7f8761a27a07",
                    qrCode = PayOsClientQrTests.QrPayload,
                    bin = "970415",
                    accountNumber = expectedAccount ?? "1010099887766",
                    accountName = expectedName ?? "TIEM HOA SO 1"
                });

                var signature = PayOsChecksum.Sign(data, "checksum-1-32bytes-secret-key-ok!");
                var envelope = new { code = "00", data, signature };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(envelope) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { code = "00", data = new { status = "PAID", amount = 100000 } })
            };
        }
    }
}
