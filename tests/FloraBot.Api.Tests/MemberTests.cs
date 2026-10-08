using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class MemberTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("CUSTOMER", false, true)]
    [InlineData("SELLER", false, true)]
    [InlineData("CUSTOMER", true, false)]
    [InlineData("SELLER", true, false)]
    [InlineData("ADMIN", false, false)]
    [InlineData("STAFF", false, false)]
    public async Task SharedIdentityPolicyRejectsDeviceSessionsAndOperationsRoles(string role, bool kiosk, bool allowed)
    {
        using var scope = factory.Services.CreateScope();
        var claims = new List<System.Security.Claims.Claim> { new("role", role) };
        if (kiosk) claims.Add(new("kiosk_id", Guid.NewGuid().ToString()));
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test", "sub", "role"));
        var authorization = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(principal, null, "MemberIdentity");
        Assert.Equal(allowed, result.Succeeded);
    }

    [Fact]
    public async Task RegistrationProfileAndPasswordRevocationUseOnlyCustomerRole()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var email = $"member-{Guid.NewGuid():N}@example.invalid";
        const string password = "Member-test-password-2026";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", new { fullName = "Member", email, password, role = "ADMIN" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", new { fullName = "Member", email, password = "short" })).StatusCode);
        var registered = await client.PostAsJsonAsync("/api/auth/register", new { fullName = "Member test", email, password });
        registered.EnsureSuccessStatusCode();
        var session = await registered.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.Equal("CUSTOMER", session!.Role);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/register", new { fullName = "Member test", email = email.ToUpperInvariant(), password })).StatusCode);
        var cookie = registered.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("florabot_access=")).Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5173");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/preorders")).StatusCode);
        var profile = await client.GetFromJsonAsync<MemberProfile>("/api/member/profile");
        Assert.Equal(email, profile!.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/member/profile", new { fullName = "Updated member" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/member/password", new { currentPassword = "incorrect", newPassword = password + "new" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/member/password", new { currentPassword = password, newPassword = password + "new" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/member/profile")).StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email, password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email, password = password + "new" })).StatusCode);
    }

    [Theory]
    [InlineData("CUSTOMER")]
    [InlineData("SELLER")]
    public async Task ResetTokenExpiresAndCanOnlyBeUsedOnce(string role)
    {
        using var app = factory.WithWebHostBuilder(_ => { });
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var email = $"reset-{Guid.NewGuid():N}@example.invalid";
        var registered = await client.PostAsJsonAsync("/api/auth/register", new { fullName = "Reset test", email, password = "Initial-test-password" });
        registered.EnsureSuccessStatusCode();
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        if (role == "SELLER")
        {
            await using var promote = source.CreateCommand("UPDATE identity.users SET role='SELLER',seller_id='20000000-0000-0000-0000-000000000001' WHERE email=@email");
            promote.Parameters.AddWithValue("email", email);
            await promote.ExecuteNonQueryAsync();
        }
        await using var read = source.CreateCommand("SELECT id,password_hash FROM identity.users WHERE email=@email");
        read.Parameters.AddWithValue("email", email);
        await using var reader = await read.ExecuteReaderAsync(); await reader.ReadAsync();
        var id = reader.GetGuid(0); var hash = reader.GetString(1);
        var version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hash)));
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("member-password-reset-v1");
        string Token(DateTimeOffset expiry) => protector.Protect(JsonSerializer.Serialize(new { UserId = id, Version = version, ExpiresAt = expiry }));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", new { token = Token(DateTimeOffset.UtcNow.AddMinutes(-1)), newPassword = "Updated-test-password" })).StatusCode);
        var token = Token(DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "Updated-test-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "Another-test-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/auth/forgot-password", new { email })).StatusCode);
    }

    [Fact]
    public async Task ShopOwnerCanManageSameIdentityAndPasswordWithoutAdminAccess()
    {
        await using var app = new ApiFactory();
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var id = Guid.NewGuid();
        var email = $"shop-member-{id:N}@example.invalid";
        const string password = "Shop-member-password-2026";
        await using var insert = source.CreateCommand("""
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@id,@email,@hash,'Shop member','SELLER','20000000-0000-0000-0000-000000000001')
            """);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("hash", BCrypt.Net.BCrypt.HashPassword(password, 12));
        await insert.ExecuteNonQueryAsync();
        try
        {
            using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
            login.EnsureSuccessStatusCode();
            client.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("florabot_access=")).Split(';')[0]);
            client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5173");
            var profile = await client.GetFromJsonAsync<MemberProfile>("/api/member/profile");
            Assert.Equal(id, profile!.Id);
            Assert.Equal(email, profile.Email);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/preorders")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/member/profile", new { fullName = "" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/member/profile", new { fullName = "Updated shop owner" })).StatusCode);
            Assert.Equal("Updated shop owner", (await client.GetFromJsonAsync<MemberProfile>("/api/member/profile"))!.FullName);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/member/password", new { currentPassword = "wrong", newPassword = password + "new" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/member/password", new { currentPassword = password, newPassword = password + "new" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/member/profile")).StatusCode);
            client.DefaultRequestHeaders.Remove("Cookie");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email, password })).StatusCode);
            var renewed = await client.PostAsJsonAsync("/api/auth/login", new { email, password = password + "new" });
            renewed.EnsureSuccessStatusCode();
            var session = await renewed.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.Equal(id, session!.Id);
            Assert.Equal("SELLER", session.Role);
        }
        finally
        {
            await using var cleanup = source.CreateCommand("DELETE FROM identity.users WHERE id=@id");
            cleanup.Parameters.AddWithValue("id", id);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
