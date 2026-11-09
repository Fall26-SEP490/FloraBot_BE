using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FloraBot.Api.Tests;

[Collection("Esp32 provisioning")]
public sealed class Esp32AutoProvisioningApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task EnrollmentQueuesAtomicallyAndScopedDownloadWaitsForBrokerAndLiveKey()
    {
        await using var f = await Seed();
        using var client = f.Client(f.Manager);
        using var enrolled = await client.PostAsJsonAsync("/api/operations/kiosks/enroll", f.Request());
        Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);
        var body = await enrolled.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<EnrollKioskResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        f.Kiosk = created.Id;
        Assert.Equal("OFFLINE", created.Status);
        Assert.Equal("PENDING", created.Provisioning!.State);
        Assert.DoesNotContain("hmac", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await f.Scalar("SELECT count(*) FROM kiosk_ops.manager_kiosks WHERE kiosk_id=@kiosk AND manager_id=@manager"));
        Assert.Equal(1L, await f.Scalar("SELECT count(*) FROM kiosk_ops.esp32_provisioning_jobs WHERE kiosk_id=@kiosk"));
        Assert.Equal("esp32-" + f.Hardware, await f.Scalar("SELECT mqtt_client_id FROM kiosk_ops.kiosks WHERE id=@kiosk"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/operations/kiosks/enroll", f.Request())).StatusCode);

        var url = $"/api/operations/kiosks/{f.Kiosk}/esp32-config";
        var metadata = await client.GetFromJsonAsync<Esp32ConfigMetadata>(url);
        Assert.False(metadata!.CanDownload);
        Assert.Equal("PENDING", metadata.Provisioning!.State);
        using var pending = await client.PostAsJsonAsync(url + "/download", new { password = Fixture.Password });
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        Assert.Contains("ESP32_CONFIG_NOT_READY", await pending.Content.ReadAsStringAsync());
        Assert.True(await f.Worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal(1, f.Broker.EnsureCalls);

        metadata = await client.GetFromJsonAsync<Esp32ConfigMetadata>(url);
        Assert.True(metadata!.CanDownload);
        Assert.Equal("READY", metadata.Provisioning!.State);
        using var wrong = await client.PostAsJsonAsync(url + "/download", new { password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var download = await client.PostAsJsonAsync(url + "/download", new { password = Fixture.Password });
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("no-store", download.Headers.CacheControl!.ToString());
        Assert.Contains("config.local.h", download.Content.Headers.ContentDisposition!.ToString());
        var header = await download.Content.ReadAsStringAsync();
        Assert.Contains("wifiSsid[] = \"\";", header);
        Assert.Contains("mqttPassword[] = \"" + f.Broker.Passwords.Single() + "\";", header);
        Assert.Equal("OFFLINE", await f.Scalar("SELECT status FROM kiosk_ops.kiosks WHERE id=@kiosk"));
        Assert.Null(await f.Scalar("SELECT last_heartbeat_at FROM kiosk_ops.kiosks WHERE id=@kiosk"));

        using var foreign = f.Client(f.OtherManager);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsync(url + "/provision", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(url + "/provision", null)).StatusCode);
        Assert.False(await f.Worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal(1, f.Broker.EnsureCalls);
    }

    [Fact]
    public async Task ConcurrentRetryPreservesCredentialsAndDeleteRequiresOwnedBrokerRevocation()
    {
        await using var f = await Seed();
        using var client = f.Client(f.Manager);
        var created = await (await client.PostAsJsonAsync("/api/operations/kiosks/enroll", f.Request())).Content.ReadFromJsonAsync<EnrollKioskResponse>();
        f.Kiosk = created!.Id;
        var url = $"/api/operations/kiosks/{f.Kiosk}/esp32-config";
        f.Broker.FailEnsure = true;
        Assert.True(await f.Worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal("FAILED", (await client.GetFromJsonAsync<Esp32ConfigMetadata>(url))!.Provisioning!.State);
        var retries = await Task.WhenAll(client.PostAsync(url + "/provision", null), client.PostAsync(url + "/provision", null))
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.All(retries, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        f.Broker.FailEnsure = false;
        Assert.True(await f.Worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, f.Broker.Passwords.Count);
        Assert.Equal(f.Broker.Passwords[0], f.Broker.Passwords[1]);
        // Preserve the existing deletion rule: an Admin must first remove the Manager
        // assignment. Auto-assignment at enrollment must not bypass that old guard.
        await f.Scalar("DELETE FROM kiosk_ops.manager_kiosks WHERE kiosk_id=@kiosk");
        f.Broker.FailDisable = true;
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/operations/kiosks/{f.Kiosk}")).StatusCode);
        Assert.Equal(1L, await f.Scalar("SELECT count(*) FROM kiosk_ops.kiosks WHERE id=@kiosk"));
        f.Broker.FailDisable = false;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/operations/kiosks/{f.Kiosk}")).StatusCode);
        Assert.Equal(0L, await f.Scalar("SELECT count(*) FROM kiosk_ops.esp32_provisioning_jobs WHERE kiosk_id=@kiosk"));
        Assert.DoesNotContain(f.Hardware, f.App.Services.GetRequiredService<DeviceKeyRing>().HardwareIds);
    }

    [Fact]
    public async Task DeleteCannotRaceAnInFlightBrokerProvisioningAttempt()
    {
        await using var f = await Seed();
        using var client = f.Client(f.Manager);
        var created = await (await client.PostAsJsonAsync("/api/operations/kiosks/enroll", f.Request())).Content.ReadFromJsonAsync<EnrollKioskResponse>();
        f.Kiosk = created!.Id;
        await f.Scalar("""
            DELETE FROM kiosk_ops.manager_kiosks WHERE kiosk_id=@kiosk;
            UPDATE kiosk_ops.esp32_provisioning_jobs SET state='PROVISIONING',attempts=1,
              lease_token=public.uuid_v7(),lease_expires_at=CURRENT_TIMESTAMP+interval '120 seconds'
            WHERE kiosk_id=@kiosk;
            """);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/operations/kiosks/{f.Kiosk}")).StatusCode);
        Assert.Equal(0, f.Broker.DisableCalls);
        Assert.Equal("PROVISIONING", await f.Scalar("SELECT state FROM kiosk_ops.esp32_provisioning_jobs WHERE kiosk_id=@kiosk"));
    }

    [Theory]
    [InlineData("bad/hardware", null)]
    [InlineData("ESP32-VALID", "wrong-client")]
    public async Task InvalidDeviceIdentityDoesNotCreateAKioskOrJob(string hardware, string? clientId)
    {
        await using var f = await Seed();
        using var client = f.Client(f.Manager);
        var response = await client.PostAsJsonAsync("/api/operations/kiosks/enroll", f.Request() with { HardwareId = hardware, MqttClientId = clientId });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await f.Scalar("SELECT count(*) FROM kiosk_ops.kiosks WHERE requested_application_id=@application"));
    }

    private async Task<Fixture> Seed()
    {
        var f = new Fixture(factory);
        await f.Seed();
        return f;
    }

    private sealed class Broker : IMqttDeviceProvisioner
    {
        public bool FailEnsure { get; set; }
        public bool FailDisable { get; set; }
        public int EnsureCalls { get; private set; }
        public int DisableCalls { get; private set; }
        public List<string> Passwords { get; } = [];
        public Task EnsureAsync(Guid kiosk, string hardware, string password, CancellationToken ct)
        {
            EnsureCalls++;
            Passwords.Add(password);
            if (FailEnsure) throw new Esp32ProvisioningException("ESP32_MQTT_TEST_CONFLICT", false);
            return Task.CompletedTask;
        }
        public Task DisableAsync(Guid kiosk, string hardware, CancellationToken ct)
        {
            DisableCalls++;
            if (FailDisable) throw new Esp32ProvisioningException("ESP32_MQTT_TEST_UNAVAILABLE");
            return Task.CompletedTask;
        }
    }

    private sealed class PublicBrokerSource(IServiceProvider services, IConfiguration config) : IEsp32ConfigurationSource
    {
        public Task<Esp32BrokerConfiguration?> ReadBrokerAsync(CancellationToken ct) =>
            Task.FromResult<Esp32BrokerConfiguration?>(new("mqtt.example.invalid", 8883, "synthetic-public-ca"));
        public Task<Esp32SecretResult> ReadSecretsAsync(string hardware, CancellationToken ct) =>
            new Esp32ConfigurationSource(config, services).ReadSecretsAsync(hardware, ct);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Password = "Synthetic-manager-password";
        public Guid Manager { get; } = Guid.NewGuid();
        public Guid OtherManager { get; } = Guid.NewGuid();
        private Guid Customer { get; } = Guid.NewGuid();
        private Guid Application { get; } = Guid.NewGuid();
        private Guid Payment { get; } = Guid.NewGuid();
        public Guid Kiosk { get; set; }
        public string Hardware { get; } = "ESP32-AUTO-" + Guid.NewGuid().ToString("N");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "florabot-auto-api-" + Guid.NewGuid().ToString("N"));
        public WebApplicationFactory<Program> App { get; }
        private readonly ApiFactory parent;
        public Broker Broker { get; } = new();
        private NpgsqlDataSource Data => App.Services.GetRequiredService<NpgsqlDataSource>();
        public Esp32ProvisioningWorker Worker => new(App.Services.GetRequiredService<Esp32ProvisioningStore>(), Broker,
            App.Services.GetRequiredService<DeviceKeyRing>(), App.Services.GetRequiredService<IConfiguration>(), NullLogger<Esp32ProvisioningWorker>.Instance);

        public Fixture(ApiFactory factory)
        {
            parent = factory;
            Directory.CreateDirectory(directory);
            var credentials = Path.Combine(directory, "backend.json");
            File.WriteAllText(credentials, "{\"florabot-backend\":{\"password\":\"synthetic-backend-only\"}}");
            App = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("MQTT_ENABLED", "true");
                builder.UseSetting("MQTT_AUTO_PROVISIONING_ENABLED", "true");
                builder.UseSetting("MQTT_CREDENTIALS_FILE", credentials);
                builder.UseSetting("DATA_PROTECTION_KEYS_PATH", Path.Combine(directory, "keys"));
                builder.ConfigureServices(services =>
                {
                    foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)
                        && d.ImplementationType is { } type && (type == typeof(MqttEventWorker) || type == typeof(Esp32ProvisioningWorker))).ToList())
                        services.Remove(hosted);
                    services.AddSingleton<IMqttDeviceProvisioner>(Broker);
                    services.AddSingleton<IEsp32ConfigurationSource>(sp => new PublicBrokerSource(sp, sp.GetRequiredService<IConfiguration>()));
                });
            });
        }
        public EnrollKioskRequest Request() => new("K-AUTO-" + Application.ToString("N"), "Auto kiosk", Hardware, null, "HCM", 2, Application);
        public HttpClient Client(Guid actor)
        {
            var client = App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            client.DefaultRequestHeaders.Authorization = new("Bearer", parent.Token("OPERATIONS_MANAGER", userId: actor));
            return client;
        }
        public async Task Seed() => await Scalar("""
            INSERT INTO identity.users(id,email,password_hash,full_name,role,status)
            VALUES(@manager,@manager::text||'@example.invalid',@hash,'Auto manager','OPERATIONS_MANAGER','ACTIVE'),
              (@otherManager,@otherManager::text||'@example.invalid',@hash,'Other auto manager','OPERATIONS_MANAGER','ACTIVE'),
              (@customer,@customer::text||'@example.invalid','!test','Auto customer','CUSTOMER','ACTIVE');
            INSERT INTO payment.payments(id,kind,purpose,amount,status,gateway,gateway_txn_id,idempotency_key,paid_at)
            VALUES(@payment,'CHARGE','KIOSK_APPLICATION',20000,'SUCCEEDED','PAYOS',@payment::text,@payment::text,public.app_now());
            INSERT INTO kiosk_ops.kiosk_applications(id,customer_id,package_id,shop_name,phone,contact_email,
              installation_address,latitude,longitude,bank_name,bank_holder,bank_account_no_enc,status,
              walk_in_slots,preorder_slots,total_slots,annual_fee,payment_id)
            VALUES(@application,@customer,'20000000-0000-0000-0000-00000000000c','Auto shop','0901234567',
              @customer::text||'@example.invalid','Test address',10.8,106.7,'TEST','TEST','synthetic','CONFIRMED',1,1,2,20000,@payment);
            """);
        public async Task<object?> Scalar(string sql)
        {
            await using var command = Data.CreateCommand(sql);
            foreach (var (name, value) in new[] { ("manager", Manager), ("otherManager", OtherManager), ("customer", Customer),
                ("application", Application), ("payment", Payment), ("kiosk", Kiosk) }) command.Parameters.AddWithValue(name, value);
            command.Parameters.AddWithValue("hash", BCrypt.Net.BCrypt.HashPassword(Password, 4));
            var valueResult = await command.ExecuteScalarAsync();
            return valueResult is DBNull ? null : valueResult;
        }
        public async ValueTask DisposeAsync()
        {
            // Preserve immutable payment/audit history. Only remove this test's pending job to keep
            // explicitly invoked workers from claiming a previous test's work in the shared test DB.
            if (Kiosk != Guid.Empty) await Scalar("DELETE FROM kiosk_ops.esp32_provisioning_jobs WHERE kiosk_id=@kiosk");
            await App.DisposeAsync();
        }
    }
}
