using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Payment;

namespace FloraBot.Api.Tests;

public sealed class PayOsReceiverEvidenceTests
{
    private static PayOsChannelOptions Options(string client = "client", string api = "api", string checksum = "checksum",
        string bin = "970422", string account = "ROOT1234") => new(client, api, checksum, "https://example.invalid/return",
        "https://example.invalid/cancel", bin, account, "TEST SHOP");

    [Fact]
    public void FingerprintPinsCredentialsAndRootWithoutDelimiterAmbiguity()
    {
        var baseline = Options();
        var hash = PayOsReceiverEvidence.Fingerprint(baseline);
        Assert.Matches("^v1:[0-9a-f]{64}$", hash);
        Assert.True(PayOsReceiverEvidence.MatchesFingerprint(baseline, hash));
        foreach (var changed in new[] { Options(client: "other"), Options(api: "other"), Options(checksum: "other"),
            Options(bin: "970415"), Options(account: "OTHER1234") })
            Assert.False(PayOsReceiverEvidence.MatchesFingerprint(changed, hash));
        Assert.NotEqual(PayOsReceiverEvidence.Fingerprint(Options(client: "a|b", api: "c")),
            PayOsReceiverEvidence.Fingerprint(Options(client: "a", api: "b|c")));
        Assert.False(PayOsReceiverEvidence.MatchesFingerprint(baseline, null));
        Assert.False(PayOsReceiverEvidence.MatchesFingerprint(baseline, "v1:bad"));
    }

    [Fact]
    public void VirtualAccountRequiresVerifiedRootAndEvidenceDoesNotPrintBankAccount()
    {
        var data = JsonSerializer.SerializeToElement(new { bin = "970422", accountNumber = "VA98765432", accountName = "TEST SHOP" });
        var error = Assert.Throws<PaymentGatewayException>(() => PayOsReceiverEvidence.ReadCreation(data, Options(), false));
        Assert.Equal("provider_receiver_account_mismatch", error.FailureCode);
        var evidence = PayOsReceiverEvidence.ReadCreation(data, Options(), true);
        Assert.Equal("VA98765432", evidence.AccountNumber);
        Assert.Equal("VIRTUAL_ACCOUNT", evidence.Mode);
        Assert.DoesNotContain("VA98765432", evidence.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"bin\":970422,\"accountNumber\":\"VA123456\"}")]
    [InlineData("{\"bin\":\"970415\",\"accountNumber\":\"VA123456\"}")]
    [InlineData("{\"bin\":\"970422\"}")]
    [InlineData("{\"bin\":\"970422\",\"accountNumber\":null}")]
    [InlineData("{\"bin\":\"970422\",\"accountNumber\":123456}")]
    [InlineData("{\"bin\":\"970422\",\"accountNumber\":\" VA1234\"}")]
    [InlineData("{\"bin\":\"970422\",\"accountNumber\":\"VA1234\\n\"}")]
    [InlineData("{\"bin\":\"970422\",\"accountNumber\":\"VA1234\",\"accountName\":12}")]
    public void MalformedCreationIsRejectedEvenWithVerifiedCredentials(string json) =>
        Assert.Throws<PaymentGatewayException>(() => PayOsReceiverEvidence.ReadCreation(JsonSerializer.Deserialize<JsonElement>(json), Options(), true));

    [Theory]
    [InlineData("{\"accountNumber\":\"ROOT1234\",\"virtualAccountNumber\":\"VA123456\"}", true, true)]
    [InlineData("{\"accountNumber\":\"VA123456\"}", true, true)]
    [InlineData("{\"accountNumber\":\"ROOT1234\"}", true, true)]
    [InlineData("{}", false, true)]
    [InlineData("{}", true, false)]
    [InlineData("{\"accountNumber\":null}", false, false)]
    [InlineData("{\"accountNumber\":12345678}", false, false)]
    [InlineData("{\"accountNumber\":\"OTHER1234\"}", true, false)]
    [InlineData("{\"accountNumber\":\"ROOT1234\",\"virtualAccountNumber\":\"OTHER1234\"}", true, false)]
    [InlineData("{\"accountNumber\":\"VA123456\",\"virtualAccountNumber\":\"OTHER1234\"}", true, false)]
    [InlineData("{\"accountNumber\":\"ROOT1234\",\"virtualAccountNumber\":123456}", true, false)]
    public void SubsequentRecipientMustAgreeWithOrderEvidence(string json, bool required, bool accepted) =>
        Assert.Equal(accepted, PayOsReceiverEvidence.ValidateSubsequent(JsonSerializer.Deserialize<JsonElement>(json), "ROOT1234", "VA123456", required));

    [Fact]
    public void LegacyOrderCannotAcceptUnknownVirtualAccount()
    {
        var data = JsonSerializer.SerializeToElement(new { accountNumber = "ROOT1234", virtualAccountNumber = "VA123456" });
        Assert.False(PayOsReceiverEvidence.ValidateSubsequent(data, "ROOT1234", null, true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ProviderSignatureIsAlwaysRequiredForVirtualReceiver(bool validSignature, bool accepted)
    {
        var options = Options();
        using var http = new HttpClient(new Transport(options, validSignature));
        var client = PayOsClient.ForChannel(http, options);
        if (accepted)
        {
            var result = await client.CreateForVerifiedSellerAsync(123, 10000, CancellationToken.None, null, true);
            Assert.Equal("VA123456", result.ReceiverEvidence!.AccountNumber);
            Assert.DoesNotContain("VA123456", JsonSerializer.Serialize(result));
        }
        else
        {
            var error = await Assert.ThrowsAsync<PaymentGatewayException>(() => client.CreateForVerifiedSellerAsync(123, 10000, CancellationToken.None, null, true));
            Assert.Equal("provider_signature_invalid", error.FailureCode);
        }
        await Assert.ThrowsAsync<PaymentGatewayException>(() => client.CreateAsync(123, 10000, CancellationToken.None));
    }

    private sealed class Transport(PayOsChannelOptions options, bool valid) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var data = JsonSerializer.SerializeToElement(new
            {
                orderCode = 123,
                amount = 10000,
                status = "PENDING",
                bin = "970422",
                accountNumber = "VA123456",
                checkoutUrl = "https://pay.payos.vn/web/124c33293c934a85be5b7f8761a27a07"
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                { code = "00", data, signature = PayOsChecksum.Sign(data, valid ? options.ChecksumKey : "foreign-shop-key") })
            });
        }
    }
}
