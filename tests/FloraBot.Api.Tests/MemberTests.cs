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

    [Fact]
    public async Task ResetTokenExpiresAndCanOnlyBeUsedOnce()
    {
        using var app = factory.WithWebHostBuilder(_ => { });
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var email = $"reset-{Guid.NewGuid():N}@example.invalid";
        var registered = await client.PostAsJsonAsync("/api/auth/register", new { fullName = "Reset test", email, password = "Initial-test-password" });
        registered.EnsureSuccessStatusCode();
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
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
}
