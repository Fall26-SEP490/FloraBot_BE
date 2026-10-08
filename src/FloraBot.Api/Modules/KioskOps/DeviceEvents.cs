using System.Text.Json.Serialization;
using DotNetCore.CAP;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record SlotPickedUp(
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("slot_id")] Guid SlotId,
    [property: JsonPropertyName("bouquet_id")] Guid BouquetId,
    [property: JsonPropertyName("picked_up_at")] DateTime PickedUpAt);
public sealed record DeviceSnapshot(Guid CommandId, Guid? OrderId, Guid SlotId, Guid? BouquetId, string Status, Guid? SellerId);

public static class DeviceEvents
{
    public const string PickedUp = "florabot.slot.picked-up.v1";

    public static async Task<DeviceSnapshot?> BeforeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid commandId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT t.order_id,t.slot_id,s.bouquet_id,t.status,s.current_seller_id FROM kiosk_ops.unlock_tokens t
            JOIN kiosk_ops.slots s ON s.id=t.slot_id WHERE t.cmd_id=@id FOR UPDATE OF t
            """, connection, transaction);
        command.Parameters.AddWithValue("id", commandId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(commandId, reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetGuid(4)) : null;
    }

    public static async Task AfterAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ICapPublisher publisher, DeviceSnapshot before, CancellationToken ct)
    {
        if (before.OrderId is not { } orderId) return;
        await using var command = new NpgsqlCommand("SELECT status,door_closed_at FROM kiosk_ops.unlock_tokens WHERE cmd_id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", before.CommandId);
        string status; DateTime? closed;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return;
            status = reader.GetString(0); closed = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
        }
        if (status == before.Status) return;
        if (status == "CLOSED" && closed is { } at && before.BouquetId is { } bouquet)
            await publisher.PublishAsync(PickedUp, new SlotPickedUp(orderId, before.SlotId, bouquet, at), cancellationToken: ct);
    }
}
