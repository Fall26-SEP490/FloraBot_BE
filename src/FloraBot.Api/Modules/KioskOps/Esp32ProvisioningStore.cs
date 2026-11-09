using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record Esp32ProvisioningStatus(string State, string? ErrorCode, int Attempts, DateTimeOffset? ReadyAt);

// Protected material and decrypted credentials are deliberately excluded from
// public status DTOs, generated record ToString methods and log properties.
internal sealed class Esp32ProvisioningJob(Guid kioskId, string hardwareId, string protectedSecrets,
    Guid leaseToken, int attempts)
{
    internal Guid KioskId { get; } = kioskId;
    internal string HardwareId { get; } = hardwareId;
    internal string ProtectedSecrets { get; } = protectedSecrets;
    internal Guid LeaseToken { get; } = leaseToken;
    internal int Attempts { get; } = attempts;
    public override string ToString() => "[ESP32 provisioning job redacted]";
}

public sealed class Esp32ProvisioningStore(NpgsqlDataSource data, IDataProtectionProvider protection, IConfiguration configuration)
{
    private readonly IDataProtector protector = protection.CreateProtector("Esp32DeviceProvisioning.v1");
    public bool Enabled => configuration.GetValue("MQTT_AUTO_PROVISIONING_ENABLED", false);

