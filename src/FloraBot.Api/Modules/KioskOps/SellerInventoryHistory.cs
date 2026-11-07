using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record SellerInventoryEvent(long Seq, Guid BouquetId, Guid SlotId, Guid KioskId,
    string SlotCode, string MovementType, Guid? OrderId, string? OrderCode,
    Guid? PerformedBy, string? ActorName, DateTime CreatedAt);
public sealed record SellerInventoryHistoryPage(List<SellerInventoryEvent> Items, int Page, bool HasMore);

public static class SellerInventoryHistory
{
    public static void MapSellerInventoryHistory(this WebApplication app)
    {
        app.MapGet("/api/me/inventory-history", async (
            Guid? kioskId, Guid? slotId, Guid? bouquetId, DateTimeOffset? from, DateTimeOffset? to,
            int? page, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor) ||
                !Guid.TryParse(http.User.FindFirstValue("seller_id"), out var seller)) return Results.Unauthorized();
            var number = page.GetValueOrDefault(1);
            if (number is < 1 or > 100000 || from > to) return Results.BadRequest();
            await using var conn = await data.OpenConnectionAsync(ct);
            await using (var active = new NpgsqlCommand("""
                SELECT 1 FROM identity.users u JOIN identity.sellers s ON s.id=u.seller_id
                WHERE u.id=@actor AND u.role='SELLER' AND u.status='ACTIVE'
                  AND s.id=@seller AND s.status='ACTIVE'
                """, conn))
            {
                active.Parameters.AddWithValue("actor", actor);
                active.Parameters.AddWithValue("seller", seller);
                if (await active.ExecuteScalarAsync(ct) is null) return Results.Forbid();
            }
            await using var cmd = new NpgsqlCommand("""
                SELECT l.seq,l.bouquet_id,l.slot_id,s.kiosk_id,s.slot_code,l.movement_type,
                       l.order_id,o.order_code,l.performed_by,u.full_name,l.created_at
                FROM kiosk_ops.inventory_logs l
                JOIN kiosk_ops.slots s ON s.id=l.slot_id
                JOIN kiosk_ops.bouquets b ON b.id=l.bouquet_id
                LEFT JOIN ordering.orders o ON o.id=l.order_id
                LEFT JOIN identity.users u ON u.id=l.performed_by
                WHERE b.seller_id=@seller
                  AND (@kiosk IS NULL OR s.kiosk_id=@kiosk)
                  AND (@slot IS NULL OR s.id=@slot)
                  AND (@bouquet IS NULL OR b.id=@bouquet)
                  AND (@from IS NULL OR l.created_at>=@from)
                  AND (@to IS NULL OR l.created_at<@to)
                ORDER BY l.seq DESC LIMIT 51 OFFSET @skip
                """, conn);
            cmd.Parameters.AddWithValue("seller", seller);
            cmd.Parameters.Add("kiosk", NpgsqlDbType.Uuid).Value = (object?)kioskId ?? DBNull.Value;
            cmd.Parameters.Add("slot", NpgsqlDbType.Uuid).Value = (object?)slotId ?? DBNull.Value;
            cmd.Parameters.Add("bouquet", NpgsqlDbType.Uuid).Value = (object?)bouquetId ?? DBNull.Value;
            cmd.Parameters.Add("from", NpgsqlDbType.TimestampTz).Value = from.HasValue ? from.Value.UtcDateTime : DBNull.Value;
            cmd.Parameters.Add("to", NpgsqlDbType.TimestampTz).Value = to.HasValue ? to.Value.UtcDateTime : DBNull.Value;
            cmd.Parameters.AddWithValue("skip", (number - 1) * 50);
            var events = new List<SellerInventoryEvent>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                events.Add(new(reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
                    reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetGuid(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetGuid(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetDateTime(10)));
            return Results.Ok(new SellerInventoryHistoryPage(events.Take(50).ToList(), number, events.Count > 50));
        }).RequireAuthorization("Seller").WithTags("KioskOps").Produces<SellerInventoryHistoryPage>();
    }
}
