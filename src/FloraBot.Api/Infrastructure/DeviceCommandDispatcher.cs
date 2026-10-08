using DotNetCore.CAP;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Notify;
using Npgsql;

namespace FloraBot.Api.Infrastructure;

public sealed record PreparedUnlock(Guid DeliveryId, UnlockCommand Command);

public sealed class DeviceCommandDispatcher(NpgsqlDataSource data, ICapPublisher publisher, DeviceKeyRing keys)
{
    private const string Eligible = """
        t.status IN ('ISSUED','SENT') AND t.expires_at>CURRENT_TIMESTAMP
        AND k.status='ONLINE' AND k.last_heartbeat_at>CURRENT_TIMESTAMP-make_interval(secs=>flow.cfg('heartbeat_offline_seconds'))
        AND k.hardware_id=ANY(@hardware)
        AND (t.purpose='SELLER_ACCESS' OR (s.status='HELD' AND s.hold_order_id=t.order_id AND s.hold_until>CURRENT_TIMESTAMP))
        """;

    public async Task<IReadOnlyList<Guid>> PendingAsync(CancellationToken ct)
    {
        await using var command = data.CreateCommand($"""
            SELECT t.cmd_id FROM kiosk_ops.unlock_tokens t JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id
            JOIN kiosk_ops.slots s ON s.id=t.slot_id LEFT JOIN kiosk_ops.mqtt_dispatches d ON d.cmd_id=t.cmd_id
            WHERE {Eligible} AND (d.cmd_id IS NULL OR d.next_attempt_at<=CURRENT_TIMESTAMP)
            ORDER BY t.issued_at,t.cmd_id LIMIT 100
            """);
        command.Parameters.AddWithValue("hardware", keys.HardwareIds);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    public async Task<PreparedUnlock?> PrepareAsync(Guid cmd, CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        UnlockCommand message; int attempts; int maximum; bool previouslyDispatched;
        await using (var select = new NpgsqlCommand($"""
            SELECT k.hardware_id,t.slot_id,s.relay_channel,t.purpose,t.issued_at,t.expires_at,t.attempts,flow.cfg('door_max_attempts'),d.cmd_id IS NOT NULL
            FROM kiosk_ops.unlock_tokens t JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id
            JOIN kiosk_ops.slots s ON s.id=t.slot_id LEFT JOIN kiosk_ops.mqtt_dispatches d ON d.cmd_id=t.cmd_id
            WHERE t.cmd_id=@cmd AND {Eligible} AND (d.cmd_id IS NULL OR d.next_attempt_at<=CURRENT_TIMESTAMP)
              AND NOT EXISTS (SELECT 1 FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id=t.cmd_id AND i.disposition='PENDING')
            FOR UPDATE OF t SKIP LOCKED
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("cmd", cmd); select.Parameters.AddWithValue("hardware", keys.HardwareIds);
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            message = new(1, reader.GetString(0), cmd, reader.GetGuid(1), reader.GetInt16(2), reader.GetString(3),
                new DateTimeOffset(reader.GetDateTime(4)).ToUnixTimeSeconds(), new DateTimeOffset(reader.GetDateTime(5)).ToUnixTimeSeconds(), "");
            attempts = reader.GetInt32(6); maximum = reader.GetInt32(7);
            previouslyDispatched = reader.GetBoolean(8);
        }
        await using var clock = new NpgsqlCommand("SELECT flow.reset_clock()", connection, transaction);
        await clock.ExecuteNonQueryAsync(ct);
        if (attempts >= maximum)
        {
            if (!previouslyDispatched) return null;
            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            await using var failed = new NpgsqlCommand("SELECT flow.device_event(@cmd,'FAILED')", connection, transaction);
            failed.Parameters.AddWithValue("cmd", cmd);
            await failed.ExecuteNonQueryAsync(ct);
            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);
            return null;
        }
        var signed = keys.Sign(message);
        var delivery = Guid.NewGuid();
        await using var prepare = new NpgsqlCommand("""
            SELECT flow.device_event(@cmd,'SENT');
            INSERT INTO kiosk_ops.mqtt_dispatches(cmd_id,delivery_id,next_attempt_at)
            VALUES(@cmd,@delivery,CURRENT_TIMESTAMP+interval '10 seconds')
            ON CONFLICT(cmd_id) DO UPDATE SET delivery_id=excluded.delivery_id,attempted_at=CURRENT_TIMESTAMP,
              next_attempt_at=excluded.next_attempt_at,pubacked_at=NULL
            """, connection, transaction);
        prepare.Parameters.AddWithValue("cmd", cmd); prepare.Parameters.AddWithValue("delivery", delivery);
        await prepare.ExecuteNonQueryAsync(ct);
        await outbox.CommitAsync(ct);
        return new(delivery, signed);
    }

    public async Task<bool> DeliverAsync(PreparedUnlock prepared, Func<UnlockCommand, CancellationToken, Task> publish, CancellationToken ct)
    {
        if (!keys.Verify(prepared.Command, DateTimeOffset.UtcNow)) return false;
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // NO KEY UPDATE serializes revocation but lets the receiver insert its token FK before PUBACK.
        await using var live = new NpgsqlCommand($"""
            SELECT t.cmd_id FROM kiosk_ops.unlock_tokens t JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id
            JOIN kiosk_ops.slots s ON s.id=t.slot_id JOIN kiosk_ops.mqtt_dispatches d ON d.cmd_id=t.cmd_id
            WHERE t.cmd_id=@cmd AND d.delivery_id=@delivery AND d.pubacked_at IS NULL AND {Eligible}
              AND NOT EXISTS (SELECT 1 FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id=t.cmd_id AND i.disposition='PENDING')
              AND k.hardware_id=@expected_hardware AND t.slot_id=@slot AND s.relay_channel=@relay AND t.purpose=@purpose
              AND floor(extract(epoch FROM t.issued_at))=@issued AND floor(extract(epoch FROM t.expires_at))=@expires
            FOR NO KEY UPDATE OF t SKIP LOCKED
            """, connection, transaction);
        live.Parameters.AddWithValue("cmd", prepared.Command.CommandId); live.Parameters.AddWithValue("delivery", prepared.DeliveryId);
        live.Parameters.AddWithValue("hardware", keys.HardwareIds);
        live.Parameters.AddWithValue("expected_hardware", prepared.Command.HardwareId);
        live.Parameters.AddWithValue("slot", prepared.Command.SlotId); live.Parameters.AddWithValue("relay", prepared.Command.RelayChannel);
        live.Parameters.AddWithValue("purpose", prepared.Command.Purpose); live.Parameters.AddWithValue("issued", prepared.Command.IssuedAt);
        live.Parameters.AddWithValue("expires", prepared.Command.ExpiresAt);
        if (await live.ExecuteScalarAsync(ct) is not Guid) return false;
        // Bound the lock duration even if the transport stops responding. Firmware rechecks expiry.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await publish(prepared.Command, timeout.Token);
        await using var acknowledged = new NpgsqlCommand("UPDATE kiosk_ops.mqtt_dispatches SET pubacked_at=CURRENT_TIMESTAMP WHERE cmd_id=@cmd AND delivery_id=@delivery", connection, transaction);
        acknowledged.Parameters.AddWithValue("cmd", prepared.Command.CommandId); acknowledged.Parameters.AddWithValue("delivery", prepared.DeliveryId);
        await acknowledged.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
