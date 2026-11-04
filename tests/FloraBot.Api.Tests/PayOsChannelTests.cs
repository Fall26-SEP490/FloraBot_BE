using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FloraBot.Api.Modules.Payment;
using Microsoft.Extensions.Configuration;

namespace FloraBot.Api.Tests;

public sealed class PayOsChannelTests
{
    private static readonly Guid TargetSellerId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid ForeignSellerId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private const string ChecksumKey = "test-channel-checksum-key-32bytes!";

    private static IConfiguration CreateChannelConfig() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        // Platform keys
        ["PAYOS_CLIENT_ID"] = "platform-client-id",
        ["PAYOS_API_KEY"] = "platform-api-key",
        ["PAYOS_CHECKSUM_KEY"] = "platform-checksum-key",
        ["PAYOS_RETURN_URL"] = "https://example.invalid/platform/return",
        ["PAYOS_CANCEL_URL"] = "https://example.invalid/platform/cancel",

        // Target seller channel
        ["PAYOS_CHANNELS:seller-shop-1:SellerId"] = TargetSellerId.ToString(),
        ["PAYOS_CHANNELS:seller-shop-1:ClientId"] = "seller1-client-id",
        ["PAYOS_CHANNELS:seller-shop-1:ApiKey"] = "seller1-api-key",
        ["PAYOS_CHANNELS:seller-shop-1:ChecksumKey"] = ChecksumKey,
        ["PAYOS_CHANNELS:seller-shop-1:ReturnUrl"] = "https://example.invalid/seller1/return",
        ["PAYOS_CHANNELS:seller-shop-1:CancelUrl"] = "https://example.invalid/seller1/cancel",
        ["PAYOS_CHANNELS:seller-shop-1:ExpectedBin"] = "970415",
        ["PAYOS_CHANNELS:seller-shop-1:ExpectedAccount"] = "1010099887766",
        ["PAYOS_CHANNELS:seller-shop-1:ExpectedName"] = "TIEM HOA SO 1",

