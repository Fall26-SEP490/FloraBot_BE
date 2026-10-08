using System.Net;
using System.Net.Http.Json;

namespace FloraBot.Api.Tests;

public sealed class ReceiptSecurityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task PublicReceiptReadsAndWritesShareTheIpLimitAndNeverCacheDenials()
    {
        using var app = factory.WithWebHostBuilder(_ => { });
        using var client = app.CreateClient();
        for (var index = 0; index < 31; index++)
        {
            var response = (index % 3) switch
            {
                0 => await client.PostAsJsonAsync("/api/receipts/lookup", new { orderId = Guid.Empty, trackingToken = "ABCD2345" }),
                1 => await client.PostAsJsonAsync("/api/receipts/flows/submit_refund_info", new { p_order = Guid.Empty, p_tracking = "ABCD2345", p_bank = "ACB", p_account_enc = "001234567890", p_holder = "Nguyen Van A" }),
                _ => await client.PostAsJsonAsync("/api/receipts/flows/open_dispute", new { p_order = Guid.Empty, p_tracking = "ABCD2345", p_reason = "Test", p_photo = "https://example.invalid/test.jpg" })
            };
            Assert.Equal(index < 30 ? HttpStatusCode.NotFound : HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }
}