    public async Task EnqueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid kioskId, Guid actor, CancellationToken ct)
    {
        if (kioskId == Guid.Empty || actor == Guid.Empty) throw new ArgumentException("Kiosk and actor are required.");
        // Serialize credential creation without upgrading another authorized
        // session's kiosk SHARE lock during an explicit retry request.
        await using (var serialize = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('esp32-provisioning:'||@id::text,0))", connection, transaction))
        {
            serialize.Parameters.AddWithValue("id", kioskId);
            await serialize.ExecuteNonQueryAsync(ct);
        }
        await using var kiosk = new NpgsqlCommand("SELECT hardware_id FROM kiosk_ops.kiosks WHERE id=@id FOR KEY SHARE", connection, transaction);
        kiosk.Parameters.AddWithValue("id", kioskId);
        var hardware = await kiosk.ExecuteScalarAsync(ct) as string;
        if (!DeviceProtocol.ValidHardwareId(hardware))
            throw new Esp32ProvisioningException("ESP32_PROVISIONING_IDENTITY_INVALID", false);
        await using (var existing = new NpgsqlCommand("SELECT hardware_id FROM kiosk_ops.esp32_provisioning_jobs WHERE kiosk_id=@id", connection, transaction))
        {
            existing.Parameters.AddWithValue("id", kioskId);
            if (await existing.ExecuteScalarAsync(ct) is string prior)
            {
                if (prior != hardware) throw new Esp32ProvisioningException("ESP32_PROVISIONING_IDENTITY_CHANGED", false);
                return;
            }
        }

        var passwordBytes = RandomNumberGenerator.GetBytes(32);
        var hmacBytes = RandomNumberGenerator.GetBytes(32);
        string ciphertext;
        try
        {
            var password = Convert.ToHexStringLower(passwordBytes);
            var hmac = Convert.ToHexStringLower(hmacBytes);
            ciphertext = protector.Protect($"v1\n{kioskId:D}\n{hardware}\n{password}\n{hmac}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(hmacBytes);
        }
        await using var insert = new NpgsqlCommand("""
            INSERT INTO kiosk_ops.esp32_provisioning_jobs(kiosk_id,hardware_id,protected_secrets,created_by)
            VALUES(@id,@hardware,@protected,@actor);
            SELECT flow.audit(@actor,'ESP32_PROVISIONING_QUEUED','kiosks',@id,
              jsonb_build_object('state','PENDING'));
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", kioskId);
        insert.Parameters.AddWithValue("hardware", hardware!);
        insert.Parameters.AddWithValue("protected", ciphertext);
        insert.Parameters.AddWithValue("actor", actor);
        await insert.ExecuteNonQueryAsync(ct);
    }

    public async Task RequeueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid kioskId, Guid actor, CancellationToken ct)
    {
        await EnqueueAsync(connection, transaction, kioskId, actor, ct);
        await using var command = new NpgsqlCommand("""
            WITH changed AS (
              UPDATE kiosk_ops.esp32_provisioning_jobs
              SET state='PENDING',error_code=NULL,attempts=0,next_attempt_at=CURRENT_TIMESTAMP,
                lease_token=NULL,lease_expires_at=NULL,ready_at=NULL,updated_at=CURRENT_TIMESTAMP
              WHERE kiosk_id=@id AND state='FAILED' RETURNING kiosk_id
            )
            SELECT flow.audit(@actor,'ESP32_PROVISIONING_REQUEUED','kiosks',kiosk_id,
              jsonb_build_object('state','PENDING')) FROM changed;
            """, connection, transaction);
        command.Parameters.AddWithValue("id", kioskId);
        command.Parameters.AddWithValue("actor", actor);
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task<Esp32ProvisioningStatus?> GetStatusAsync(Guid kioskId, CancellationToken ct) =>
        ReadStatusAsync("kiosk_id=@value", kioskId, ct);

    public Task<Esp32ProvisioningStatus?> GetStatusByHardwareAsync(string hardwareId, CancellationToken ct) =>
        ReadStatusAsync("hardware_id=@value", hardwareId, ct);

    private async Task<Esp32ProvisioningStatus?> ReadStatusAsync(string predicate, object value, CancellationToken ct)
    {
        // This query must remain independent of the encrypted secret column.
        await using var command = data.CreateCommand($"SELECT state,error_code,attempts,ready_at FROM kiosk_ops.esp32_provisioning_jobs WHERE {predicate}");
        command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
    }

    public async Task<Esp32DeviceSecrets?> GetReadySecretsAsync(string hardwareId, CancellationToken ct)
    {
        await using var command = data.CreateCommand("""
            SELECT p.kiosk_id,p.protected_secrets FROM kiosk_ops.esp32_provisioning_jobs p
            JOIN kiosk_ops.kiosks k ON k.id=p.kiosk_id AND k.hardware_id=p.hardware_id
            WHERE p.hardware_id=@hardware AND p.state='READY'
            """);
        command.Parameters.AddWithValue("hardware", hardwareId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return Unprotect(reader.GetGuid(0), hardwareId, reader.GetString(1));
    }

    internal Esp32DeviceSecrets Unprotect(Esp32ProvisioningJob job) =>
        Unprotect(job.KioskId, job.HardwareId, job.ProtectedSecrets);

    private Esp32DeviceSecrets Unprotect(Guid kioskId, string hardware, string ciphertext)
    {
        try
        {
            var fields = protector.Unprotect(ciphertext).Split('\n');
            if (fields.Length != 5 || fields[0] != "v1" || fields[1] != kioskId.ToString("D")
                || fields[2] != hardware || fields[3].Length != 64 || !fields[3].All(char.IsAsciiHexDigit)
                || fields[4].Length != 64 || !fields[4].All(char.IsAsciiHexDigit))
                throw new CryptographicException();
            return new(fields[3], fields[4]);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            throw new Esp32ProvisioningException("ESP32_PROVISIONING_SECRET_UNAVAILABLE", false);
        }
    }

    internal async Task<Esp32ProvisioningJob?> ClaimAsync(CancellationToken ct)
    {
        await using (var cleanup = data.CreateCommand("""
            UPDATE kiosk_ops.esp32_provisioning_jobs
            SET state='FAILED',error_code='ESP32_PROVISIONING_LEASE_EXPIRED',
              lease_token=NULL,lease_expires_at=NULL,updated_at=CURRENT_TIMESTAMP
            WHERE state='PROVISIONING' AND lease_expires_at<=CURRENT_TIMESTAMP AND attempts>=5;
            UPDATE kiosk_ops.esp32_provisioning_jobs p
            SET state='FAILED',error_code='ESP32_PROVISIONING_IDENTITY_CHANGED',ready_at=NULL,
              lease_token=NULL,lease_expires_at=NULL,updated_at=CURRENT_TIMESTAMP
            WHERE p.state IN ('PENDING','PROVISIONING','READY') AND EXISTS (
              SELECT 1 FROM kiosk_ops.kiosks k WHERE k.id=p.kiosk_id AND k.hardware_id<>p.hardware_id);
            """))
        {
            await cleanup.ExecuteNonQueryAsync(ct);
        }
        await using var command = data.CreateCommand("""
            WITH candidate AS (
              SELECT p.kiosk_id FROM kiosk_ops.esp32_provisioning_jobs p
              WHERE p.attempts<5 AND ((p.state='PENDING' AND p.next_attempt_at<=CURRENT_TIMESTAMP)
                OR (p.state='PROVISIONING' AND p.lease_expires_at<=CURRENT_TIMESTAMP))
                AND EXISTS (SELECT 1 FROM kiosk_ops.kiosks k WHERE k.id=p.kiosk_id AND k.hardware_id=p.hardware_id)
              ORDER BY p.next_attempt_at,p.kiosk_id FOR UPDATE OF p SKIP LOCKED LIMIT 1
            )
            UPDATE kiosk_ops.esp32_provisioning_jobs p
            SET state='PROVISIONING',error_code=NULL,attempts=p.attempts+1,
              lease_token=@lease,lease_expires_at=CURRENT_TIMESTAMP+interval '120 seconds',
              ready_at=NULL,updated_at=CURRENT_TIMESTAMP
            FROM candidate c WHERE p.kiosk_id=c.kiosk_id
            RETURNING p.kiosk_id,p.hardware_id,p.protected_secrets,p.lease_token,p.attempts;
            """);
        command.Parameters.AddWithValue("lease", Guid.CreateVersion7());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3), reader.GetInt32(4));
    }

    internal async Task<bool> CompleteAsync(Esp32ProvisioningJob job, CancellationToken ct)
    {
        await using var command = data.CreateCommand("""
            UPDATE kiosk_ops.esp32_provisioning_jobs p
            SET state='READY',error_code=NULL,lease_token=NULL,lease_expires_at=NULL,
              ready_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP
            WHERE kiosk_id=@id AND state='PROVISIONING' AND lease_token=@lease
              AND lease_expires_at>CURRENT_TIMESTAMP AND EXISTS (
                SELECT 1 FROM kiosk_ops.kiosks k WHERE k.id=p.kiosk_id AND k.hardware_id=p.hardware_id)
            """);
        command.Parameters.AddWithValue("id", job.KioskId);
        command.Parameters.AddWithValue("lease", job.LeaseToken);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    internal async Task FailAsync(Esp32ProvisioningJob job, string code, bool retryable, CancellationToken ct)
    {
        var retry = retryable && job.Attempts < 5;
        var delay = Math.Min(300, 5 * (1 << Math.Min(job.Attempts - 1, 6)));
        await using var command = data.CreateCommand("""
            UPDATE kiosk_ops.esp32_provisioning_jobs
            SET state=@state,error_code=@code,lease_token=NULL,lease_expires_at=NULL,ready_at=NULL,
              next_attempt_at=CURRENT_TIMESTAMP+make_interval(secs=>@delay),updated_at=CURRENT_TIMESTAMP
            WHERE kiosk_id=@id AND state='PROVISIONING' AND lease_token=@lease
            """);
        command.Parameters.AddWithValue("state", retry ? "PENDING" : "FAILED");
        command.Parameters.AddWithValue("code", SafeCode(code));
        command.Parameters.AddWithValue("delay", (double)delay);
        command.Parameters.AddWithValue("id", job.KioskId);
        command.Parameters.AddWithValue("lease", job.LeaseToken);
        await command.ExecuteNonQueryAsync(ct);
    }

    internal async Task<List<Esp32ProvisioningJob>> ReadyAsync(CancellationToken ct)
    {
        await using var command = data.CreateCommand("""
            SELECT p.kiosk_id,p.hardware_id,p.protected_secrets FROM kiosk_ops.esp32_provisioning_jobs p
            JOIN kiosk_ops.kiosks k ON k.id=p.kiosk_id AND k.hardware_id=p.hardware_id
            WHERE p.state='READY' ORDER BY p.kiosk_id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Esp32ProvisioningJob>();
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), Guid.Empty, 0));
        return rows;
    }

    internal async Task FailReadyAsync(Guid kioskId, string code, CancellationToken ct)
    {
        await using var command = data.CreateCommand("""
            UPDATE kiosk_ops.esp32_provisioning_jobs
            SET state='FAILED',error_code=@code,ready_at=NULL,updated_at=CURRENT_TIMESTAMP
            WHERE kiosk_id=@id AND state='READY'
            """);
        command.Parameters.AddWithValue("id", kioskId);
        command.Parameters.AddWithValue("code", SafeCode(code));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static string SafeCode(string? code) => code is { Length: > 0 and <= 80 }
        && code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
            ? code : "ESP32_PROVISIONING_FAILED";
}
