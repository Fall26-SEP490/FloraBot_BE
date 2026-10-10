using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class LoginRealmTests
{
    [Theory]
    [InlineData("ADMIN", true)]
    [InlineData("OPERATIONS_MANAGER", true)]
    [InlineData("TECHNICIAN", true)]
    [InlineData("CUSTOMER", false)]
    [InlineData("SELLER", false)]
    [InlineData("SELLER_STAFF", false)]
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
              CASE WHEN @role IN ('SELLER', 'SELLER_STAFF') THEN '20000000-0000-0000-0000-000000000001'::uuid ELSE NULL END)
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
            var accessCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_access=", StringComparison.Ordinal)).Split(';')[0];
            var refreshCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_refresh=", StringComparison.Ordinal)).Split(';')[0];
            client.DefaultRequestHeaders.Add("Cookie", accessCookie);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
            if (role is "STAFF" or "OPERATIONS_MANAGER" or "TECHNICIAN")
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/refunds")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/preorders")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/member/profile")).StatusCode);
            }
            if (role == "SELLER_STAFF")
            {
                // Never widen SameSeller or owner/financial/member routes to SELLER_STAFF
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/sellers/20000000-0000-0000-0000-000000000001/wallet")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/sellers/20000000-0000-0000-0000-000000000001/bank")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/sellers/20000000-0000-0000-0000-000000000001/subscriptions")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/refunds")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/member/profile")).StatusCode);
            }

            // Exercise actual refresh token: valid active refresh resolves current role and tenant
            using var refreshClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            refreshClient.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5173");
            refreshClient.DefaultRequestHeaders.Add("Cookie", refreshCookie);
            var refreshed = await refreshClient.PostAsync("/api/auth/refresh", null);
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            var refreshedSession = await refreshed.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.Equal(role, refreshedSession!.Role);
            var expectedSeller = role is "SELLER" or "SELLER_STAFF" ? Guid.Parse("20000000-0000-0000-0000-000000000001") : (Guid?)null;
            Assert.Equal(expectedSeller, refreshedSession.SellerId);

            var newAccessCookie = refreshed.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_access=", StringComparison.Ordinal)).Split(';')[0];
            var newRefreshCookie = refreshed.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_refresh=", StringComparison.Ordinal)).Split(';')[0];

            // Single-use replay of consumed refresh token is rejected
            var replayed = await refreshClient.PostAsync("/api/auth/refresh", null);
            Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);

            // Deactivate account: both access and rotated refresh are rejected
            await using var disable = data.CreateCommand("UPDATE identity.users SET status='LOCKED' WHERE id=@id");
            disable.Parameters.AddWithValue("id", id);
            await disable.ExecuteNonQueryAsync();

            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", newAccessCookie);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

            refreshClient.DefaultRequestHeaders.Remove("Cookie");
            refreshClient.DefaultRequestHeaders.Add("Cookie", newRefreshCookie);
            Assert.Equal(HttpStatusCode.Unauthorized, (await refreshClient.PostAsync("/api/auth/refresh", null)).StatusCode);

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

    [Fact]
    public async Task SellerStaffRequiresAuthoritativeTenantAndRejectsStaleClaimsOrScopeChanges()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var id = Guid.NewGuid();
        var email = $"staff-{id:N}@example.invalid";
        const string password = "Staff-test-password-2026";
        var shopA = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var shopB = Guid.Parse("20000000-0000-0000-0000-000000000002");

        await using var insert = data.CreateCommand("""
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@id,@email,@hash,'Staff test','SELLER_STAFF',@shop)
            """);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("hash", BCrypt.Net.BCrypt.HashPassword(password, 12));
        insert.Parameters.AddWithValue("shop", shopA);
        await insert.ExecuteNonQueryAsync();

        try
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var session = await login.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.Equal("SELLER_STAFF", session!.Role);
            Assert.Equal(shopA, session.SellerId);

            var accessCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_access=", StringComparison.Ordinal)).Split(';')[0];
            var refreshCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_refresh=", StringComparison.Ordinal)).Split(';')[0];
            client.DefaultRequestHeaders.Add("Cookie", accessCookie);

            // Accessing /api/auth/me works
            var me = await client.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);

            // Cross-shop access is denied
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/sellers/{shopB}/products")).StatusCode);

            // Reassign seller to shopB in database -> existing token with shopA claim is revoked
            await using var reassign = data.CreateCommand("UPDATE identity.users SET seller_id=@shopB WHERE id=@id");
            reassign.Parameters.AddWithValue("shopB", shopB);
            reassign.Parameters.AddWithValue("id", id);
            await reassign.ExecuteNonQueryAsync();

            var revoked = await client.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

            // Authoritative refresh reflects CURRENT eligible seller shopB (never old shopA)
            using var refreshClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            refreshClient.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5173");
            refreshClient.DefaultRequestHeaders.Add("Cookie", refreshCookie);
            var refreshed = await refreshClient.PostAsync("/api/auth/refresh", null);
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            var refreshedSession = await refreshed.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.Equal("SELLER_STAFF", refreshedSession!.Role);
            Assert.Equal(shopB, refreshedSession.SellerId);

            var refreshedAccess = refreshed.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_access=", StringComparison.Ordinal)).Split(';')[0];
            var refreshedRefresh = refreshed.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_refresh=", StringComparison.Ordinal)).Split(';')[0];

            // Refreshed access token for shopB still gets denied on owner/finance routes
            refreshClient.DefaultRequestHeaders.Remove("Cookie");
            refreshClient.DefaultRequestHeaders.Add("Cookie", refreshedAccess);
            Assert.Equal(HttpStatusCode.OK, (await refreshClient.GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await refreshClient.GetAsync($"/api/sellers/{shopB}/wallet")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await refreshClient.GetAsync($"/api/sellers/{shopB}/bank")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await refreshClient.GetAsync($"/api/sellers/{shopB}/subscriptions")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await refreshClient.GetAsync("/api/admin/refunds")).StatusCode);

            // Password change in database -> token with old credential version is rejected
            await using var passChange = data.CreateCommand("UPDATE identity.users SET password_hash=@newHash WHERE id=@id");
            passChange.Parameters.AddWithValue("newHash", BCrypt.Net.BCrypt.HashPassword("New-password-12345", 12));
            passChange.Parameters.AddWithValue("id", id);
            await passChange.ExecuteNonQueryAsync();

            var credRevoked = await refreshClient.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.Unauthorized, credRevoked.StatusCode);

            // Outstanding refresh token with old credential version is rejected
            refreshClient.DefaultRequestHeaders.Remove("Cookie");
            refreshClient.DefaultRequestHeaders.Add("Cookie", refreshedRefresh);
            var refreshCredRevoked = await refreshClient.PostAsync("/api/auth/refresh", null);
            Assert.Equal(HttpStatusCode.Unauthorized, refreshCredRevoked.StatusCode);
        }
        finally
        {
            await using var cleanup = data.CreateCommand("DELETE FROM identity.users WHERE id=@id");
            cleanup.Parameters.AddWithValue("id", id);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task SellerStaffWithoutTenantIsEnforcedByDatabaseConstraint()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var id = Guid.NewGuid();
        var email = $"notenant-{id:N}@example.invalid";

        await using var insert = data.CreateCommand("""
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@id,@email,'!unprovisioned','No tenant staff','SELLER_STAFF',NULL)
            """);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("email", email);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task MigrationPreservesLegacyStaffRows()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var query = data.CreateCommand("SELECT count(*) FROM identity.users WHERE role='STAFF' AND seller_id IS NULL");
        var staffCount = (long)(await query.ExecuteScalarAsync())!;
        Assert.True(staffCount > 0, "Legacy STAFF rows must exist and have NULL seller_id");

        // Verify that legacy STAFF row cannot have non-null seller_id under users_seller_check constraint
        var legacyId = Guid.NewGuid();
        await using var insert = data.CreateCommand("""
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@id,@email,'!unprovisioned','Legacy staff test','STAFF','20000000-0000-0000-0000-000000000001'::uuid)
            """);
        insert.Parameters.AddWithValue("id", legacyId);
        insert.Parameters.AddWithValue("email", $"legacystaff-{legacyId:N}@example.invalid");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task LegacyStaffCannotLoginOrRefreshAndExistingTokensAreRevoked()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();

        var id = Guid.NewGuid();
        var email = $"retired-staff-{id:N}@example.invalid";
        const string password = "Staff-legacy-password-2026";
        await using var insert = data.CreateCommand("""
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@id,@email,@hash,'Retired staff','STAFF',NULL)
            """);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("hash", BCrypt.Net.BCrypt.HashPassword(password, 12));
        await insert.ExecuteNonQueryAsync();

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // Admin realm login rejects legacy STAFF
        var adminLogin = await client.PostAsJsonAsync("/api/auth/admin/login", new { email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, adminLogin.StatusCode);
        Assert.False(adminLogin.Headers.Contains("Set-Cookie"));

        // Ordinary portal login rejects legacy STAFF
        var portalLogin = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, portalLogin.StatusCode);
        Assert.False(portalLogin.Headers.Contains("Set-Cookie"));

        // Refresh token consumption rejects legacy STAFF
        var userEntity = new User { Id = id, Role = "STAFF", FullName = "Retired staff", PasswordHash = "!unprovisioned" };
        var refreshToken = await tokens.CreateRefresh(userEntity);
        using var refreshClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        refreshClient.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5173");
        refreshClient.DefaultRequestHeaders.Add("Cookie", $"florabot_refresh={refreshToken}");
        var refreshRes = await refreshClient.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshRes.StatusCode);

        // Old access token with role STAFF is revoked at OnTokenValidated or rejected by policy
        var oldAccessToken = factory.Token("STAFF", userId: id);
        var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meReq.Headers.Authorization = new("Bearer", oldAccessToken);
        var meRes = await client.SendAsync(meReq);
        Assert.True(meRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        // Accessing tasks route with old STAFF token is revoked
        var tasksReq = new HttpRequestMessage(HttpMethod.Get, "/api/staff/tasks");
        tasksReq.Headers.Authorization = new("Bearer", oldAccessToken);
        var tasksRes = await client.SendAsync(tasksReq);
        Assert.True(tasksRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }
}
