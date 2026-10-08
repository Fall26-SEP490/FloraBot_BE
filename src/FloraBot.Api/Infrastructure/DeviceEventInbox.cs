using System.Security.Cryptography;
using System.Text.Json;
using DotNetCore.CAP;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Realtime;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Infrastructure;

public enum DeviceReceipt { Stored, Duplicate, Rejected }

public sealed class DeviceEventInbox(NpgsqlDataSource data, ICapPublisher publisher, DeviceKeyRing keys, PortalNotifier notifier)
{
    // A broker delivery can be acknowledged only after this durable receipt succeeds.
    public async Task<DeviceReceipt> ReceiveAsync(string hardware, SignedDeviceEvent message, CancellationToken ct)
    {
        if (!keys.Verify(message, hardware, DateTimeOffset.UtcNow)) return DeviceReceipt.Rejected;
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(message)));
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var identity = new NpgsqlCommand("""
            SELECT k.id FROM kiosk_ops.kiosks k WHERE k.hardware_id=@hardware
              AND (@heartbeat OR EXISTS (SELECT 1 FROM kiosk_ops.unlock_tokens t WHERE t.cmd_id=@cmd AND t.kiosk_id=k.id))
            """, connection, transaction);
        identity.Parameters.AddWithValue("hardware", hardware);
        identity.Parameters.AddWithValue("heartbeat", message.Event == "HEARTBEAT");
        identity.Parameters.AddWithValue("cmd", message.CommandId);
        if (await identity.ExecuteScalarAsync(ct) is not Guid kiosk) return DeviceReceipt.Rejected;
        await using var insert = new NpgsqlCommand("""
            INSERT INTO kiosk_ops.mqtt_inbox(event_id,kiosk_id,cmd_id,event,payload_hash,occurred_at)
            VALUES(@id,@kiosk,@cmd,@event,@hash,@at) ON CONFLICT (event_id) DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", message.EventId); insert.Parameters.AddWithValue("kiosk", kiosk);
        insert.Parameters.AddWithValue("cmd", NpgsqlDbType.Uuid, message.Event == "HEARTBEAT" ? DBNull.Value : message.CommandId);
        insert.Parameters.AddWithValue("event", message.Event); insert.Parameters.AddWithValue("hash", hash);
        insert.Parameters.AddWithValue("at", DateTimeOffset.FromUnixTimeSeconds(message.OccurredAt).UtcDateTime);
        var created = await insert.ExecuteNonQueryAsync(ct) == 1;
        if (!created)
        {
            await using var previous = new NpgsqlCommand("SELECT payload_hash FROM kiosk_ops.mqtt_inbox WHERE event_id=@id", connection, transaction);
            previous.Parameters.AddWithValue("id", message.EventId);
            if ((string?)await previous.ExecuteScalarAsync(ct) != hash) return DeviceReceipt.Rejected;
        }
        await transaction.CommitAsync(ct);
        return created ? DeviceReceipt.Stored : DeviceReceipt.Duplicate;
    }

    public async Task<IReadOnlyList<Guid>> PendingAsync(CancellationToken ct)
    {
        await using var command = data.CreateCommand("SELECT event_id FROM kiosk_ops.mqtt_inbox WHERE disposition='PENDING' ORDER BY received_at,event_id LIMIT 100");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    public async Task<bool> ProcessAsync(Guid eventId, CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        Guid kiosk; Guid? commandId; string kind; bool timedOut; bool heartbeatStale;
        await using (var receipt = new NpgsqlCommand("""
            SELECT kiosk_id,cmd_id,event,received_at < CURRENT_TIMESTAMP-interval '2 minutes',
              occurred_at < CURRENT_TIMESTAMP-make_interval(secs=>flow.cfg('heartbeat_offline_seconds'))
            FROM kiosk_ops.mqtt_inbox WHERE event_id=@id AND disposition='PENDING' FOR UPDATE SKIP LOCKED
            """, connection, transaction))
        {
            receipt.Parameters.AddWithValue("id", eventId);
            await using var reader = await receipt.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return false;
            kiosk = reader.GetGuid(0); commandId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            kind = reader.GetString(2); timedOut = reader.GetBoolean(3);
            heartbeatStale = reader.GetBoolean(4);
        }
        string disposition;
        Guid? targetSeller = null;
        if (commandId is { } cmd)
        {
            var before = await DeviceEvents.BeforeAsync(connection, transaction, cmd, ct);
            if (before is null) disposition = "REJECTED";
            else
            {
                targetSeller = before.SellerId;
                var state = before.Status;
                var apply = kind switch
                {
                    "ACK" => state == "SENT",
                    "OPENED" => state == "ACKED",
                    "CLOSED" => state == "OPENED",
                    "FAILED" => state is "ISSUED" or "SENT" or "ACKED",
                    _ => false
                };
                var duplicate = state is "CLOSED" or "FAILED" or "EXPIRED" or "REVOKED"
                    || kind == "ACK" && state is "ACKED" or "OPENED"
                    || kind == "OPENED" && state == "OPENED";
                if (apply)
                {
                    var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
                    await using var flow = new NpgsqlCommand("SELECT flow.reset_clock(); SELECT flow.device_event(@cmd,@event)", connection, transaction);
                    flow.Parameters.AddWithValue("cmd", cmd); flow.Parameters.AddWithValue("event", kind);
                    await flow.ExecuteNonQueryAsync(ct);
                    await DeviceEvents.AfterAsync(connection, transaction, publisher, before, ct);
                    await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
                    disposition = "APPLIED";
                }
                else if (duplicate) disposition = "IGNORED";
                else if (timedOut) disposition = "REJECTED";
                else return false; // Out-of-order sensor events remain durable until predecessors arrive.
            }
        }
        else if (heartbeatStale) disposition = "REJECTED";
        else
        {
            await using var heartbeat = new NpgsqlCommand("SELECT flow.reset_clock(); SELECT flow.kiosk_heartbeat(@id)", connection, transaction);
            heartbeat.Parameters.AddWithValue("id", kiosk);
            await heartbeat.ExecuteNonQueryAsync(ct);
            disposition = "APPLIED";
        }
        await using var finish = new NpgsqlCommand("UPDATE kiosk_ops.mqtt_inbox SET disposition=@state,processed_at=CURRENT_TIMESTAMP WHERE event_id=@id", connection, transaction);
        finish.Parameters.AddWithValue("state", disposition); finish.Parameters.AddWithValue("id", eventId);
        await finish.ExecuteNonQueryAsync(ct);
        await outbox.CommitAsync(ct);
        await connection.CloseAsync();
        if (disposition == "APPLIED") await notifier.RefreshAsync(targetSeller);
        return true;
    }
}
