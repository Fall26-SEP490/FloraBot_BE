using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class LoginRealmTests
{
    [Theory]
    [InlineData("ADMIN", true)]
    [InlineData("STAFF", true)]
    [InlineData("CUSTOMER", false)]
    [InlineData("SELLER", false)]
    public async Task LoginRejectsTheWrongRealmAndNeverElevatesStaff(string role, bool operations)
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var id = Guid.NewGuid();
        var email = $"realm-{id:N}@example.invalid";
        const string password = "Realm-test-password-2026";
        await using var insert = data.CreateCommand("""
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@id,@email,@hash,'Realm test',@role,
              CASE WHEN @role='SELLER' THEN '20000000-0000-0000-0000-000000000001'::uuid ELSE NULL END)
            """);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("hash", BCrypt.Net.BCrypt.HashPassword(password, 12));
        insert.Parameters.AddWithValue("role", role);
        await insert.ExecuteNonQueryAsync();
        try
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var correct = operations ? "/api/auth/admin/login" : "/api/auth/login";
            var wrong = operations ? "/api/auth/login" : "/api/auth/admin/login";
            var denied = await client.PostAsJsonAsync(wrong, new { email, password });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.False(denied.Headers.Contains("Set-Cookie"));
            var login = await client.PostAsJsonAsync(correct, new { email, password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Assert.Equal(role, (await login.Content.ReadFromJsonAsync<SessionResponse>())!.Role);
            client.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_access=", StringComparison.Ordinal)).Split(';')[0]);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
            if (role == "STAFF")
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/refunds")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/preorders")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/member/profile")).StatusCode);
            }
            await using var disable = data.CreateCommand("UPDATE identity.users SET status='LOCKED' WHERE id=@id");
            disable.Parameters.AddWithValue("id", id);
            await disable.ExecuteNonQueryAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
            client.DefaultRequestHeaders.Remove("Cookie");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(correct, new { email, password })).StatusCode);
        }
        finally
        {
            await using var cleanup = data.CreateCommand("DELETE FROM identity.users WHERE id=@id");
            cleanup.Parameters.AddWithValue("id", id);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
