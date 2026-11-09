using System.Security.Cryptography;
using System.Text;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FloraBot.Api.Tests;

[CollectionDefinition("Esp32 provisioning", DisableParallelization = true)]
public sealed class Esp32ProvisioningCollection;

[Collection("Esp32 provisioning")]
public sealed class Esp32ProvisioningWorkerTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Guid Actor = Guid.Parse(CurrentMainKioskFixture.AdminUserId);
    private NpgsqlDataSource Data => factory.Services.GetRequiredService<NpgsqlDataSource>();
    private IDataProtectionProvider Protection => factory.Services.GetRequiredService<IDataProtectionProvider>();
    private static readonly IConfiguration Configuration = new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["MQTT_AUTO_PROVISIONING_ENABLED"] = "true" }).Build();

    private sealed class FakeProvisioner : IMqttDeviceProvisioner
    {
        private readonly List<string> passwords = [];
        internal int FailuresRemaining { get; set; }
        internal Func<CancellationToken, Task>? DuringEnsure { get; set; }
        internal int Calls { get { lock (passwords) return passwords.Count; } }
        internal bool SamePasswordsAs(FakeProvisioner other)
        {
            string[] first;
            string[] second;
            lock (passwords) first = passwords.ToArray();
            lock (other.passwords) second = other.passwords.ToArray();
            return first.Concat(second).All(value => CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(value), Encoding.ASCII.GetBytes(first.FirstOrDefault() ?? "")));
        }
        public async Task EnsureAsync(Guid kioskId, string hardwareId, string mqttPassword, CancellationToken ct)
        {
            lock (passwords) passwords.Add(mqttPassword);
            if (DuringEnsure is not null) await DuringEnsure(ct);
            if (FailuresRemaining-- > 0)
                throw new Esp32ProvisioningException("ESP32_MQTT_PROVISIONING_UNAVAILABLE");
        }
        public Task DisableAsync(Guid kioskId, string hardwareId, CancellationToken ct) => Task.CompletedTask;
        public override string ToString() => "[Fake device provisioner redacted]";
    }

    private sealed class Fixture(NpgsqlDataSource data, Esp32ProvisioningStore store) : IAsyncDisposable
    {
        internal Guid Kiosk { get; } = Guid.NewGuid();
        internal string Hardware { get; } = "auto-test-" + Guid.NewGuid().ToString("N");
        internal Esp32ProvisioningStore Store { get; } = store;
        internal async Task<object?> Sql(string sql, params (string Name, object Value)[] extra)
        {
            await using var command = data.CreateCommand(sql);
            command.Parameters.AddWithValue("id", Kiosk);
            command.Parameters.AddWithValue("hardware", Hardware);
            foreach (var (name, value) in extra) command.Parameters.AddWithValue(name, value);
            return await command.ExecuteScalarAsync();
        }
        internal async Task Enqueue(bool requeue = false, Esp32ProvisioningStore? through = null)
        {
            await using var connection = await data.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            if (requeue) await (through ?? Store).RequeueAsync(connection, transaction, Kiosk, Actor, default);
            else await (through ?? Store).EnqueueAsync(connection, transaction, Kiosk, Actor, default);
            await transaction.CommitAsync();
        }
        internal async Task<byte[]> ProtectedFingerprint()
        {
            var ciphertext = (string)(await Sql("SELECT protected_secrets FROM kiosk_ops.esp32_provisioning_jobs WHERE kiosk_id=@id"))!;
            return SHA256.HashData(Encoding.UTF8.GetBytes(ciphertext));
        }
        public async ValueTask DisposeAsync() => await Sql("DELETE FROM kiosk_ops.kiosks WHERE id=@id");
    }

    private async Task<Fixture> Setup()
    {
        var f = new Fixture(Data, new(Data, Protection, Configuration));
        await f.Sql("""
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@id,@id::text,'Auto provisioning test','Isolated test address','HCM',@hardware,'esp32-'||@hardware,'OFFLINE')
            """);
        return f;
    }

    private static Esp32ProvisioningWorker Worker(Esp32ProvisioningStore store, FakeProvisioner fake, DeviceKeyRing ring) =>
        new(store, fake, ring, Configuration, NullLogger<Esp32ProvisioningWorker>.Instance);
    private static DeviceKeyRing EmptyRing() => new(new Dictionary<string, byte[]>());
    private static bool SameFingerprint(byte[] first, byte[] second) => CryptographicOperations.FixedTimeEquals(first, second);
    private static UnlockCommand Probe(string hardware) => new(1, hardware, Guid.NewGuid(), Guid.NewGuid(),
        0, "CUSTOMER_PICKUP", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds(), "");

    [Fact]
    public void DynamicRingRefreshNeverOverwritesOrRemovesLegacyKeys()
    {
        var legacyKey = Enumerable.Repeat((byte)1, 32).ToArray();
        var dynamicKey = Enumerable.Repeat((byte)2, 32).ToArray();
        var ring = new DeviceKeyRing(new Dictionary<string, byte[]> { ["legacy"] = legacyKey });
        Assert.True(ring.AddOrVerify("dynamic", dynamicKey));
        Assert.True(ring.AddOrVerify("dynamic", dynamicKey));
        Assert.False(ring.AddOrVerify("legacy", dynamicKey));
        var signed = ring.Sign(Probe("legacy"));
        ring.RemoveDynamic("legacy");
        Assert.True(ring.Verify(signed, DateTimeOffset.UtcNow));
        var conflicts = ring.ReplaceDynamic(new Dictionary<string, byte[]> { ["legacy"] = dynamicKey });
        Assert.Equal(["legacy"], conflicts);
        Assert.DoesNotContain("dynamic", ring.HardwareIds);
        Assert.True(DeviceProtocol.Verify(ring.Sign(Probe("legacy")), "legacy", legacyKey, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task EnrollmentRollbackLeavesNoJobAndConcurrentEnqueueKeepsOneCredentialPair()
    {
        await using var f = await Setup();
        await using (var connection = await Data.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await f.Store.EnqueueAsync(connection, transaction, f.Kiosk, Actor, default);
            Assert.Null(await f.Store.GetStatusAsync(f.Kiosk, default));
            await transaction.RollbackAsync();
        }
        Assert.Null(await f.Store.GetStatusByHardwareAsync(f.Hardware, default));
        await Task.WhenAll(f.Enqueue(), f.Enqueue());
        var fingerprint = await f.ProtectedFingerprint();
        await f.Enqueue();
        Assert.True(SameFingerprint(fingerprint, await f.ProtectedFingerprint()));
        Assert.Equal(1L, await f.Sql("SELECT count(*) FROM kiosk_ops.esp32_provisioning_jobs WHERE kiosk_id=@id"));
        Assert.Equal(1L, await f.Sql("SELECT count(*) FROM notify.audit_logs WHERE entity_id=@id AND action='ESP32_PROVISIONING_QUEUED'"));
        var status = await f.Store.GetStatusAsync(f.Kiosk, default);
        Assert.Equal("PENDING", status!.State);
        Assert.Equal(0, status.Attempts);
        Assert.Null(await f.Store.GetReadySecretsAsync(f.Hardware, default));
    }

    [Fact]
    public async Task RetryBudgetAndManualRequeueRetainCredentialsAndNeverMarkKioskOnline()
    {
        await using var f = await Setup();
        await f.Enqueue();
        var fingerprint = await f.ProtectedFingerprint();
        var fake = new FakeProvisioner { FailuresRemaining = 5 };
        var worker = Worker(f.Store, fake, EmptyRing());
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.True(await worker.RunOnceAsync(default));
            var status = (await f.Store.GetStatusAsync(f.Kiosk, default))!;
            Assert.Equal(attempt, status.Attempts);
            Assert.Equal(attempt < 5 ? "PENDING" : "FAILED", status.State);
            Assert.Equal("ESP32_MQTT_PROVISIONING_UNAVAILABLE", status.ErrorCode);
            Assert.Null(status.ReadyAt);
            Assert.False(await worker.RunOnceAsync(default));
            if (attempt < 5)
                await f.Sql("UPDATE kiosk_ops.esp32_provisioning_jobs SET next_attempt_at=CURRENT_TIMESTAMP-interval '1 second' WHERE kiosk_id=@id");
        }
        Assert.Equal(5, fake.Calls);
        Assert.True(fake.SamePasswordsAs(fake));
        await f.Enqueue();
        Assert.Equal("FAILED", (await f.Store.GetStatusAsync(f.Kiosk, default))!.State);
        await f.Enqueue(requeue: true);
        Assert.True(SameFingerprint(fingerprint, await f.ProtectedFingerprint()));
        Assert.Equal(0, (await f.Store.GetStatusAsync(f.Kiosk, default))!.Attempts);
        Assert.True(await worker.RunOnceAsync(default));
        var ready = (await f.Store.GetStatusAsync(f.Kiosk, default))!;
        Assert.Equal("READY", ready.State);
        Assert.Equal(1, ready.Attempts);
        Assert.NotNull(ready.ReadyAt);
        Assert.True(fake.SamePasswordsAs(fake));
        Assert.Equal("OFFLINE", await f.Sql("SELECT status FROM kiosk_ops.kiosks WHERE id=@id"));
    }

    [Fact]
    public async Task ConcurrentWorkersClaimOnceAndPersistCredentialsBeforeBrokerIo()
    {
        await using var f = await Setup();
        await f.Enqueue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeProvisioner
        {
            DuringEnsure = async ct =>
            {
                var status = (await f.Store.GetStatusAsync(f.Kiosk, ct))!;
                Assert.Equal("PROVISIONING", status.State);
                Assert.Equal(1, status.Attempts);
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        var first = Worker(f.Store, fake, EmptyRing()).RunOnceAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(await Worker(f.Store, fake, EmptyRing()).RunOnceAsync(default));
            Assert.Equal(1, fake.Calls);
        }
        finally { release.TrySetResult(); }
        Assert.True(await first);
        Assert.Equal("READY", (await f.Store.GetStatusAsync(f.Kiosk, default))!.State);
    }

    [Fact]
    public async Task ExpiredLeaseIsReclaimedAndStaleWorkerCannotOverwriteNewReadyAttempt()
    {
        await using var f = await Setup();
        await f.Enqueue();
        var fingerprint = await f.ProtectedFingerprint();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldFake = new FakeProvisioner { DuringEnsure = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        } };
        var oldAttempt = Worker(f.Store, oldFake, EmptyRing()).RunOnceAsync(default);
        var replacement = new FakeProvisioner();
        DateTimeOffset? readyAt;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Sql("UPDATE kiosk_ops.esp32_provisioning_jobs SET lease_expires_at=CURRENT_TIMESTAMP-interval '1 second' WHERE kiosk_id=@id");
            Assert.True(await Worker(f.Store, replacement, EmptyRing()).RunOnceAsync(default));
            var status = (await f.Store.GetStatusAsync(f.Kiosk, default))!;
            Assert.Equal("READY", status.State);
            Assert.Equal(2, status.Attempts);
            readyAt = status.ReadyAt;
        }
        finally { release.TrySetResult(); }
        Assert.True(await oldAttempt);
        var final = (await f.Store.GetStatusAsync(f.Kiosk, default))!;
        Assert.Equal("READY", final.State);
        Assert.Equal(2, final.Attempts);
        Assert.Equal(readyAt, final.ReadyAt);
        Assert.True(oldFake.SamePasswordsAs(replacement));
        Assert.True(SameFingerprint(fingerprint, await f.ProtectedFingerprint()));
    }

    [Fact]
    public async Task ReadyKeysHotReloadAfterRecycleAndDeletedDynamicKeysDisappear()
    {
        await using var f = await Setup();
        await f.Enqueue();
        var firstRing = EmptyRing();
        Assert.True(await Worker(f.Store, new FakeProvisioner(), firstRing).RunOnceAsync(default));
        var recycled = new DeviceKeyRing(new Dictionary<string, byte[]> { ["legacy-device"] = new byte[32] });
        var fake = new FakeProvisioner();
        var worker = Worker(f.Store, fake, recycled);
        Assert.False(await worker.RunOnceAsync(default));
        Assert.Equal(0, fake.Calls);
        Assert.True(recycled.Verify(firstRing.Sign(Probe(f.Hardware)), DateTimeOffset.UtcNow));
        Assert.NotNull(await f.Store.GetReadySecretsAsync(f.Hardware, default));
        await f.Sql("DELETE FROM kiosk_ops.esp32_provisioning_jobs WHERE kiosk_id=@id");
        Assert.False(await worker.RunOnceAsync(default));
        Assert.DoesNotContain(f.Hardware, recycled.HardwareIds);
        Assert.Contains("legacy-device", recycled.HardwareIds);
    }

    [Fact]
    public async Task LegacyKeyConflictFailsBeforeBrokerIoAndPreservesExistingKey()
    {
        await using var f = await Setup();
        await f.Enqueue();
        var legacy = Enumerable.Repeat((byte)7, 32).ToArray();
        var ring = new DeviceKeyRing(new Dictionary<string, byte[]> { [f.Hardware] = legacy });
        var fake = new FakeProvisioner();
        Assert.True(await Worker(f.Store, fake, ring).RunOnceAsync(default));
        Assert.Equal(0, fake.Calls);
        var status = (await f.Store.GetStatusAsync(f.Kiosk, default))!;
        Assert.Equal("FAILED", status.State);
        Assert.Equal("ESP32_HMAC_KEY_CONFLICT", status.ErrorCode);
        Assert.True(DeviceProtocol.Verify(ring.Sign(Probe(f.Hardware)), f.Hardware, legacy, DateTimeOffset.UtcNow));
        Assert.Null(await f.Store.GetReadySecretsAsync(f.Hardware, default));
    }

    [Fact]
    public async Task UnreadableProtectedCredentialsFailClosedWithoutBrokerIoOrRotation()
    {
        await using var f = await Setup();
        var foreignProtection = new Esp32ProvisioningStore(Data, new EphemeralDataProtectionProvider(), Configuration);
        await f.Enqueue(through: foreignProtection);
        var fingerprint = await f.ProtectedFingerprint();
        var fake = new FakeProvisioner();
        Assert.True(await Worker(f.Store, fake, EmptyRing()).RunOnceAsync(default));
        var status = (await f.Store.GetStatusAsync(f.Kiosk, default))!;
        Assert.Equal("FAILED", status.State);
        Assert.Equal("ESP32_PROVISIONING_SECRET_UNAVAILABLE", status.ErrorCode);
        Assert.Equal(0, fake.Calls);
        Assert.True(SameFingerprint(fingerprint, await f.ProtectedFingerprint()));
    }
}
