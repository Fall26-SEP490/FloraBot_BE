using System.Security.Cryptography;

namespace FloraBot.Api.Modules.KioskOps;

public sealed class Esp32ProvisioningWorker(Esp32ProvisioningStore store, IMqttDeviceProvisioner provisioner,
    DeviceKeyRing keys, IConfiguration configuration, ILogger<Esp32ProvisioningWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim cycleLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.Enabled) return;
        var seconds = Math.Clamp(configuration.GetValue("MQTT_AUTO_PROVISIONING_POLL_SECONDS", 5), 1, 60);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Repeated cycles recover READY keys after process recycle and
                // propagate another process's successfully provisioned devices.
                if (await RunOnceAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Exception text can contain provider/configuration material.
                logger.LogWarning("ESP32 provisioning cycle failed: {Code}", "ESP32_PROVISIONING_STORE_UNAVAILABLE");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        if (!store.Enabled) return false;
        await cycleLock.WaitAsync(ct);
        try { return await RunCycleAsync(ct); }
        finally { cycleLock.Release(); }
    }

    private async Task<bool> RunCycleAsync(CancellationToken ct)
    {
        var readyRows = await store.ReadyAsync(ct);
        var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var ready in readyRows)
            {
                try
                {
                    var secrets = store.Unprotect(ready);
                    snapshot.Add(ready.HardwareId, Convert.FromHexString(secrets.HmacKeyHex));
                }
                catch (Esp32ProvisioningException ex)
                {
                    var code = Esp32ProvisioningStore.SafeCode(ex.ErrorCode);
                    await store.FailReadyAsync(ready.KioskId, code, ct);
                    logger.LogWarning("ESP32 provisioning failed for kiosk {KioskId}: {Code}", ready.KioskId, code);
                }
            }
            foreach (var hardware in keys.ReplaceDynamic(snapshot))
            {
                var ready = readyRows.Single(row => row.HardwareId == hardware);
                const string code = "ESP32_HMAC_KEY_CONFLICT";
                await store.FailReadyAsync(ready.KioskId, code, ct);
                logger.LogWarning("ESP32 provisioning failed for kiosk {KioskId}: {Code}", ready.KioskId, code);
            }
        }
        finally
        {
            foreach (var candidate in snapshot.Values) CryptographicOperations.ZeroMemory(candidate);
        }

        var job = await store.ClaimAsync(ct);
        if (job is null) return false;
        try
        {
            var secrets = store.Unprotect(job);
            // Detect a pre-existing manual signing key before broker I/O.
            // An automatic enrollment must never rotate a legacy identity.
            if (keys.HardwareIds.Contains(job.HardwareId, StringComparer.Ordinal))
            {
                var candidate = Convert.FromHexString(secrets.HmacKeyHex);
                try
                {
                    if (!Esp32ConfigurationSource.MatchesBackendKey(keys, job.HardwareId, candidate))
                        throw new Esp32ProvisioningException("ESP32_HMAC_KEY_CONFLICT", false);
                }
                finally { CryptographicOperations.ZeroMemory(candidate); }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await provisioner.EnsureAsync(job.KioskId, job.HardwareId, secrets.MqttPassword, timeout.Token);
            if (!AddKey(job.HardwareId, secrets))
                throw new Esp32ProvisioningException("ESP32_HMAC_KEY_CONFLICT", false);
            // READY is fenced by this attempt's unexpired lease, and does not
            // mark a physical kiosk ONLINE or fabricate device observations.
            if (await store.CompleteAsync(job, ct))
                logger.LogInformation("ESP32 provisioning ready for kiosk {KioskId}", job.KioskId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Esp32ProvisioningException ex)
        {
            var code = Esp32ProvisioningStore.SafeCode(ex.ErrorCode);
            await store.FailAsync(job, code, ex.Retryable, ct);
            logger.LogWarning("ESP32 provisioning failed for kiosk {KioskId}: {Code}", job.KioskId, code);
        }
        catch (Exception)
        {
            // Timeouts and unexpected transport/provider failures are retried
            // within the persisted five-attempt budget using the same secrets.
            const string code = "ESP32_PROVISIONING_UNAVAILABLE";
            await store.FailAsync(job, code, true, ct);
            logger.LogWarning("ESP32 provisioning failed for kiosk {KioskId}: {Code}", job.KioskId, code);
        }
        return true;
    }

    private bool AddKey(string hardware, Esp32DeviceSecrets secrets)
    {
        var candidate = Convert.FromHexString(secrets.HmacKeyHex);
        try { return keys.AddOrVerify(hardware, candidate); }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }
}
