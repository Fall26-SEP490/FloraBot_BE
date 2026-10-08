using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetCore.CAP;
using FloraBot.Api.Realtime;
using Npgsql;

namespace FloraBot.Api.Modules.Ordering;

public sealed record PaidOrderItem(
    [property: JsonPropertyName("item_type")] string ItemType,
    [property: JsonPropertyName("bouquet_id")] Guid? BouquetId,
    [property: JsonPropertyName("accessory_id")] Guid? AccessoryId,
    [property: JsonPropertyName("slot_id")] Guid? SlotId,
    [property: JsonPropertyName("quantity")] int Quantity);
public sealed record OrderPaid(
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("checkout_id")] Guid CheckoutId,
    [property: JsonPropertyName("seller_id")] Guid SellerId,
    [property: JsonPropertyName("kiosk_id")] Guid KioskId,
    [property: JsonPropertyName("items")] PaidOrderItem[] Items);

public sealed record DispenseFailed(
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("slot_id")] Guid? SlotId,
    [property: JsonPropertyName("reason")] string Reason);

public sealed class OrderEventSubscriber(PortalNotifier notifier, NpgsqlDataSource data) : ICapSubscribe
{
    [CapSubscribe(OrderEvents.Paid)]
    public Task HandleAsync(OrderPaid message) => notifier.RefreshAsync(message.SellerId);

    [CapSubscribe(KioskOps.DeviceEvents.PickedUp)]
    public Task PickedUpAsync(KioskOps.SlotPickedUp message) => RefreshOrderAsync(message.OrderId);

    [CapSubscribe(OrderEvents.Failed)]
    public Task FailedAsync(DispenseFailed message) => RefreshOrderAsync(message.OrderId);

    private async Task RefreshOrderAsync(Guid orderId)
    {
        await using var command = data.CreateCommand("SELECT seller_id FROM ordering.orders WHERE id=@id");
        command.Parameters.AddWithValue("id", orderId);
        if (await command.ExecuteScalarAsync() is Guid seller) await notifier.RefreshAsync(seller);
    }
}

public static class OrderEvents
{
    public const string Paid = "florabot.order.paid.v1";
    public const string Failed = "florabot.dispense.failed.v1";

    public static async Task PublishFailuresAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ICapPublisher publisher, Guid orderId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT slot_id,reason FROM ordering.disputes WHERE order_id=@id AND kind='DISPENSE_FAILED'
              AND xmin=pg_current_xact_id()::xid ORDER BY id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", orderId);
        var messages = new List<DispenseFailed>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) messages.Add(new(orderId, reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetString(1)));
        }
        foreach (var message in messages) await publisher.PublishAsync(Failed, message, cancellationToken: ct);
    }

    public static async Task PublishPaidAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ICapPublisher publisher, Guid checkoutId, CancellationToken ct)
    {
        // Paid orders can complete immediately (accessories) or fail dispensing in the same flow.
        // Device consumers must use live unlock tokens, never treat this fact as an unlock instruction.
        await using var command = new NpgsqlCommand("""
            SELECT o.id,o.seller_id,o.kiosk_id,
              (SELECT coalesce(jsonb_agg(jsonb_build_object('item_type',i.item_type,'bouquet_id',i.bouquet_id,
                 'accessory_id',i.accessory_id,'slot_id',i.slot_id,'quantity',i.quantity) ORDER BY i.id),'[]'::jsonb)
               FROM ordering.order_items i WHERE i.order_id=o.id AND i.line_status='ACTIVE')
            FROM ordering.orders o WHERE o.checkout_id=@id AND o.status IN ('PAID','DISPENSING','COMPLETED','DISPENSE_FAILED') ORDER BY o.id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", checkoutId);
        var messages = new List<OrderPaid>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) messages.Add(new(reader.GetGuid(0), checkoutId, reader.GetGuid(1), reader.GetGuid(2),
                JsonSerializer.Deserialize<PaidOrderItem[]>(reader.GetString(3))!));
        }
        foreach (var message in messages)
        {
            await publisher.PublishAsync(Paid, message, cancellationToken: ct);
            await PublishFailuresAsync(connection, transaction, publisher, message.OrderId, ct);
        }
    }
}
