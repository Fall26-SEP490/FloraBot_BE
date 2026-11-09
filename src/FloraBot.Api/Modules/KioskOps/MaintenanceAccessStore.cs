using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

internal sealed record MaintenanceAccessBinding(Guid KioskId, Guid SlotId, Guid SellerId, Guid ActorId,
    string ActorRole, Guid? TaskId, string Operation, string Payload);

internal sealed record MaintenanceAccessIssued(Guid Id, string Code, DateTimeOffset ExpiresAt);
internal sealed record MaintenanceAccessPending(Guid Id, string SlotCode, string Operation, DateTimeOffset ExpiresAt);
internal enum MaintenanceAccessVerification { Valid, Invalid, Unavailable }

internal sealed class MaintenanceAccessStore(byte[] key)
{
    private const int MaximumAttempts = 5;

    public async Task<MaintenanceAccessIssued> IssueAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, MaintenanceAccessBinding binding, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(hashtextextended(@scope, 0));
            UPDATE kiosk_ops.maintenance_access_challenges SET consumed_at = CURRENT_TIMESTAMP
              WHERE actor_id = @actor AND kiosk_id = @kiosk AND slot_id = @slot
                AND consumed_at IS NULL AND expires_at > CURRENT_TIMESTAMP;
            INSERT INTO kiosk_ops.maintenance_access_challenges
                (id, kiosk_id, slot_id, seller_id, actor_id, actor_role, task_id, operation, payload, digest, expires_at)
            VALUES (@id, @kiosk, @slot, @seller, @actor, @role, @task, @operation, @payload, @digest,
                CURRENT_TIMESTAMP + INTERVAL '2 minutes')
            RETURNING expires_at
            """, connection, transaction);
        var id = Guid.CreateVersion7();
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("scope", $"maintenance-otp:{binding.ActorId}:{binding.KioskId}:{binding.SlotId}");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("kiosk", binding.KioskId);
        command.Parameters.AddWithValue("slot", binding.SlotId);
        command.Parameters.AddWithValue("seller", binding.SellerId);
        command.Parameters.AddWithValue("actor", binding.ActorId);
        command.Parameters.AddWithValue("role", binding.ActorRole);
        command.Parameters.Add("task", NpgsqlDbType.Uuid).Value = (object?)binding.TaskId ?? DBNull.Value;
        command.Parameters.AddWithValue("operation", binding.Operation);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, binding.Payload);
        command.Parameters.AddWithValue("digest", Digest(id, code));
        await using var reader = await command.ExecuteReaderAsync(ct);
        do
        {
            if (reader.FieldCount == 1 && reader.GetName(0) == "expires_at" && await reader.ReadAsync(ct))
                return new(id, code, new DateTimeOffset(reader.GetDateTime(0)));
        } while (await reader.NextResultAsync(ct));
        throw new InvalidOperationException("The maintenance access challenge was not issued.");
    }

    public static async Task<List<MaintenanceAccessPending>> PendingAsync(NpgsqlDataSource data, Guid kioskId, CancellationToken ct)
    {
        await using var command = data.CreateCommand("""
            SELECT c.id, s.slot_code, c.operation, c.expires_at
            FROM kiosk_ops.maintenance_access_challenges c
            JOIN kiosk_ops.slots s ON s.id = c.slot_id AND s.kiosk_id = c.kiosk_id
            WHERE c.kiosk_id = @kiosk AND c.consumed_at IS NULL AND c.expires_at > CURRENT_TIMESTAMP
              AND c.failed_attempts < 5
            ORDER BY c.created_at, c.id LIMIT 10
            """);
        command.Parameters.AddWithValue("kiosk", kioskId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var challenges = new List<MaintenanceAccessPending>();
        while (await reader.ReadAsync(ct))
            challenges.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), new DateTimeOffset(reader.GetDateTime(3))));
        return challenges;
    }

    public static async Task<MaintenanceAccessBinding?> ReadAsync(NpgsqlDataSource data, Guid id, Guid kioskId, CancellationToken ct)
    {
        await using var command = data.CreateCommand("""
            SELECT slot_id, seller_id, actor_id, actor_role, task_id, operation, payload
            FROM kiosk_ops.maintenance_access_challenges
            WHERE id = @id AND kiosk_id = @kiosk AND consumed_at IS NULL
              AND expires_at > CURRENT_TIMESTAMP AND failed_attempts < 5
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("kiosk", kioskId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(kioskId, reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.GetString(5), reader.GetString(6))
            : null;
    }

    public async Task<MaintenanceAccessVerification> VerifyAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid id, Guid kioskId, string? code, CancellationToken ct)
    {
        await using var lookup = new NpgsqlCommand("""
            SELECT digest FROM kiosk_ops.maintenance_access_challenges
            WHERE id = @id AND kiosk_id = @kiosk AND consumed_at IS NULL
              AND expires_at > clock_timestamp() AND failed_attempts < 5
            FOR UPDATE
            """, connection, transaction);
        lookup.Parameters.AddWithValue("id", id);
        lookup.Parameters.AddWithValue("kiosk", kioskId);
        if (await lookup.ExecuteScalarAsync(ct) is not byte[] digest)
            return MaintenanceAccessVerification.Unavailable;

        var matches = code is { Length: 6 } && code.All(digit => digit is >= '0' and <= '9')
            && CryptographicOperations.FixedTimeEquals(digest, Digest(id, code));
        var updateSql = matches
            ? "UPDATE kiosk_ops.maintenance_access_challenges SET consumed_at = clock_timestamp() WHERE id = @id"
            : "UPDATE kiosk_ops.maintenance_access_challenges SET failed_attempts = failed_attempts + 1 WHERE id = @id";
        await using var update = new NpgsqlCommand(updateSql, connection, transaction);
        update.Parameters.AddWithValue("id", id);
        await update.ExecuteNonQueryAsync(ct);
        return matches ? MaintenanceAccessVerification.Valid : MaintenanceAccessVerification.Invalid;
    }

    private byte[] Digest(Guid id, string code) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id:N}:{code}"));
}
