using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Payment;
using Microsoft.Extensions.Configuration;

namespace FloraBot.Api.Tests;

public sealed class PayOsClientQrTests
{
    // Example VietQR payload from the payOS API documentation. Tests never contact payOS.
    public const string QrPayload = "00020101021238570010A000000727012700069704220113113366668888020899998888530370454061000005802VN62230819THANH TOAN DON HANG6304BE36";
    private const string Key = "test-only-qr-checksum-key";
    private const string Url = "https://pay.payos.vn/web/124c33293c934a85be5b7f8761a27a07";

    [Theory]
    [InlineData(false, 123L)]
    [InlineData(true, 123L)]
    [InlineData(false, 9007199254740991L)]
    [InlineData(true, 9007199254740991L)]
    public async Task CreatesBankCompatibleLinkWithSignedShortDescription(bool seller, long orderCode)
    {
        using var http = new HttpClient(new ShortDescriptionBank(orderCode));
        var gateway = seller
            ? PayOsClient.ForChannel(http, new PayOsChannelOptions("client", "api", Key,
                "https://example.invalid/return", "https://example.invalid/cancel",
                "970422", "113366668888"))
            : Client(http);

        var result = await gateway.CreateAsync(orderCode, 10000, CancellationToken.None, 1800000000);

        Assert.Equal(orderCode, result.OrderCode);
        Assert.Equal(10000, result.Amount);
        Assert.Equal(Url, result.CheckoutUrl);
        Assert.Equal(QrPayload, result.QrCode);
    }

    private sealed class ShortDescriptionBank(long orderCode) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var description = body.GetProperty("description").GetString()!;
            // Some payOS bank channels reject descriptions longer than nine characters.
            if (description.Length > 9)
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { code = "400", desc = "description too long" }) };
            Assert.Matches("^[A-Za-z0-9 ]{1,9}$", description);
            Assert.Equal(orderCode, body.GetProperty("orderCode").GetInt64());
            Assert.Equal(1800000000, body.GetProperty("expiredAt").GetInt64());
            var signed = JsonSerializer.SerializeToElement(new
            {
                amount = body.GetProperty("amount").GetInt64(),
                cancelUrl = body.GetProperty("cancelUrl").GetString(),
                description,
                orderCode,
                returnUrl = body.GetProperty("returnUrl").GetString()
            });
            Assert.True(PayOsChecksum.Verify(signed, body.GetProperty("signature").GetString()!, Key));
            var data = JsonSerializer.SerializeToElement(new
            {
                orderCode, amount = 10000, status = "PENDING", checkoutUrl = Url,
                qrCode = QrPayload, bin = "970422", accountNumber = "113366668888"
            });
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Envelope(data)) };
        }
    }

    [Fact]
    public async Task ReturnsExactVietQrFromVerifiedCreationResponse()
    {
        var data = Data(QrPayload);
        using var http = new HttpClient(new Transport(_ => Envelope(data)));
        var result = await Client(http).CreateAsync(123, 10000, CancellationToken.None);
        Assert.Equal(QrPayload, result.QrCode);
        Assert.Equal(Url, result.CheckoutUrl);
        Assert.Equal(10000, result.Amount);
        Assert.Equal(123, result.OrderCode);
    }

    [Fact]
    public async Task RejectsQrChangedAfterResponseWasSigned()
    {
        var original = Data(QrPayload);
        var changed = Data(QrPayload + "tampered");
        using var http = new HttpClient(new Transport(_ => new
        {
            code = "00",
            data = changed,
            signature = PayOsChecksum.Sign(original, Key)
        }));
        var error = await Assert.ThrowsAsync<PaymentGatewayException>(() => Client(http).CreateAsync(123, 10000, CancellationToken.None));
        Assert.Equal("provider_signature_invalid", error.FailureCode);
    }

    [Theory]
    [InlineData("https://pay.payos.vn/web/124c33293c934a85be5b7f8761a27a07")]
    [InlineData("")]
    [InlineData("000201\nnot-a-qr")]
    [InlineData(123)]
    public async Task RejectsInvalidQrInsteadOfReturningItAsBankPaymentCode(object value)
    {
        using var http = new HttpClient(new Transport(_ => Envelope(Data(value))));
        var error = await Assert.ThrowsAsync<PaymentGatewayException>(() => Client(http).CreateAsync(123, 10000, CancellationToken.None));
        Assert.Equal("provider_qr_code_invalid", error.FailureCode);
    }

    [Fact]
    public async Task RejectsOversizedQr()
    {
        using var http = new HttpClient(new Transport(_ => Envelope(Data("000201" + new string('1', 4096)))));
        var error = await Assert.ThrowsAsync<PaymentGatewayException>(() => Client(http).CreateAsync(123, 10000, CancellationToken.None));
        Assert.Equal("provider_qr_code_invalid", error.FailureCode);
    }

    [Fact]
    public async Task DuplicateLookupWithoutQrPreservesCheckoutFallback()
    {
        var methods = new List<HttpMethod>();
        var lookup = JsonSerializer.SerializeToElement(new
        {
            id = "124c33293c934a85be5b7f8761a27a07",
            orderCode = 123,
            amount = 10000,
            status = "PENDING"
        });
        using var http = new HttpClient(new Transport(request =>
        {
            methods.Add(request.Method);
            return request.Method == HttpMethod.Post ? new { code = "231" } : Envelope(lookup);
        }));
        var result = await Client(http).CreateAsync(123, 10000, CancellationToken.None);
        Assert.Null(result.QrCode);
        Assert.Equal(Url, result.CheckoutUrl);
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Get }, methods);
    }

    private static PayOsClient Client(HttpClient http) => new(http, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["PAYOS_CLIENT_ID"] = "test-client",
        ["PAYOS_API_KEY"] = "test-api",
        ["PAYOS_CHECKSUM_KEY"] = Key,
        ["PAYOS_RETURN_URL"] = "https://example.invalid/return",
        ["PAYOS_CANCEL_URL"] = "https://example.invalid/cancel"
    }).Build());

    private static JsonElement Data(object? qr) => JsonSerializer.SerializeToElement(new
    {
        orderCode = 123,
        amount = 10000,
        status = "PENDING",
        checkoutUrl = Url,
        qrCode = qr
    });

    private static object Envelope(JsonElement data) => new { code = "00", data, signature = PayOsChecksum.Sign(data, Key) };

    private sealed class Transport(Func<HttpRequestMessage, object> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response(request)) });
    }
}