        // Foreign seller channel (belongs to ForeignSellerId)
        ["PAYOS_CHANNELS:foreign-ref:SellerId"] = ForeignSellerId.ToString(),
        ["PAYOS_CHANNELS:foreign-ref:ClientId"] = "foreign-client-id",
        ["PAYOS_CHANNELS:foreign-ref:ApiKey"] = "foreign-api-key",
        ["PAYOS_CHANNELS:foreign-ref:ChecksumKey"] = "foreign-checksum-key",
        ["PAYOS_CHANNELS:foreign-ref:ReturnUrl"] = "https://example.invalid/foreign/return",
        ["PAYOS_CHANNELS:foreign-ref:CancelUrl"] = "https://example.invalid/foreign/cancel",
        ["PAYOS_CHANNELS:foreign-ref:ExpectedBin"] = "970422",
        ["PAYOS_CHANNELS:foreign-ref:ExpectedAccount"] = "999988887777",
        ["PAYOS_CHANNELS:foreign-ref:ExpectedName"] = "FOREIGN SHOP"
    }).Build();

    [Fact]
    public void ConfigResolution_ValidReference_ReturnsExpectedOptions()
    {
        var config = CreateChannelConfig();
        using var http = new HttpClient();
        var resolver = PayOsClientResolver.ForTest(http, config);

        var found = resolver.TryResolveChannelConfig("seller-shop-1", out var channelConfig);
        Assert.True(found);
        Assert.NotNull(channelConfig);
        Assert.Equal(TargetSellerId, channelConfig.SellerId);
        Assert.Equal("seller-shop-1", channelConfig.SecretRef);
        Assert.Equal("seller1-client-id", channelConfig.Options.ClientId);
        Assert.Equal("seller1-api-key", channelConfig.Options.ApiKey);
        Assert.Equal(ChecksumKey, channelConfig.Options.ChecksumKey);
        Assert.Equal("970415", channelConfig.Options.ExpectedBin);
        Assert.Equal("1010099887766", channelConfig.Options.ExpectedAccount);
        Assert.Equal("TIEM HOA SO 1", channelConfig.Options.ExpectedName);
    }

    [Fact]
    public void ConfigResolution_MissingReference_ReturnsFalseAndThrowsOnResolve()
    {
        var config = CreateChannelConfig();
        using var http = new HttpClient();
        var resolver = PayOsClientResolver.ForTest(http, config);

        var found = resolver.TryResolveChannelConfig("non-existent-secret-ref", out var channelConfig);
        Assert.False(found);
        Assert.Null(channelConfig);

        var ex = Assert.Throws<KeyNotFoundException>(() => resolver.ResolveChannelConfig("non-existent-secret-ref"));
        Assert.DoesNotContain("non-existent-secret-ref", ex.Message); // Does not echo secret_ref in error
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("seller/channel")]
    [InlineData("seller:channel")]
    [InlineData("seller.channel")]
    [InlineData("seller@shop")]
    public void ConfigResolution_InvalidSecretRefFormat_Rejected(string invalidRef)
    {
        var config = CreateChannelConfig();
        using var http = new HttpClient();
        var resolver = PayOsClientResolver.ForTest(http, config);

        var found = resolver.TryResolveChannelConfig(invalidRef, out var channelConfig);
        Assert.False(found);
        Assert.Null(channelConfig);
    }

    [Fact]
    public void ConfigResolution_ForeignSellerId_DetectedAsMismatch()
    {
        var config = CreateChannelConfig();
        using var http = new HttpClient();
        var resolver = PayOsClientResolver.ForTest(http, config);

        var found = resolver.TryResolveChannelConfig("foreign-ref", out var channelConfig);
        Assert.True(found);
        Assert.NotNull(channelConfig);
        Assert.NotEqual(TargetSellerId, channelConfig.SellerId);
        Assert.Equal(ForeignSellerId, channelConfig.SellerId);
    }

    [Fact]
    public void NoCredentialLeakage_InPublicDtos_AndToString()
    {
        // 1. Inspect SellerPaymentChannelResponse properties
        var sellerProps = typeof(SellerPaymentChannelResponse).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var forbiddenWords = new[] { "secret", "key", "token", "password", "raw" };

        foreach (var prop in sellerProps)
        {
            foreach (var word in forbiddenWords)
            {
                Assert.DoesNotContain(word, prop.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        // 2. Inspect AdminPaymentChannelItemResponse properties
        var adminProps = typeof(AdminPaymentChannelItemResponse).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in adminProps)
        {
            foreach (var word in forbiddenWords)
            {
                Assert.DoesNotContain(word, prop.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        // 3. Verify JSON serialization never includes secret_ref or credentials
        var response = new SellerPaymentChannelResponse(
            TargetSellerId,
            1,
            "ACTIVE",
            "970415",
            SellerPaymentChannels.MaskAccountNumber("1010099887766"),
            "TIEM HOA SO 1",
            DateTime.UtcNow,
            DateTime.UtcNow,
            DateTime.UtcNow
        );

        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("secretRef", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret_ref", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checksumKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1010099887766", json);
        Assert.Contains("*********7766", json);

        // 4. Verify PayOsChannelOptions.ToString() never outputs raw secrets
        var options = new PayOsChannelOptions("cid", "secret-api-key", "secret-checksum-key", "https://return", "https://cancel", "970415", "1010099887766");
        var str = options.ToString();
        Assert.DoesNotContain("secret-api-key", str);
        Assert.DoesNotContain("secret-checksum-key", str);
        Assert.DoesNotContain("1010099887766", str);

        // 5. Verify PayOsChannelConfig.ToString() never outputs raw secrets
        var cfg = new PayOsChannelConfig(TargetSellerId, "ref-1", options);
        var cfgStr = cfg.ToString();
        Assert.DoesNotContain("secret-api-key", cfgStr);
        Assert.DoesNotContain("secret-checksum-key", cfgStr);
    }

    [Theory]
    [InlineData("1010099887766", "*********7766")]
    [InlineData("99998888", "****8888")]
    [InlineData("1234", "****")]
    [InlineData("12", "**")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void MaskAccountNumber_MasksCorrectly(string? input, string expected)
    {
        Assert.Equal(expected, SellerPaymentChannels.MaskAccountNumber(input));
    }

    [Fact]
    public async Task PayOsClient_ChannelOptions_UsesChannelCredentials()
    {
        var options = new PayOsChannelOptions(
            "test-seller-client",
            "test-seller-api",
            ChecksumKey,
            "https://example.invalid/return",
            "https://example.invalid/cancel",
            "970415",
            "1010099887766",
            "TIEM HOA"
        );

        string? capturedClientId = null;
        string? capturedApiKey = null;

        using var http = new HttpClient(new Transport(req =>
        {
            capturedClientId = req.Headers.GetValues("x-client-id").FirstOrDefault();
            capturedApiKey = req.Headers.GetValues("x-api-key").FirstOrDefault();

            var data = JsonSerializer.SerializeToElement(new
            {
                orderCode = 555,
                amount = 250000,
                status = "PENDING",
                checkoutUrl = "https://pay.payos.vn/web/124c33293c934a85be5b7f8761a27a07",
                bin = "970415",
                accountNumber = "1010099887766"
            });
            return new
            {
                code = "00",
                data,
                signature = PayOsChecksum.Sign(data, ChecksumKey)
            };
        }));

        var client = PayOsClient.ForChannel(http, options);
        var res = await client.CreateAsync(555, 250000, CancellationToken.None);

        Assert.Equal("test-seller-client", capturedClientId);
        Assert.Equal("test-seller-api", capturedApiKey);
        Assert.Equal(555, res.OrderCode);
        Assert.Equal(250000, res.Amount);
    }

    [Fact]
    public async Task PayOsClient_CreateAsync_EnforcesMatchingBinAndAccount()
    {
        var options = new PayOsChannelOptions(
            "seller-client",
            "seller-api",
            ChecksumKey,
            "https://example.invalid/return",
            "https://example.invalid/cancel",
            "970415",
            "1010099887766"
        );

        // 1. Success when bin and account match
        using var httpSuccess = new HttpClient(new Transport(_ =>
        {
            var data = JsonSerializer.SerializeToElement(new
            {
                orderCode = 111,
                amount = 100000,
                status = "PENDING",
                checkoutUrl = "https://pay.payos.vn/web/124c33293c934a85be5b7f8761a27a07",
                bin = "970415",
                accountNumber = "1010099887766"
            });
            return new { code = "00", data, signature = PayOsChecksum.Sign(data, ChecksumKey) };
        }));

        var clientSuccess = PayOsClient.ForChannel(httpSuccess, options);
        var link = await clientSuccess.CreateAsync(111, 100000, CancellationToken.None);
        Assert.NotNull(link);

        // 2. Reject when accountNumber mismatches
        using var httpMismatchedAccount = new HttpClient(new Transport(_ =>
        {
            var data = JsonSerializer.SerializeToElement(new
            {
                orderCode = 111,
                amount = 100000,
                status = "PENDING",
                checkoutUrl = "https://pay.payos.vn/web/124c33293c934a85be5b7f8761a27a07",
                bin = "970415",
                accountNumber = "9999999999999" // mismatch
            });
            return new { code = "00", data, signature = PayOsChecksum.Sign(data, ChecksumKey) };
        }));

        var clientMismatch = PayOsClient.ForChannel(httpMismatchedAccount, options);
        var errAcc = await Assert.ThrowsAsync<PaymentGatewayException>(() => clientMismatch.CreateAsync(111, 100000, CancellationToken.None));
        Assert.Equal("provider_receiver_account_mismatch", errAcc.FailureCode);

        // 3. Reject when bin mismatches
        using var httpMismatchedBin = new HttpClient(new Transport(_ =>
        {
            var data = JsonSerializer.SerializeToElement(new
            {
                orderCode = 111,
                amount = 100000,
                status = "PENDING",
                checkoutUrl = "https://pay.payos.vn/web/124c33293c934a85be5b7f8761a27a07",
                bin = "970422", // mismatch
                accountNumber = "1010099887766"
            });
            return new { code = "00", data, signature = PayOsChecksum.Sign(data, ChecksumKey) };
        }));

        var clientBinMismatch = PayOsClient.ForChannel(httpMismatchedBin, options);
        var errBin = await Assert.ThrowsAsync<PaymentGatewayException>(() => clientBinMismatch.CreateAsync(111, 100000, CancellationToken.None));
        Assert.Equal("provider_receiver_bin_mismatch", errBin.FailureCode);

        // 4. Reject when recipient info missing from creation response
        using var httpMissingRecipient = new HttpClient(new Transport(_ =>
        {
            var data = JsonSerializer.SerializeToElement(new
            {
                orderCode = 111,
                amount = 100000,
                status = "PENDING",
                checkoutUrl = "https://pay.payos.vn/web/124c33293c934a85be5b7f8761a27a07"
            });
            return new { code = "00", data, signature = PayOsChecksum.Sign(data, ChecksumKey) };
        }));

        var clientMissing = PayOsClient.ForChannel(httpMissingRecipient, options);
        var errMissing = await Assert.ThrowsAsync<PaymentGatewayException>(() => clientMissing.CreateAsync(111, 100000, CancellationToken.None));
        Assert.Equal("provider_receiver_bin_mismatch", errMissing.FailureCode);
    }

    [Fact]
    public async Task PayOsClient_CreateAsync_WithExpectedReceiver_RejectsGetRetryWhenReceiverUnproven()
    {
        var options = new PayOsChannelOptions(
            "seller-client",
            "seller-api",
            ChecksumKey,
            "https://example.invalid/return",
            "https://example.invalid/cancel",
            "970415",
            "1010099887766"
        );

        using var http = new HttpClient(new Transport(req =>
        {
            // Initial POST returns error
            return new { code = "231", desc = "Already exists" };
        }));

        var client = PayOsClient.ForChannel(http, options);
        // GET lookup is disabled when expected receiver is configured; fails fast rather than using unproven GET
        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => client.CreateAsync(222, 100000, CancellationToken.None));
        Assert.Equal("provider_code_231", ex.FailureCode);
    }

    [Fact]
    public async Task ConfirmWebhookAsync_MatchingReply_ReturnsResponse()
    {
        var options = new PayOsChannelOptions(
            "seller-client",
            "seller-api",
            ChecksumKey,
            "https://example.invalid/return",
            "https://example.invalid/cancel",
            "970415",
            "1010099887766",
            "TIEM HOA SO 1"
        );

        string? capturedWebhookUrl = null;
        using var http = new HttpClient(new Transport(req =>
        {
            var body = req.Content!.ReadFromJsonAsync<JsonElement>().Result;
            capturedWebhookUrl = body.GetProperty("webhookUrl").GetString();

            return new
            {
                code = "00",
                data = new
                {
                    webhookUrl = capturedWebhookUrl,
                    accountNumber = "1010099887766",
                    accountName = "TIEM HOA SO 1",
                    name = "VietinBank",
                    shortName = "ICB"
                }
            };
        }));

        var client = PayOsClient.ForChannel(http, options);
        var res = await client.ConfirmWebhookAsync("https://api.florabot.test/api/payments/channels/test/webhook", CancellationToken.None);

        Assert.Equal("1010099887766", res.AccountNumber);
        Assert.Equal("TIEM HOA SO 1", res.AccountName);
        Assert.Equal("https://api.florabot.test/api/payments/channels/test/webhook", capturedWebhookUrl);
    }

    [Fact]
    public async Task ConfirmWebhookAsync_ProviderError_ThrowsPaymentGatewayException()
    {
        var options = new PayOsChannelOptions(
            "seller-client",
            "seller-api",
            ChecksumKey,
            "https://example.invalid/return",
            "https://example.invalid/cancel"
        );

        using var http = new HttpClient(new Transport(_ => new
        {
            code = "10",
            desc = "Invalid webhook url"
        }));

        var client = PayOsClient.ForChannel(http, options);
        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            client.ConfirmWebhookAsync("https://api.florabot.test/webhook", CancellationToken.None));
        Assert.Equal("provider_code_10", ex.FailureCode);
    }

    [Fact]
    public void PayOsChecksum_MultipleSellers_SignaturesAreIsolated()
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            orderCode = 999,
            amount = 500000,
            accountNumber = "1010099887766"
        });

        var seller1Key = "seller-1-checksum-key-32bytes-ok!";
        var seller2Key = "seller-2-checksum-key-32bytes-ok!";
        var platformKey = "platform-checksum-key-32bytes-ok!";

        var seller1Sig = PayOsChecksum.Sign(data, seller1Key);
        var seller2Sig = PayOsChecksum.Sign(data, seller2Key);
        var platformSig = PayOsChecksum.Sign(data, platformKey);

        Assert.NotEqual(seller1Sig, seller2Sig);
        Assert.NotEqual(seller1Sig, platformSig);

        // Verification succeeds with matching key
        Assert.True(PayOsChecksum.Verify(data, seller1Sig, seller1Key));
        Assert.True(PayOsChecksum.Verify(data, seller2Sig, seller2Key));
        Assert.True(PayOsChecksum.Verify(data, platformSig, platformKey));

        // Cross-key verification fails (no fallback / isolation)
        Assert.False(PayOsChecksum.Verify(data, seller1Sig, seller2Key));
        Assert.False(PayOsChecksum.Verify(data, seller1Sig, platformKey));
        Assert.False(PayOsChecksum.Verify(data, seller2Sig, seller1Key));
        Assert.False(PayOsChecksum.Verify(data, platformSig, seller1Key));
    }

    [Fact]
    public void PayOsEndpoints_IsWebhookConfirmationSample_ValidatesCorrectly()
    {
        var sampleData = JsonSerializer.SerializeToElement(new
        {
            orderCode = 123,
            amount = 3000,
            description = "VQRIO123"
        });
        var sig = PayOsChecksum.Sign(sampleData, ChecksumKey);

        var validWebhook = new PayOsWebhook(sampleData, sig, "00", "success", true);
        Assert.True(PayOsEndpoints.IsWebhookConfirmationSample(validWebhook, ChecksumKey));

        // Wrong code
        var wrongCode = new PayOsWebhook(sampleData, sig, "01", "success", true);
        Assert.False(PayOsEndpoints.IsWebhookConfirmationSample(wrongCode, ChecksumKey));

        // Wrong desc
        var wrongDesc = new PayOsWebhook(sampleData, sig, "00", "fail", true);
        Assert.False(PayOsEndpoints.IsWebhookConfirmationSample(wrongDesc, ChecksumKey));

        // Tampered signature
        var tamperedSig = new PayOsWebhook(sampleData, new string('0', 64), "00", "success", true);
        Assert.False(PayOsEndpoints.IsWebhookConfirmationSample(tamperedSig, ChecksumKey));

        // Different orderCode
        var differentData = JsonSerializer.SerializeToElement(new
        {
            orderCode = 124,
            amount = 3000,
            description = "VQRIO123"
        });
        var differentSig = PayOsChecksum.Sign(differentData, ChecksumKey);
        var wrongOrder = new PayOsWebhook(differentData, differentSig, "00", "success", true);
        Assert.False(PayOsEndpoints.IsWebhookConfirmationSample(wrongOrder, ChecksumKey));
    }

    private sealed class Transport(Func<HttpRequestMessage, object> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response(request)) });
    }
}
