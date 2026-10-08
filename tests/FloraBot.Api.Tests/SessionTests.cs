using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class SessionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task PortalCookiesRefreshOnceAndRejectForeignOrigin()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var id = Guid.NewGuid();
        var email = $"session-{id:N}@example.invalid";
        var password = Guid.NewGuid().ToString("N");
        await using var insert = data.CreateCommand("INSERT INTO identity.users(id,email,password_hash,full_name,role,status) VALUES(@id,@email,@hash,'Session test','ADMIN','ACTIVE')");
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("hash", BCrypt.Net.BCrypt.HashPassword(password));
        await insert.ExecuteNonQueryAsync();
        try
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email, password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var cookies = login.Headers.GetValues("Set-Cookie").ToArray();
            Assert.All(cookies, cookie => { Assert.Contains("httponly", cookie.ToLowerInvariant()); Assert.Contains("samesite=strict", cookie.ToLowerInvariant()); });
            var refreshCookie = cookies.Single(c => c.StartsWith("florabot_refresh=", StringComparison.Ordinal)).Split(';')[0];
            using var rejected = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
            rejected.Headers.Add("Cookie", refreshCookie);
            rejected.Headers.Add("Origin", "https://untrusted.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(rejected)).StatusCode);

            async Task<HttpResponseMessage> Refresh()
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
                message.Headers.Add("Cookie", refreshCookie);
                message.Headers.Add("Origin", "http://127.0.0.1:5173");
                return await client.SendAsync(message);
            }
            var rotated = await Refresh();
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
            Assert.NotEqual(refreshCookie, rotated.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_refresh=", StringComparison.Ordinal)).Split(';')[0]);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh()).StatusCode);
        }
        finally
        {
            await using var cleanup = data.CreateCommand("DELETE FROM identity.users WHERE id=@id");
            cleanup.Parameters.AddWithValue("id", id);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
