using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FloraBot.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class OtpHttpTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("SELLER", "ACTIVE", true)]
    [InlineData("CUSTOMER", "LOCKED", false)]
    [InlineData("ADMIN", "ACTIVE", false)]
    [InlineData("STAFF", "ACTIVE", false)]
    [InlineData("OPERATIONS_MANAGER", "ACTIVE", false)]
    [InlineData("TECHNICIAN", "ACTIVE", false)]
    [InlineData("SELLER_STAFF", "ACTIVE", false)]
    public async Task ExistingIdentityIsPreservedWithoutGrantingPortalAccess(string role, string status, bool allowed)
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var otp = scope.ServiceProvider.GetRequiredService<OtpService>();
        var id = Guid.NewGuid();
        var phone = "09" + RandomNumberGenerator.GetInt32(10000000, 99999999);
        const string seller = "20000000-0000-0000-0000-000000000001";
        const string kioskId = "40000000-0000-0000-0000-000000000001";
        const string path = "/api/kiosks/" + kioskId;
        await using var setup = data.CreateCommand("INSERT INTO identity.users(id,phone,full_name,password_hash,role,status,seller_id) VALUES(@id,@phone,'OTP identity test','!unprovisioned',@role,@status,CASE WHEN @role IN ('SELLER', 'SELLER_STAFF') THEN @seller ELSE NULL END)");
        setup.Parameters.AddWithValue("id", id); setup.Parameters.AddWithValue("phone", phone);
        setup.Parameters.AddWithValue("role", role); setup.Parameters.AddWithValue("status", status);
        setup.Parameters.AddWithValue("seller", Guid.Parse(seller)); await setup.ExecuteNonQueryAsync();
        var code = await otp.IssueAsync(Guid.Parse(kioskId), phone);
        using var device = factory.CreateClient(); device.DefaultRequestHeaders.Add("X-Kiosk-Key", "demo-key-q1");
        var response = await device.PostAsJsonAsync(path + "/otp/verify", new { phone = "+84" + phone[1..], code });
        if (!allowed) { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); return; }
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<CustomerSession>())!;
        Assert.False(session.CanForgetAccount);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken);
        Assert.Equal(id.ToString(), jwt.Claims.Single(c => c.Type == "sub").Value);
        Assert.Equal(role, jwt.Claims.Single(c => c.Type == "role").Value);
        Assert.InRange((jwt.ValidTo - DateTime.UtcNow).TotalSeconds, 590, 600);
        using var member = factory.CreateClient(); member.DefaultRequestHeaders.Authorization = new("Bearer", session.AccessToken);
        foreach (var suffix in new[] { "/customer/history", "/customer/points", "/catalog/items" })
            Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(path + suffix)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/member/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/admin/staff")).StatusCode);
        var wallet = await member.GetAsync($"/api/sellers/{seller}/wallet");
        Assert.True(wallet.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/kiosks/40000000-0000-0000-0000-000000000002/customer/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(path + "/flows/forget_customer", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.PostAsJsonAsync(path + "/otp/verify", new { phone, code })).StatusCode);
    }

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
