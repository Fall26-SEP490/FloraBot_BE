using System.Text.Json;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class Esp32ConfigurationDatabaseTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Synthetic-esp32-account-password";
    private const string ReplacementPassword = "Synthetic-replacement-password";

    [Fact]
    public async Task ManagerScopeUsesCurrentAssignmentAndRevokesPreviousManagerAfterReassignment()
    {
        await using var f = await Seed();
        var own = await Resolve(f, f.Kiosk, f.Manager, "OPERATIONS_MANAGER");
        Assert.True(own.ActorActive);
        Assert.Equal(f.Kiosk, own.Kiosk!.Id);
        Assert.Equal("ESP32-" + f.Kiosk.ToString("N"), own.Kiosk.HardwareId);
        Assert.Equal(f.Kiosk, own.AuditKioskId);

        var foreign = await Resolve(f, f.OtherKiosk, f.Manager, "OPERATIONS_MANAGER");
        Assert.Null(foreign.Kiosk);
        Assert.Equal(f.OtherKiosk, foreign.AuditKioskId);
        Assert.Null((await Resolve(f, f.Kiosk, f.OtherManager, "OPERATIONS_MANAGER")).Kiosk);
        Assert.Null((await Resolve(f, Guid.NewGuid(), f.Manager, "OPERATIONS_MANAGER")).Kiosk);

        await f.Execute("""
            DELETE FROM kiosk_ops.manager_kiosks WHERE kiosk_id=@kiosk;
            INSERT INTO kiosk_ops.manager_kiosks(manager_id,kiosk_id) VALUES(@otherManager,@kiosk);
            """);
        Assert.Null((await Resolve(f, f.Kiosk, f.Manager, "OPERATIONS_MANAGER")).Kiosk);
        Assert.Equal(f.Kiosk, (await Resolve(f, f.Kiosk, f.OtherManager, "OPERATIONS_MANAGER")).Kiosk!.Id);
    }

    [Fact]
    public async Task TechnicianScopeRequiresAssignedLiveInstallationAndEndsAtHandover()
    {
        await using var f = await Seed();
        foreach (var status in new[] { "SCHEDULED", "INSTALLING", "AWAITING_HANDOVER" })
        {
            await f.Execute("UPDATE kiosk_ops.kiosk_applications SET status=@status WHERE id=@installation", status);
            var own = await Resolve(f, f.Installation, f.Technician, "TECHNICIAN");
            Assert.True(own.ActorActive);
            Assert.Equal(f.Kiosk, own.Kiosk!.Id);
            Assert.Equal(f.Kiosk, own.AuditKioskId);
            var foreign = await Resolve(f, f.Installation, f.OtherTechnician, "TECHNICIAN");
            Assert.Null(foreign.Kiosk);
            Assert.Equal(f.Kiosk, foreign.AuditKioskId);
        }

        // Assignment changes must take effect on the next request even while the installation stays live.
        await f.Execute("UPDATE kiosk_ops.kiosk_applications SET assigned_technician_id=@otherTechnician WHERE id=@installation");
        Assert.Null((await Resolve(f, f.Installation, f.Technician, "TECHNICIAN")).Kiosk);
        Assert.Equal(f.Kiosk, (await Resolve(f, f.Installation, f.OtherTechnician, "TECHNICIAN")).Kiosk!.Id);

        await f.Execute("UPDATE kiosk_ops.kiosk_applications SET status='ACTIVE' WHERE id=@installation");
        Assert.Null((await Resolve(f, f.Installation, f.OtherTechnician, "TECHNICIAN")).Kiosk);
        var missing = await Resolve(f, Guid.NewGuid(), f.Technician, "TECHNICIAN");
        Assert.Null(missing.Kiosk);
        Assert.Null(missing.AuditKioskId);
        // The Technician route accepts installation IDs, not arbitrary kiosk IDs.
        Assert.Null((await Resolve(f, f.Kiosk, f.Technician, "TECHNICIAN")).Kiosk);
    }

    [Fact]
    public async Task PasswordReauthenticationChecksCurrentHashRoleAndActiveStatusAndAuditContainsOnlyIdentifiers()
    {
        await using var f = await Seed();
        var sessions = new Esp32ConfigurationSessions(f.Data);
        await using (var session = await sessions.OpenAsync(CancellationToken.None))
        {
            Assert.NotNull((await session.ResolveAsync(f.Kiosk, f.Manager, "OPERATIONS_MANAGER", CancellationToken.None)).Kiosk);
            Assert.True(await session.ReauthenticateAsync(f.Manager, "OPERATIONS_MANAGER", Password, CancellationToken.None));
            Assert.False(await session.ReauthenticateAsync(f.Manager, "OPERATIONS_MANAGER", "wrong-password", CancellationToken.None));
            Assert.False(await session.ReauthenticateAsync(f.Manager, "OPERATIONS_MANAGER", null, CancellationToken.None));
            Assert.False(await session.ReauthenticateAsync(f.Manager, "TECHNICIAN", Password, CancellationToken.None));
            await session.AuditAndCommitAsync(f.Manager, f.Kiosk, null, true, "SUCCESS", CancellationToken.None);
        }

        await using (var verify = f.Data.CreateCommand("""
            SELECT action,entity_type,entity_id,payload::text
            FROM notify.audit_logs WHERE actor_id=@actor AND entity_id=@kiosk
            """))
        {
            verify.Parameters.AddWithValue("actor", f.Manager);
            verify.Parameters.AddWithValue("kiosk", f.Kiosk);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("ESP32_CONFIG_DOWNLOADED", reader.GetString(0));
            Assert.Equal("kiosks", reader.GetString(1));
            Assert.Equal(f.Kiosk, reader.GetGuid(2));
            var payload = reader.GetString(3);
            using var json = JsonDocument.Parse(payload);
            Assert.Equal(new[] { "installationId", "result" }, json.RootElement.EnumerateObject().Select(x => x.Name).Order());
            Assert.Equal("SUCCESS", json.RootElement.GetProperty("result").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("installationId").ValueKind);
            Assert.DoesNotContain(Password, payload);
            Assert.DoesNotContain(f.PasswordHash, payload);
            Assert.False(await reader.ReadAsync());
        }

        await f.Execute("UPDATE identity.users SET password_hash=@replacementHash WHERE id=@manager");
        await using (var session = await sessions.OpenAsync(CancellationToken.None))
        {
            Assert.False(await session.ReauthenticateAsync(f.Manager, "OPERATIONS_MANAGER", Password, CancellationToken.None));
            Assert.True(await session.ReauthenticateAsync(f.Manager, "OPERATIONS_MANAGER", ReplacementPassword, CancellationToken.None));
        }
        await f.Execute("UPDATE identity.users SET status='DISABLED' WHERE id=@manager");
        await using (var session = await sessions.OpenAsync(CancellationToken.None))
        {
            var revoked = await session.ResolveAsync(f.Kiosk, f.Manager, "OPERATIONS_MANAGER", CancellationToken.None);
            Assert.False(revoked.ActorActive);
            Assert.Null(revoked.Kiosk);
            Assert.False(await session.ReauthenticateAsync(f.Manager, "OPERATIONS_MANAGER", ReplacementPassword, CancellationToken.None));
        }
    }

    private static async Task<Esp32KioskAccess> Resolve(Fixture f, Guid resource, Guid actor, string role)
    {
        await using var session = await new Esp32ConfigurationSessions(f.Data).OpenAsync(CancellationToken.None);
        return await session.ResolveAsync(resource, actor, role, CancellationToken.None);
    }

    private async Task<Fixture> Seed()
    {
        using var scope = factory.Services.CreateScope();
        var f = new Fixture(scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>());
        await f.Execute("""
            INSERT INTO identity.users(id,email,password_hash,full_name,role,status)
            VALUES(@manager,@manager::text||'@example.invalid',@hash,'ESP32 manager','OPERATIONS_MANAGER','ACTIVE'),
                  (@otherManager,@otherManager::text||'@example.invalid',@hash,'Other ESP32 manager','OPERATIONS_MANAGER','ACTIVE'),
                  (@technician,@technician::text||'@example.invalid',@hash,'ESP32 technician','TECHNICIAN','ACTIVE'),
                  (@otherTechnician,@otherTechnician::text||'@example.invalid',@hash,'Other ESP32 technician','TECHNICIAN','ACTIVE'),
                  (@customer,@customer::text||'@example.invalid','!test-only','ESP32 installation customer','CUSTOMER','ACTIVE');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'ESP32 scope kiosk','Test address','HCM','ESP32-'||replace(@kiosk::text,'-',''),'esp32-ESP32-'||replace(@kiosk::text,'-',''),'OFFLINE'),
                  (@otherKiosk,@otherKiosk::text,'Foreign ESP32 kiosk','Test address','HCM','ESP32-'||replace(@otherKiosk::text,'-',''),'esp32-ESP32-'||replace(@otherKiosk::text,'-',''),'OFFLINE');
            INSERT INTO kiosk_ops.manager_kiosks(manager_id,kiosk_id)
            VALUES(@manager,@kiosk),(@otherManager,@otherKiosk);
            INSERT INTO kiosk_ops.kiosk_applications(
                id,customer_id,package_id,shop_name,phone,contact_email,installation_address,
                latitude,longitude,bank_name,bank_holder,bank_account_no_enc,status,
                assigned_technician_id,assigned_kiosk_id,scheduled_date,time_window)
            VALUES(@installation,@customer,'20000000-0000-0000-0000-00000000000c','ESP32 scope shop',
                '0901234567',@customer::text||'@example.invalid','Test address',10.8,106.7,
                'TEST','TEST','test-only-placeholder','SCHEDULED',@technician,@kiosk,CURRENT_DATE,'09:00 - 12:00');
            """);
        return f;
    }

    private sealed class Fixture(NpgsqlDataSource data) : IAsyncDisposable
    {
        public NpgsqlDataSource Data { get; } = data;
        public Guid Manager { get; } = Guid.NewGuid();
        public Guid OtherManager { get; } = Guid.NewGuid();
        public Guid Technician { get; } = Guid.NewGuid();
        public Guid OtherTechnician { get; } = Guid.NewGuid();
        public Guid Customer { get; } = Guid.NewGuid();
        public Guid Kiosk { get; } = Guid.NewGuid();
        public Guid OtherKiosk { get; } = Guid.NewGuid();
        public Guid Installation { get; } = Guid.NewGuid();
        public string PasswordHash { get; } = BCrypt.Net.BCrypt.HashPassword(Password, 4);
        private string ReplacementHash { get; } = BCrypt.Net.BCrypt.HashPassword(ReplacementPassword, 4);

        public async Task Execute(string sql, string? status = null)
        {
            await using var command = Data.CreateCommand(sql);
            foreach (var (name, id) in new[] { ("manager", Manager), ("otherManager", OtherManager),
                ("technician", Technician), ("otherTechnician", OtherTechnician), ("customer", Customer),
                ("kiosk", Kiosk), ("otherKiosk", OtherKiosk), ("installation", Installation) })
                command.Parameters.AddWithValue(name, id);
            command.Parameters.AddWithValue("hash", PasswordHash);
            command.Parameters.AddWithValue("replacementHash", ReplacementHash);
            if (status is not null) command.Parameters.AddWithValue("status", status);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            // Audit rows retain logical identifiers and are append-only; fixture entities have no leases/payments.
            await Execute("""
                DELETE FROM kiosk_ops.kiosk_applications WHERE id=@installation;
                DELETE FROM kiosk_ops.manager_kiosks WHERE kiosk_id IN (@kiosk,@otherKiosk);
                DELETE FROM kiosk_ops.kiosks WHERE id IN (@kiosk,@otherKiosk);
                DELETE FROM identity.users WHERE id IN (@manager,@otherManager,@technician,@otherTechnician,@customer);
                """);
        }
    }
}
