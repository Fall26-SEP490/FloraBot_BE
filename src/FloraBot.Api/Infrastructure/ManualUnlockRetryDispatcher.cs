using DotNetCore.CAP;
using FloraBot.Api.Modules.KioskOps;
using Npgsql;

namespace FloraBot.Api.Infrastructure;

public sealed record PreparedUnlockRetry(Guid DeliveryId, UnlockRetryCommand Command);

public sealed class ManualUnlockRetryDispatcher(
    NpgsqlDataSource data,
    ICapPublisher publisher,
    DeviceKeyRing keys,
    ManualUnlockRetryService retryService)
{
    public async Task<IReadOnlyList<Guid>> PendingAsync(CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);

        // Bounded sweep: expire stale PENDING rows
        await using var expireCmd = new NpgsqlCommand("""
            UPDATE kiosk_ops.manual_unlock_retries
            SET state = 'EXPIRED', updated_at = CURRENT_TIMESTAMP
            WHERE state = 'PENDING' AND request_id IN (
                SELECT request_id FROM kiosk_ops.manual_unlock_retries
                WHERE state = 'PENDING' AND expires_at <= floor(extract(epoch FROM CURRENT_TIMESTAMP))
                ORDER BY expires_at LIMIT 50 FOR UPDATE SKIP LOCKED
            )
            """, connection);
        await expireCmd.ExecuteNonQueryAsync(ct);

        // Cancel pending retries whose parent token has already opened, closed, failed or revoked
        await using var cancelCmd = new NpgsqlCommand("""
            UPDATE kiosk_ops.manual_unlock_retries r
            SET state = 'CANCELLED', updated_at = CURRENT_TIMESTAMP
            WHERE r.state = 'PENDING' AND r.request_id IN (
                SELECT pending.request_id FROM kiosk_ops.manual_unlock_retries pending
                JOIN kiosk_ops.unlock_tokens t ON t.cmd_id = pending.parent_cmd_id
                WHERE pending.state = 'PENDING'
                  AND (t.door_opened_at IS NOT NULL OR t.door_closed_at IS NOT NULL
                       OR t.status IN ('OPENED', 'CLOSED', 'FAILED', 'REVOKED'))
                ORDER BY pending.requested_at LIMIT 50 FOR UPDATE OF pending SKIP LOCKED
            )
            """, connection);
        await cancelCmd.ExecuteNonQueryAsync(ct);

        // Return eligible due PENDING IDs
        await using var command = new NpgsqlCommand("""
            SELECT r.request_id
            FROM kiosk_ops.manual_unlock_retries r
            JOIN kiosk_ops.unlock_tokens t ON t.cmd_id = r.parent_cmd_id
            JOIN kiosk_ops.kiosks k ON k.id = r.kiosk_id
            WHERE r.state = 'PENDING'
              AND r.expires_at > floor(extract(epoch FROM CURRENT_TIMESTAMP))
              AND r.next_attempt_at <= CURRENT_TIMESTAMP
              AND k.hardware_id = ANY(@hardware)
            ORDER BY r.requested_at, r.request_id
            LIMIT 50
            """, connection);
        command.Parameters.AddWithValue("hardware", keys.HardwareIds);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetGuid(0));

        return ids;
    }

    public async Task<PreparedUnlockRetry?> PrepareAsync(Guid requestId, CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;

        await using var setLockTimeout = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", connection, transaction);
        await setLockTimeout.ExecuteNonQueryAsync(ct);

        // Fetch scope identifiers from durable queue row
        Guid parentCmdId;
        Guid kioskId;
        Guid slotId;
        Guid? orderId;
        Guid? sessionId;
        string actorKind;
        Guid actorId;
        long requestedAt;
        long expiresAt;

        await using (var fetchCmd = new NpgsqlCommand("""
            SELECT parent_cmd_id, kiosk_id, slot_id, order_id, session_id,
                   actor_kind, actor_id, requested_at, expires_at, state
            FROM kiosk_ops.manual_unlock_retries
            WHERE request_id = @requestId
            """, connection, transaction))
        {
            fetchCmd.Parameters.AddWithValue("requestId", requestId);
            await using var fetchReader = await fetchCmd.ExecuteReaderAsync(ct);
            if (!await fetchReader.ReadAsync(ct)) return null;

            var state = fetchReader.GetString(9);
            if (state != "PENDING") return null;

            parentCmdId = fetchReader.GetGuid(0);
            kioskId = fetchReader.GetGuid(1);
            slotId = fetchReader.GetGuid(2);
            orderId = fetchReader.IsDBNull(3) ? (Guid?)null : fetchReader.GetGuid(3);
            sessionId = fetchReader.IsDBNull(4) ? (Guid?)null : fetchReader.GetGuid(4);
            actorKind = fetchReader.GetString(5);
            actorId = fetchReader.GetGuid(6);
            requestedAt = fetchReader.GetInt64(7);
            expiresAt = fetchReader.GetInt64(8);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var nowSec = nowUtc.ToUnixTimeSeconds();
        if (expiresAt <= nowSec)
        {
            await using var expireSingle = new NpgsqlCommand("UPDATE kiosk_ops.manual_unlock_retries SET state = 'EXPIRED', updated_at = CURRENT_TIMESTAMP WHERE request_id = @requestId AND state = 'PENDING'", connection, transaction);
            expireSingle.Parameters.AddWithValue("requestId", requestId);
            await expireSingle.ExecuteNonQueryAsync(ct);
            await outbox.CommitAsync(ct);
            return null;
        }

        // Lock parent token before request row using LoadCandidateAsync with skipLocked: true
        var candidate = await retryService.LoadCandidateAsync(
            actorKind, actorId, kioskId, slotId, parentCmdId, orderId, sessionId,
            guestTrackingToken: null,
            connection, transaction, ct, skipLocked: true);

        if (candidate is null)
            return null;

        var denial = ManualUnlockRetryService.GetDenial(candidate, actorKind, nowUtc, out var cooldown);
        if (denial is not null)
        {
            if (cooldown > 0)
            {
                await using var defer = new NpgsqlCommand("""
                    UPDATE kiosk_ops.manual_unlock_retries
                    SET next_attempt_at = CURRENT_TIMESTAMP + make_interval(secs => @cool), updated_at = CURRENT_TIMESTAMP
                    WHERE request_id = @requestId AND state = 'PENDING'
                    """, connection, transaction);
                defer.Parameters.AddWithValue("cool", (int)cooldown);
                defer.Parameters.AddWithValue("requestId", requestId);
                await defer.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await SetTerminalAsync(connection, transaction, requestId,
                    candidate.ParentExpiresAtEpoch <= nowSec ? "EXPIRED" : "CANCELLED", ct);
            }
            await outbox.CommitAsync(ct);
            return null;
        }

        if (expiresAt > candidate.ParentExpiresAtEpoch || !keys.HardwareIds.Contains(candidate.HardwareId))
            return null;

        var deliveryId = Guid.NewGuid();
        await using (var updateAttempt = new NpgsqlCommand("""
            UPDATE kiosk_ops.manual_unlock_retries
            SET delivery_id = @deliveryId,
                next_attempt_at = CURRENT_TIMESTAMP + interval '10 seconds',
                updated_at = CURRENT_TIMESTAMP
            WHERE request_id = @requestId AND state = 'PENDING'
              AND requested_at = @requestedAt AND expires_at = @expiresAt
              AND expires_at > floor(extract(epoch FROM clock_timestamp()))
              AND next_attempt_at <= CURRENT_TIMESTAMP
              AND hardware_id_snapshot = @hardware AND relay_channel_snapshot = @relay
              AND slot_id_snapshot = @slot
            """, connection, transaction))
        {
            updateAttempt.Parameters.AddWithValue("deliveryId", deliveryId);
            updateAttempt.Parameters.AddWithValue("requestId", requestId);
            updateAttempt.Parameters.AddWithValue("requestedAt", requestedAt);
            updateAttempt.Parameters.AddWithValue("expiresAt", expiresAt);
            updateAttempt.Parameters.AddWithValue("hardware", candidate.HardwareId);
            updateAttempt.Parameters.AddWithValue("relay", candidate.RelayChannel);
            updateAttempt.Parameters.AddWithValue("slot", candidate.SlotId);
            if (await updateAttempt.ExecuteNonQueryAsync(ct) == 0) return null;
        }

        var command = new UnlockRetryCommand(
            Version: 1,
            HardwareId: candidate.HardwareId,
            RequestId: requestId,
            CommandId: candidate.CmdId,
            RequestedAt: requestedAt,
            ExpiresAt: expiresAt,
            Signature: "");

        var signedCommand = keys.Sign(command);
        await outbox.CommitAsync(ct);
        return new PreparedUnlockRetry(deliveryId, signedCommand);
    }

    public async Task<bool> DeliverAsync(
        PreparedUnlockRetry prepared,
        Func<UnlockRetryCommand, CancellationToken, Task> publish,
        CancellationToken ct)
    {
        if (!keys.Verify(prepared.Command, DateTimeOffset.UtcNow)) return false;

        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using var setLockTimeout = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", connection, transaction);
        await setLockTimeout.ExecuteNonQueryAsync(ct);

        // Fetch scope from manual_unlock_retries
        Guid parentCmdId;
        Guid kioskId;
        Guid slotId;
        Guid? orderId;
        Guid? sessionId;
        string actorKind;
        Guid actorId;

        await using (var fetchCmd = new NpgsqlCommand("""
            SELECT parent_cmd_id, kiosk_id, slot_id, order_id, session_id,
                   actor_kind, actor_id, requested_at, expires_at, state, delivery_id
            FROM kiosk_ops.manual_unlock_retries
            WHERE request_id = @requestId
            """, connection, transaction))
        {
            fetchCmd.Parameters.AddWithValue("requestId", prepared.Command.RequestId);
            await using var fetchReader = await fetchCmd.ExecuteReaderAsync(ct);
            if (!await fetchReader.ReadAsync(ct)) return false;

            var currentDelivery = fetchReader.IsDBNull(10) ? (Guid?)null : fetchReader.GetGuid(10);
            var state = fetchReader.GetString(9);
            var expiresAt = fetchReader.GetInt64(8);
            var requestedAt = fetchReader.GetInt64(7);
            var cmd = fetchReader.GetGuid(0);

            if (currentDelivery != prepared.DeliveryId || state != "PENDING") return false;
            if (cmd != prepared.Command.CommandId || requestedAt != prepared.Command.RequestedAt || expiresAt != prepared.Command.ExpiresAt) return false;
            if (expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;

            parentCmdId = cmd;
            kioskId = fetchReader.GetGuid(1);
            slotId = fetchReader.GetGuid(2);
            orderId = fetchReader.IsDBNull(3) ? (Guid?)null : fetchReader.GetGuid(3);
            sessionId = fetchReader.IsDBNull(4) ? (Guid?)null : fetchReader.GetGuid(4);
            actorKind = fetchReader.GetString(5);
            actorId = fetchReader.GetGuid(6);
        }

        // Lock parent token using LoadCandidateAsync under NO KEY UPDATE OF t SKIP LOCKED
        var candidate = await retryService.LoadCandidateAsync(
            actorKind, actorId, kioskId, slotId, parentCmdId, orderId, sessionId,
            guestTrackingToken: null,
            connection, transaction, ct, skipLocked: true);

        if (candidate is null || candidate.HardwareId != prepared.Command.HardwareId ||
            prepared.Command.ExpiresAt > candidate.ParentExpiresAtEpoch)
            return false;

        var now = DateTimeOffset.UtcNow;
        if (ManualUnlockRetryService.GetDenial(candidate, actorKind, now, out var cooldown) is not null)
        {
            if (cooldown == 0)
            {
                await SetTerminalAsync(connection, transaction, prepared.Command.RequestId,
                    candidate.ParentExpiresAtEpoch <= now.ToUnixTimeSeconds() ? "EXPIRED" : "CANCELLED", ct);
                await transaction.CommitAsync(ct);
            }
            return false;
        }

        // Lock queue row under FOR UPDATE
        await using (var lockRow = new NpgsqlCommand("""
            SELECT request_id FROM kiosk_ops.manual_unlock_retries
            WHERE request_id = @requestId AND delivery_id = @deliveryId AND state = 'PENDING'
              AND parent_cmd_id = @parent AND requested_at = @requestedAt AND expires_at = @expiresAt
              AND expires_at > floor(extract(epoch FROM clock_timestamp()))
              AND hardware_id_snapshot = @hardware AND relay_channel_snapshot = @relay
              AND slot_id_snapshot = @slot
            FOR UPDATE SKIP LOCKED
            """, connection, transaction))
        {
            lockRow.Parameters.AddWithValue("requestId", prepared.Command.RequestId);
            lockRow.Parameters.AddWithValue("deliveryId", prepared.DeliveryId);
            lockRow.Parameters.AddWithValue("parent", candidate.CmdId);
            lockRow.Parameters.AddWithValue("requestedAt", prepared.Command.RequestedAt);
            lockRow.Parameters.AddWithValue("expiresAt", prepared.Command.ExpiresAt);
            lockRow.Parameters.AddWithValue("hardware", candidate.HardwareId);
            lockRow.Parameters.AddWithValue("relay", candidate.RelayChannel);
            lockRow.Parameters.AddWithValue("slot", candidate.SlotId);
            if (await lockRow.ExecuteScalarAsync(ct) is null) return false;
        }

        // Bounded 5s publish
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        // Preparation persists the retry deadline before the transport call.
        await publish(prepared.Command, timeoutCts.Token);

        // Successful publish: state = PUBLISHED + timestamp
        await using var successUpdate = new NpgsqlCommand("""
            UPDATE kiosk_ops.manual_unlock_retries
            SET state = 'PUBLISHED',
                published_at = CURRENT_TIMESTAMP,
                updated_at = CURRENT_TIMESTAMP
            WHERE request_id = @requestId AND delivery_id = @deliveryId AND state = 'PENDING'
            """, connection, transaction);
        successUpdate.Parameters.AddWithValue("requestId", prepared.Command.RequestId);
        successUpdate.Parameters.AddWithValue("deliveryId", prepared.DeliveryId);
        await successUpdate.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static async Task SetTerminalAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid requestId, string state, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE kiosk_ops.manual_unlock_retries SET state = @state, updated_at = CURRENT_TIMESTAMP
            WHERE request_id = @requestId AND state = 'PENDING'
            """, connection, transaction);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("requestId", requestId);
        await command.ExecuteNonQueryAsync(ct);
    }
}
