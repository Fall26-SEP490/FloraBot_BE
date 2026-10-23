using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FloraBot.Api.Auth;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FloraBot.Api.Tests;

public sealed class ForgotPasswordFlowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(User User, Seller? Seller)> CreateIsolatedUserAsync(string role, string status = "ACTIVE")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        Seller? seller = null;
        if (role is "SELLER" or "SELLER_STAFF")
        {
            // Reuse only the seeded tenant identity; do not mutate its shop or subscription.
            var sellerId = await db.Sellers.AsNoTracking().Select(s => s.Id).FirstAsync();
            seller = new Seller { Id = sellerId };
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"fp-{Guid.NewGuid():N}@isolated-test.example",
            Phone = "09" + Random.Shared.Next(10000000, 99999999),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("InitialPass123!"),
            FullName = $"Test User {role}",
            Role = role,
            SellerId = seller?.Id,
            Status = status,
            LoyaltyPoints = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (user, seller);
    }

    private static string ExtractTokenFromEmail(TransactionalEmail email)
    {
        var match = Regex.Match(email.Text, @"#token=([^\s]+)");
        Assert.True(match.Success, "Expected #token fragment in email text");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    [Fact]
    public async Task ActualForgotEndpoint_MailGeneratedToken_Extraction_Reset204_NewLogin200_OldLogin401_Replay400()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("MEMBER_WEB_URL", "https://portal.florabot.test"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // 1. Call forgot-password endpoint
        var forgotResp = await client.PostAsJsonAsync("/api/auth/forgot-password", new PasswordResetRequest(user.Email!));
        Assert.Equal(HttpStatusCode.OK, forgotResp.StatusCode);
        var body = await forgotResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nếu email thuộc tài khoản thành viên, bạn sẽ nhận được liên kết đặt lại mật khẩu.", body.GetProperty("message").GetString());

        // 2. Extract token from actual sent email
        var sentMail = factory.EmailSender.SentEmails.LastOrDefault(m => string.Equals(m.Recipient, user.Email, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(sentMail);
        var token = ExtractTokenFromEmail(sentMail);
        Assert.False(string.IsNullOrWhiteSpace(token));

        // 3. Reset password using extracted token
        const string newPassword = "NewSecretPassword123!";
        var resetResp = await client.PostAsJsonAsync("/api/auth/reset-password", new PasswordReset(token, newPassword));
        Assert.Equal(HttpStatusCode.NoContent, resetResp.StatusCode);

        // 4. New password allows login
        var newLoginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, newPassword));
        Assert.Equal(HttpStatusCode.OK, newLoginResp.StatusCode);

        // 5. Old password is rejected
        var oldLoginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, "InitialPass123!"));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLoginResp.StatusCode);

        // 6. Token replay is rejected
        var replayResp = await client.PostAsJsonAsync("/api/auth/reset-password", new PasswordReset(token, "AnotherPass123!"));
        Assert.Equal(HttpStatusCode.BadRequest, replayResp.StatusCode);
    }

    [Fact]
    public async Task Seller_PasswordReset_PreservesRoleAndSellerId()
    {
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        Assert.NotNull(seller);

        using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("MEMBER_WEB_URL", "https://portal.florabot.test"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var forgotResp = await client.PostAsJsonAsync("/api/auth/forgot-password", new PasswordResetRequest(sellerUser.Email!));
        Assert.Equal(HttpStatusCode.OK, forgotResp.StatusCode);

        var sentMail = factory.EmailSender.SentEmails.LastOrDefault(m => string.Equals(m.Recipient, sellerUser.Email, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(sentMail);
        var token = ExtractTokenFromEmail(sentMail);

        const string newPassword = "SellerNewPassword123!";
        var resetResp = await client.PostAsJsonAsync("/api/auth/reset-password", new PasswordReset(token, newPassword));
        Assert.Equal(HttpStatusCode.NoContent, resetResp.StatusCode);

        var loginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(sellerUser.Email!, newPassword));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var session = await loginResp.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.NotNull(session);
        Assert.Equal("SELLER", session.Role);
        Assert.Equal(seller.Id, session.SellerId);
    }

    [Fact]
    public async Task SessionRevocation_ActiveSessionRejected_AfterPasswordReset()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("MEMBER_WEB_URL", "https://portal.florabot.test"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // Authenticate and retrieve florabot_access cookie
        var loginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, "InitialPass123!"));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var accessCookie = loginResp.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("florabot_access=")).Split(';')[0];

        // Ensure session is currently valid
        using var authClient = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        authClient.DefaultRequestHeaders.Add("Cookie", accessCookie);
        var meResp = await authClient.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);

        // Reset password
        await client.PostAsJsonAsync("/api/auth/forgot-password", new PasswordResetRequest(user.Email!));
        var sentMail = factory.EmailSender.SentEmails.LastOrDefault(m => string.Equals(m.Recipient, user.Email, StringComparison.OrdinalIgnoreCase));
        var token = ExtractTokenFromEmail(sentMail!);
        var resetResp = await client.PostAsJsonAsync("/api/auth/reset-password", new PasswordReset(token, "NewPasswordRevoke123!"));
        Assert.Equal(HttpStatusCode.NoContent, resetResp.StatusCode);

        // Prior session is revoked due to credential_version rotation
        var meRevokedResp = await authClient.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meRevokedResp.StatusCode);
    }

    [Fact]
    public async Task TamperedToken_ReturnsBadRequest400()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("MEMBER_WEB_URL", "https://portal.florabot.test"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        await client.PostAsJsonAsync("/api/auth/forgot-password", new PasswordResetRequest(user.Email!));
        var sentMail = factory.EmailSender.SentEmails.LastOrDefault(m => string.Equals(m.Recipient, user.Email, StringComparison.OrdinalIgnoreCase));
        var validToken = ExtractTokenFromEmail(sentMail!);

        var tamperedToken = validToken.Substring(0, validToken.Length - 4) + "XXXX";
        var resp = await client.PostAsJsonAsync("/api/auth/reset-password", new PasswordReset(tamperedToken, "ValidPassword123!"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var invalidResp = await client.PostAsJsonAsync("/api/auth/reset-password", new PasswordReset("completely-invalid-token", "ValidPassword123!"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidResp.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_ReturnsBadRequest400()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("MEMBER_WEB_URL", "https://portal.florabot.test"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var scope = app.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("member-password-reset-v1");
        var version = TokenService.CredentialVersion(user);
        var expiredToken = protector.Protect(JsonSerializer.Serialize(new { UserId = user.Id, Version = version, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) }));

        var resp = await client.PostAsJsonAsync("/api/auth/reset-password", new PasswordReset(expiredToken, "ValidPassword123!"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task NonexistentEmail_GenericSame200_NoMailSent()
    {
        using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("MEMBER_WEB_URL", "https://portal.florabot.test"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var nonexistentEmail = $"nonexistent-{Guid.NewGuid():N}@example.test";
        var resp = await client.PostAsJsonAsync("/api/auth/forgot-password", new PasswordResetRequest(nonexistentEmail));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nếu email thuộc tài khoản thành viên, bạn sẽ nhận được liên kết đặt lại mật khẩu.", body.GetProperty("message").GetString());

        var mail = factory.EmailSender.SentEmails.FirstOrDefault(m => string.Equals(m.Recipient, nonexistentEmail, StringComparison.OrdinalIgnoreCase));
        Assert.Null(mail);
    }

    [Theory]
    [InlineData("LOCKED")]
    [InlineData("DISABLED")]
    public async Task DisabledOrLockedAccount_ReturnsGeneric200_NoMailSent(string status)
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER", status: status);
        using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("MEMBER_WEB_URL", "https://portal.florabot.test"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var resp = await client.PostAsJsonAsync("/api/auth/forgot-password", new PasswordResetRequest(user.Email!));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nếu email thuộc tài khoản thành viên, bạn sẽ nhận được liên kết đặt lại mật khẩu.", body.GetProperty("message").GetString());

        var mail = factory.EmailSender.SentEmails.FirstOrDefault(m => string.Equals(m.Recipient, user.Email, StringComparison.OrdinalIgnoreCase));
        Assert.Null(mail);
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("STAFF")]
    [InlineData("OPERATIONS_MANAGER")]
    [InlineData("TECHNICIAN")]
    [InlineData("SELLER_STAFF")]
    public async Task PrivilegedAccount_ReturnsGeneric200_NoMailSent(string privilegedRole)
    {
        var (user, _) = await CreateIsolatedUserAsync(privilegedRole);
        using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("MEMBER_WEB_URL", "https://portal.florabot.test"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var resp = await client.PostAsJsonAsync("/api/auth/forgot-password", new PasswordResetRequest(user.Email!));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nếu email thuộc tài khoản thành viên, bạn sẽ nhận được liên kết đặt lại mật khẩu.", body.GetProperty("message").GetString());

        var mail = factory.EmailSender.SentEmails.FirstOrDefault(m => string.Equals(m.Recipient, user.Email, StringComparison.OrdinalIgnoreCase));
        Assert.Null(mail);
    }

    [Fact]
    public async Task FailedEmail_Returns503()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("MEMBER_WEB_URL", "https://portal.florabot.test"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        factory.EmailSender.ShouldFail = true;
        try
        {
            var resp = await client.PostAsJsonAsync("/api/auth/forgot-password", new PasswordResetRequest(user.Email!));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        }
        finally
        {
            factory.EmailSender.ShouldFail = false;
        }
    }

    [Theory]
    [InlineData("http://insecure.florabot.test")]
    [InlineData("https://user:pass@portal.florabot.test")]
    [InlineData("not-a-valid-uri")]
    [InlineData("")]
    public async Task InvalidOrigin_Returns503(string invalidOrigin)
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("MEMBER_WEB_URL", invalidOrigin));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var resp = await client.PostAsJsonAsync("/api/auth/forgot-password", new PasswordResetRequest(user.Email!));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task ConcurrentReset_OnlyOneSucceeds()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("MEMBER_WEB_URL", "https://portal.florabot.test"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        await client.PostAsJsonAsync("/api/auth/forgot-password", new PasswordResetRequest(user.Email!));
        var sentMail = factory.EmailSender.SentEmails.LastOrDefault(m => string.Equals(m.Recipient, user.Email, StringComparison.OrdinalIgnoreCase));
        var token = ExtractTokenFromEmail(sentMail!);

        var task1 = client.PostAsJsonAsync("/api/auth/reset-password", new PasswordReset(token, "ConcurrentPass1!"));
        var task2 = client.PostAsJsonAsync("/api/auth/reset-password", new PasswordReset(token, "ConcurrentPass2!"));

        var responses = await Task.WhenAll(task1, task2);
        var statusCodes = responses.Select(r => r.StatusCode).ToList();

        Assert.Single(statusCodes, HttpStatusCode.NoContent);
        Assert.Contains(statusCodes, s => s is HttpStatusCode.Conflict or HttpStatusCode.BadRequest);
    }
}
