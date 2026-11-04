using System.Security.Claims;
using System.Text.Json;
using DotNetCore.CAP;
using FloraBot.Api.Data;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Notify;
using Npgsql;

namespace FloraBot.Api.Modules.Ordering;

public sealed record KioskPickupOrder(Guid Id, string OrderCode, string Name, string Status, DateTime PickupBefore);
public sealed record DeviceSupportInput(string Reason);
public sealed record DeviceSupportResult(Guid Id);

public static class KioskCustomerFlows
{
    public static void MapKioskCustomerFlows(this WebApplication app)
    {
        app.MapGet("/api/kiosk-support/{kioskId:guid}", async (Guid kioskId, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var info = await KioskHomeDirectory.ReadAsync(kioskId, db, ct);
            return info is null ? Results.NotFound() : Results.Ok(info);
        }).AllowAnonymous().RequireRateLimiting("receipt").Produces<KioskHomeInfo>();

        app.MapPost("/api/kiosk-support/{kioskId:guid}/device", async (Guid kioskId, DeviceSupportInput input,
            FloraDbContext db, NpgsqlDataSource source, CancellationToken ct) =>
        {
            var reason = input.Reason?.Trim();
            if (!ValidReason(reason)) return Results.BadRequest(new { message = "Mô tả vấn đề từ 10 đến 1.000 ký tự." });
            if (await KioskHomeDirectory.ReadAsync(kioskId, db, ct) is null) return Results.NotFound();
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var command = new NpgsqlCommand("""
                INSERT INTO ordering.disputes(kind,kiosk_id,reason)
                VALUES('DEVICE_FAULT',@kiosk,'[Khách tại tủ] ' || @reason) RETURNING id;
                """, connection, transaction);
            command.Parameters.AddWithValue("kiosk", kioskId);
            command.Parameters.AddWithValue("reason", reason!);
            var id = (Guid)(await command.ExecuteScalarAsync(ct))!;
            await using var audit = new NpgsqlCommand("SELECT flow.audit(NULL,'CUSTOMER_DEVICE_REPORTED','disputes',@id)", connection, transaction);
            audit.Parameters.AddWithValue("id", id);
            await audit.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            // A public report enters the existing admin queue. It must never disable a slot,
            // cancel an order, issue a refund or send a hardware command.
            return Results.Ok(new DeviceSupportResult(id));
        }).AllowAnonymous().RequireRateLimiting("kiosk-support").Produces<DeviceSupportResult>();

        app.MapGet("/api/kiosks/{kioskId:guid}/pickup/orders", async (Guid kioskId, HttpContext http,
            NpgsqlDataSource source, CancellationToken ct) =>
        {
            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
            http.Response.Headers.CacheControl = "no-store";
            await using var command = source.CreateCommand("""
                SELECT o.id,o.order_code,coalesce(r.display_snapshot->>'product','Đơn hoa'),o.status,r.pickup_before
                FROM ordering.orders o JOIN ordering.web_requests r ON r.order_id=o.id
                WHERE o.customer_id=@customer AND r.customer_id=@customer AND o.kiosk_id=@kiosk
                  AND o.status IN ('PAID','DISPENSING') AND r.pickup_at <= public.app_now()
                  AND r.pickup_before > public.app_now()
                  AND EXISTS(SELECT 1 FROM ordering.order_items i WHERE i.order_id=o.id AND i.item_type='BOUQUET' AND i.line_status='ACTIVE')
                ORDER BY r.pickup_at,o.id LIMIT 50
                """);
            command.Parameters.AddWithValue("kiosk", kioskId);
            command.Parameters.AddWithValue("customer", Guid.Parse(http.User.FindFirstValue("sub")!));
            var orders = new List<KioskPickupOrder>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) orders.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4)));
            return Results.Ok(orders);
        }).RequireAuthorization("KioskMember").Produces<List<KioskPickupOrder>>();

        app.MapGet("/api/kiosks/{kioskId:guid}/pickup/orders/{orderId:guid}", async (Guid kioskId, Guid orderId,
            HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
        {
            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
            http.Response.Headers.CacheControl = "no-store";
            await using var command = source.CreateCommand("""
                SELECT o.id,o.order_code,coalesce(r.display_snapshot->>'product','Đơn hoa'),o.status,r.pickup_before
                FROM ordering.orders o JOIN ordering.web_requests r ON r.order_id=o.id
                WHERE o.id=@order AND o.customer_id=@customer AND r.customer_id=@customer AND o.kiosk_id=@kiosk
                """);
            command.Parameters.AddWithValue("order", orderId); command.Parameters.AddWithValue("kiosk", kioskId);
            command.Parameters.AddWithValue("customer", Guid.Parse(http.User.FindFirstValue("sub")!));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            return Results.Ok(new KioskPickupOrder(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4)));
        }).RequireAuthorization("KioskMember").Produces<KioskPickupOrder>();

        app.MapPost("/api/kiosks/{kioskId:guid}/pickup/orders/{orderId:guid}/open", async (Guid kioskId, Guid orderId,
            HttpContext http, NpgsqlDataSource source, ICapPublisher publisher, CancellationToken ct) =>
        {
            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
            await using var connection = await source.OpenConnectionAsync(ct);
            using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
            var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            await using var find = new NpgsqlCommand("""
                SELECT o.tracking_token FROM ordering.orders o
                JOIN ordering.web_requests r ON r.order_id=o.id
                WHERE o.id=@order AND o.customer_id=@customer AND r.customer_id=@customer AND o.kiosk_id=@kiosk
                FOR UPDATE OF o
                """, connection, transaction);
            find.Parameters.AddWithValue("order", orderId); find.Parameters.AddWithValue("kiosk", kioskId);
            find.Parameters.AddWithValue("customer", Guid.Parse(http.User.FindFirstValue("sub")!));
            if (await find.ExecuteScalarAsync(ct) is not string receipt) return Results.NotFound();
            await using var command = new NpgsqlCommand("SELECT flow.request_pickup(@order,@receipt,@kiosk)", connection, transaction);
            command.Parameters.AddWithValue("order", orderId); command.Parameters.AddWithValue("receipt", receipt); command.Parameters.AddWithValue("kiosk", kioskId);
            var result = await command.ExecuteScalarAsync(ct);
            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);
            return Results.Ok(new FlowResult(result is Guid id ? JsonSerializer.SerializeToElement(id) : null));
        }).RequireAuthorization("KioskMember").RequireRateLimiting("auth").Produces<FlowResult>();
    }

    public static bool ValidReason(string? reason) => reason is { Length: >= 10 and <= 1000 }
        && !reason.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));
}
