using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class ProvisioningTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("ADMIN")]
    [InlineData("SELLER")]
    [InlineData("OPERATIONS_MANAGER")]
    [InlineData("TECHNICIAN")]
    [InlineData("SELLER_STAFF")]
    public async Task ProvisioningEnablesLoginOnceAndPreservesMembership(string role)
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var id = Guid.NewGuid(); var email = $"provision-{id:N}@example.invalid";
        var password = Guid.NewGuid().ToString("N");
        await using var insert = data.CreateCommand("""
            INSERT INTO identity.users(id,email,phone,password_hash,full_name,role,seller_id)
            VALUES(@id,NULL,@phone,'!unprovisioned','Provision test',@role,
              CASE WHEN @role IN ('SELLER', 'SELLER_STAFF') THEN '20000000-0000-0000-0000-000000000001'::uuid ELSE NULL END)
            """);
        insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("phone", id.ToString()); insert.Parameters.AddWithValue("role", role);
        await insert.ExecuteNonQueryAsync();
        try
        {
            using var output = new StringWriter();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var args = new[] { "provision-user", id.ToString(), email.ToUpperInvariant() };
            Assert.Equal(0, await PortalProvisioning.RunAsync(args, config, new StringReader(password), output));
            Assert.DoesNotContain(password, output.ToString());
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5173");
            var isPlatform = role is "ADMIN" or "OPERATIONS_MANAGER" or "TECHNICIAN";
            var login = await client.PostAsJsonAsync(isPlatform ? "/api/auth/admin/login" : "/api/auth/login", new { email, password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var session = await login.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.Equal(id, session!.Id); Assert.Equal(role, session.Role);
            var expectedSeller = role is "SELLER" or "SELLER_STAFF" ? Guid.Parse("20000000-0000-0000-0000-000000000001") : (Guid?)null;
            Assert.Equal(expectedSeller, session.SellerId);
            Assert.Equal(1, await PortalProvisioning.RunAsync(args, config, new StringReader("another-strong-password"), output));
            await using var nullOverride = data.CreateCommand("SELECT flow.provision_portal_user(@id,@email,(SELECT password_hash FROM identity.users WHERE id=@id),NULL)");
            nullOverride.Parameters.AddWithValue("id", id); nullOverride.Parameters.AddWithValue("email", email);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => nullOverride.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, denied.SqlState);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(isPlatform ? "/api/auth/admin/login" : "/api/auth/login", new { email, password })).StatusCode);
            await using var audit = data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE entity_id=@id AND action='PORTAL_USER_PROVISIONED' AND actor_type='SYSTEM' AND NOT(payload ? 'password') AND NOT(payload ? 'email')");
            audit.Parameters.AddWithValue("id", id);
            Assert.Equal(1L, await audit.ExecuteScalarAsync());
        }
        finally
        {
            await using var cleanup = data.CreateCommand("DELETE FROM identity.users WHERE id=@id");
            cleanup.Parameters.AddWithValue("id", id); await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("short")]
    [InlineData("ááááááááááááááááááááááááááááááááááááá")]
    public async Task InvalidPasswordIsRejectedBeforeDatabaseAccess(string password)
    {
        using var output = new StringWriter();
        Assert.Equal(2, await PortalProvisioning.RunAsync(["provision-user", Guid.NewGuid().ToString(), "test@example.invalid"], new ConfigurationBuilder().Build(), new StringReader(password), output));
        Assert.DoesNotContain(password, output.ToString());
    }

    [Theory]
    [InlineData("CUSTOMER", "ACTIVE", "!unprovisioned", "Testing", 1)]
    [InlineData("ADMIN", "LOCKED", "!unprovisioned", "Testing", 1)]
    [InlineData("ADMIN", "ACTIVE", "$2b$12$demoadmin", "Production", 1)]
    [InlineData("ADMIN", "ACTIVE", "$2b$12$demoadmin", "Development", 0)]
    [InlineData("OPERATIONS_MANAGER", "ACTIVE", "$2b$12$demoops", "Development", 0)]
    [InlineData("TECHNICIAN", "ACTIVE", "$2b$12$demotech", "Development", 0)]
    public async Task RoleStatusAndEnvironmentControlProvisioning(string role, string status, string hash, string environment, int expected)
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var id = Guid.NewGuid(); var email = $"denied-{id:N}@example.invalid";
        await using var setup = data.CreateCommand("INSERT INTO identity.users(id,email,password_hash,full_name,role,status) VALUES(@id,@email,@hash,'Denied provision',@role,@status)");
        setup.Parameters.AddWithValue("id", id); setup.Parameters.AddWithValue("email", email); setup.Parameters.AddWithValue("hash", hash); setup.Parameters.AddWithValue("role", role); setup.Parameters.AddWithValue("status", status);
        await setup.ExecuteNonQueryAsync();
        try
        {
            var config = new ConfigurationBuilder().AddConfiguration(scope.ServiceProvider.GetRequiredService<IConfiguration>()).AddInMemoryCollection(new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = environment }).Build();
            using var output = new StringWriter();
            Assert.Equal(expected, await PortalProvisioning.RunAsync(["provision-user", id.ToString(), email], config, new StringReader("strong-test-password"), output));
            await using var check = data.CreateCommand("SELECT password_hash FROM identity.users WHERE id=@id");
            check.Parameters.AddWithValue("id", id);
            var stored = (string)(await check.ExecuteScalarAsync())!;
            if (expected == 0) Assert.True(BCrypt.Net.BCrypt.Verify("strong-test-password", stored));
            else Assert.Equal(hash, stored);
        }
        finally
        {
            await using var cleanup = data.CreateCommand("DELETE FROM identity.users WHERE id=@id");
            cleanup.Parameters.AddWithValue("id", id); await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task EmailCollisionAndMismatchedEmailDoNotReplaceCredentials()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var email = $"reserved-{first:N}@example.invalid";
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.users(id,email,password_hash,full_name,role)
            VALUES(@first,upper(@email),'!unprovisioned','Reserved','ADMIN'),
                  (@second,@second::text || '@example.invalid','!unprovisioned','Other','ADMIN');
            """);
        setup.Parameters.AddWithValue("first", first); setup.Parameters.AddWithValue("second", second); setup.Parameters.AddWithValue("email", email);
        await setup.ExecuteNonQueryAsync();
        try
        {
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            using var output = new StringWriter();
            Assert.Equal(1, await PortalProvisioning.RunAsync(["provision-user", first.ToString(), "mismatch@example.invalid"], config, new StringReader("strong-test-password"), output));
            await using var clearEmail = data.CreateCommand("UPDATE identity.users SET email=NULL,phone=@phone WHERE id=@id");
            clearEmail.Parameters.AddWithValue("phone", second.ToString()); clearEmail.Parameters.AddWithValue("id", second); await clearEmail.ExecuteNonQueryAsync();
            Assert.Equal(1, await PortalProvisioning.RunAsync(["provision-user", second.ToString(), email], config, new StringReader("strong-test-password"), output));
            await using var check = data.CreateCommand("SELECT count(*) FROM identity.users WHERE id=ANY(@ids) AND password_hash='!unprovisioned'");
            check.Parameters.AddWithValue("ids", new[] { first, second }); Assert.Equal(2L, await check.ExecuteScalarAsync());
        }
        finally
        {
            await using var cleanup = data.CreateCommand("DELETE FROM identity.users WHERE id=ANY(@ids)");
            cleanup.Parameters.AddWithValue("ids", new[] { first, second }); await cleanup.ExecuteNonQueryAsync();
        }
    }
}
