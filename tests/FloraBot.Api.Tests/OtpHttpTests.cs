using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FloraBot.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FloraBot.Api.Tests;

public sealed class OtpHttpTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task DeliveredCodeCreatesKioskBoundCustomerSessionAndCannotReplay()
    {
        var sms = new SmsTransport();
        using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("SMS_WEBHOOK_URL", "https://example.invalid/sms"); builder.UseSetting("SMS_API_TOKEN", "test-sms-key");
            builder.ConfigureServices(services => services.AddHttpClient("sms").ConfigurePrimaryHttpMessageHandler(() => sms));
        });
        using var kiosk = app.CreateClient(); kiosk.DefaultRequestHeaders.Add("X-Kiosk-Key", "demo-key-q1");
        const string basePath = "/api/kiosks/40000000-0000-0000-0000-000000000001";
        var phone = "09" + RandomNumberGenerator.GetInt32(10000000, 99999999);
        var issued = await kiosk.PostAsJsonAsync(basePath + "/otp/request", new { phone });
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").ValueKind);
        Assert.Equal(phone, sms.Phone);
        var verified = await kiosk.PostAsJsonAsync(basePath + "/otp/verify", new { phone, code = sms.Code });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var session = await verified.Content.ReadFromJsonAsync<CustomerSession>();
        Assert.Equal(600, session!.ExpiresInSeconds);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken);
        Assert.Equal("CUSTOMER", jwt.Claims.Single(claim => claim.Type == "role").Value);
        Assert.Equal("40000000-0000-0000-0000-000000000001", jwt.Claims.Single(claim => claim.Type == "kiosk_id").Value);
        Assert.InRange((jwt.ValidTo - DateTime.UtcNow).TotalSeconds, 590, 600);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Value == phone);
        using var customer = app.CreateClient(); customer.DefaultRequestHeaders.Authorization = new("Bearer", session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync(basePath + "/catalog/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/kiosks/40000000-0000-0000-0000-000000000002/catalog/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync(basePath + "/otp/request", new { phone })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await kiosk.PostAsJsonAsync(basePath + "/otp/verify", new { phone, code = sms.Code })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await customer.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    private sealed class SmsTransport : HttpMessageHandler
    {
        public string? Phone { get; private set; }
        public string? Code { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer test-sms-key", request.Headers.Authorization!.ToString());
            var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Phone = payload.GetProperty("phone").GetString();
            Code = Regex.Match(payload.GetProperty("message").GetString()!, "[0-9]{6}").Value;
            Assert.Equal(6, Code.Length);
            return new(HttpStatusCode.OK);
        }
    }
}
