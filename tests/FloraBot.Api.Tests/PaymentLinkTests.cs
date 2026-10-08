using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Modules.Payment;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class PaymentLinkTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["PAYOS_CLIENT_ID"] = "test-client",
        ["PAYOS_API_KEY"] = "test-api",
        ["PAYOS_CHECKSUM_KEY"] = "test-only-checksum-key",
        ["PAYOS_RETURN_URL"] = "https://example.invalid/return",
        ["PAYOS_CANCEL_URL"] = "https://example.invalid/cancel"
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriptionLinkUsesStoredAmountAndStableCodeWithTenantIsolation(bool loseFirstResponse)
    {
        var transport = new Gateway(loseFirstResponse ? "timeout" : null);
        using var app = factory.WithWebHostBuilder(builder =>
        {
            foreach (var setting in Settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureServices(services => services.AddHttpClient<PayOsClient>().ConfigurePrimaryHttpMessageHandler(() => transport));
        });
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var create = source.CreateCommand("SELECT flow.subscribe('20000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000003',1)");
        var sub = (Guid)(await create.ExecuteScalarAsync())!;
        var overview = await client.GetFromJsonAsync<FloraBot.Api.Infrastructure.SubscriptionOverviewResponse>("/api/sellers/20000000-0000-0000-0000-000000000001/subscriptions");
        Assert.Equal(400000, overview!.Package!.MonthlyFee);
        Assert.Contains(overview.Subscriptions, row => row.Id == sub && row.Status == "PENDING_PAYMENT" && row.Price == 400000);
        var path = $"/api/sellers/20000000-0000-0000-0000-000000000001/subscriptions/{sub}/payment-link";
        var first = await client.PostAsync(path, null);
        if (loseFirstResponse)
        {
            Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
            first = await client.PostAsync(path, null);
        }
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var link = await first.Content.ReadFromJsonAsync<PaymentLinkResponse>();
        Assert.Equal(400000, link!.Amount);
        var again = await client.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(link, await again.Content.ReadFromJsonAsync<PaymentLinkResponse>());
        Assert.Equal(loseFirstResponse ? 3 : 2, transport.Posts);
        Assert.Equal(loseFirstResponse ? 2 : 1, transport.Gets);
        var denied = await client.PostAsync(path.Replace("000000000001/subscriptions", "000000000002/subscriptions"), null);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(loseFirstResponse ? 3 : 2, transport.Posts);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: Guid.Parse("10000000-0000-0000-0000-000000000004"), sellerId: Guid.Parse("20000000-0000-0000-0000-000000000002")));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(path.Replace("000000000001/subscriptions", "000000000002/subscriptions"), null)).StatusCode);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("amount")]
    [InlineData("url")]
    [InlineData("status")]
    public async Task UntrustedGatewayResponsesAreRejected(string defect)
    {
        using var http = new HttpClient(new Gateway(defect));
        var gateway = new PayOsClient(http, new ConfigurationBuilder().AddInMemoryCollection(Settings).Build());
        await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.CreateAsync(123, 400000, CancellationToken.None));
    }

    [Fact]
    public void TransactionArraysHaveCanonicalSortedJsonForGatewaySignatures()
    {
        var data = JsonSerializer.Deserialize<JsonElement>("{\"transactions\": [ { \"reference\": \"ref\", \"amount\": 400000 } ], \"orderCode\": 123}");
        var canonical = "orderCode=123&transactions=[{\"amount\":400000,\"reference\":\"ref\"}]";
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("key"), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        Assert.Equal(expected, PayOsChecksum.Sign(data, "key"));
    }

    private sealed class Gateway(string? defect = null) : HttpMessageHandler
    {
        public int Posts { get; private set; }
        public int Gets { get; private set; }
        private long? orderCode;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api-merchant.payos.vn", request.RequestUri!.Host);
            Assert.Equal("test-client", request.Headers.GetValues("x-client-id").Single());
            Assert.Equal("test-api", request.Headers.GetValues("x-api-key").Single());
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                var code = body.GetProperty("orderCode").GetInt64();
                var canonical = $"amount=400000&cancelUrl=https://example.invalid/cancel&description=FloraBot {code}&orderCode={code}&returnUrl=https://example.invalid/return";
                var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Settings["PAYOS_CHECKSUM_KEY"]!), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
                Assert.Equal(expected, body.GetProperty("signature").GetString());
                Assert.Equal(400000, body.GetProperty("amount").GetInt64());
                if (orderCode is not null)
                {
                    Assert.Equal(orderCode.Value, code);
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { code = "231", desc = "Already exists" }) };
                }
                orderCode = code;
                if (defect == "timeout") throw new TaskCanceledException("Simulated response loss after gateway accepted order");
            }
            else
            {
                Gets++;
                Assert.EndsWith("/" + orderCode, request.RequestUri.AbsolutePath);
            }
            var fields = new Dictionary<string, object?>
            {
                ["orderCode"] = orderCode,
                ["amount"] = defect == "amount" ? 1 : 400000,
                ["status"] = defect == "status" ? "PAID" : "PENDING"
            };
            if (request.Method == HttpMethod.Get)
            {
                fields["id"] = "124c33293c934a85be5b7f8761a27a07";
                fields["transactions"] = Array.Empty<object>();
            }
            else fields["checkoutUrl"] = defect == "url" ? "https://example.invalid/phishing" : "https://pay.payos.vn/web/124c33293c934a85be5b7f8761a27a07";
            var data = JsonSerializer.SerializeToElement(fields);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    code = "00",
                    data,
                    signature = defect == "signature" ? new string('0', 64) : PayOsChecksum.Sign(data, Settings["PAYOS_CHECKSUM_KEY"]!)
                })
            };
        }
    }
}
