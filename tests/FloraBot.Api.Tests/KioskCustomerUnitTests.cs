using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Ordering;
using FloraBot.Api.Modules.Payment;
using Microsoft.Extensions.Configuration;

namespace FloraBot.Api.Tests;

public sealed class KioskCustomerUnitTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("too short", false)]
    [InlineData("The screen is frozen", true)]
    [InlineData("The door\nwill not open", true)]
    [InlineData("Screen\0is frozen", false)]
    public void PublicSupportRejectsInvalidDescriptions(string? reason, bool expected) => Assert.Equal(expected, KioskCustomerFlows.ValidReason(reason));

    [Fact]
    public void SupportBoundsPayloadSize() => Assert.False(KioskCustomerFlows.ValidReason(new string('a', 1001)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BankQrComesOnlyFromTheSignedProviderResponse(bool tampered)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PAYOS_CLIENT_ID"] = "test-client",
            ["PAYOS_API_KEY"] = "test-api",
            ["PAYOS_CHECKSUM_KEY"] = "test-key",
            ["PAYOS_RETURN_URL"] = "https://example.invalid/return",
            ["PAYOS_CANCEL_URL"] = "https://example.invalid/cancel"
        }).Build();
        var payload = JsonSerializer.SerializeToElement(new { orderCode = 123L, amount = 200000L, status = "PENDING", checkoutUrl = "https://pay.payos.vn/web/0123456789abcdef0123456789abcdef", qrCode = "000201-payOS-signed-bank-qr" });
        using var client = new HttpClient(new Stub(new { code = "00", data = payload, signature = tampered ? "bad-signature" : PayOsChecksum.Sign(payload, "test-key") }));
        var gateway = new PayOsClient(client, config);
        if (tampered) await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.CreateAsync(123, 200000, CancellationToken.None));
        else Assert.Equal("000201-payOS-signed-bank-qr", (await gateway.CreateAsync(123, 200000, CancellationToken.None)).QrCode);
    }

    private sealed class Stub(object envelope) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(envelope) });
    }
}
