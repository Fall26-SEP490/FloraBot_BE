using System.Security.Claims;
using DotNetCore.CAP;
using FloraBot.Api.Modules.Catalog;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Infrastructure;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record StaffStockInput(Guid Id, Guid ProductId, Guid SlotId, string QrCode);
public sealed record StaffStockResult(Guid Id, Guid BouquetId);
public sealed record StaffStockSlot(Guid Id, string Code, string? Zone);
public sealed record StaffStockAction(Guid Id, Guid ProductId, Guid SlotId, string QrCode, Guid BouquetId, DateTime CreatedAt);
public sealed record StaffStockOptions(List<StaffStockProduct> Products, List<StaffStockSlot> Slots, List<StaffStockAction> Actions);

public static class StaffStock
{
    public static void MapStaffStock(this WebApplication app)
    {
        app.MapGet("/api/staff/tasks/{taskId:guid}/stock", Read).RequireAuthorization("SellerStaff").Produces<StaffStockOptions>();
        app.MapPost("/api/staff/tasks/{taskId:guid}/stock", async (Guid taskId, StaffStockInput input, NpgsqlDataSource data, ICapPublisher publisher, HttpContext http, CancellationToken ct) =>
        {
            if (input.Id == Guid.Empty || input.ProductId == Guid.Empty || input.SlotId == Guid.Empty || string.IsNullOrWhiteSpace(input.QrCode) || input.QrCode.Trim().Length > 120 || input.QrCode.Any(char.IsControl)) return Results.BadRequest();
            var actor = Guid.Parse(http.User.FindFirstValue("sub")!);
            await using var connection = await data.OpenConnectionAsync(ct);
            using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
            var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
            try
            {
                // Lock the assignment before the stock routine updates the same row.
                await using (var scope = new NpgsqlCommand("""
                    SELECT t.status,
                           EXISTS (SELECT 1 FROM kiosk_ops.staff_inventory_tasks i WHERE i.task_id=t.id),
                           EXISTS (SELECT 1 FROM kiosk_ops.staff_stock_actions a
                                   WHERE a.id=@operation AND a.task_id=t.id AND a.actor_id=u.id)
                    FROM kiosk_ops.staff_tasks t
                    JOIN identity.users u ON u.id=@actor AND u.seller_id=t.seller_id
                    WHERE t.id=@task AND t.assignee_id=u.id AND t.kind='DELIVERY'
                      AND u.role='SELLER_STAFF' AND u.status='ACTIVE'
                    FOR UPDATE OF t FOR SHARE OF u
                    """, connection, transaction))
                {
                    scope.Parameters.AddWithValue("task", taskId);
                    scope.Parameters.AddWithValue("actor", actor);
                    scope.Parameters.AddWithValue("operation", input.Id);
                    await using var reader = await scope.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct)) return Results.NotFound();
                    if (reader.GetBoolean(1)) return Results.Forbid();
                    if (reader.GetString(0) != "IN_PROGRESS" && !reader.GetBoolean(2))
                        return Results.BadRequest();
                }

                await using (var checkCap = new NpgsqlCommand("SELECT 1 FROM kiosk_ops.slot_sensor_capabilities WHERE slot_id = @slot AND enabled = true", connection, transaction))
                {
                    checkCap.Parameters.AddWithValue("slot", input.SlotId);
                    if (await checkCap.ExecuteScalarAsync(ct) is not null)
                        return Results.Problem(statusCode: 409, detail: "Ô đã kích hoạt cảm biến giám sát. Hãy thực hiện quy trình nạp có giám sát cảm biến.");
                }
                var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
                await using var command = new NpgsqlCommand("SELECT flow.staff_stock_bouquet(@id,@task,@actor,@product,@slot,@qr)", connection, transaction);
                command.Parameters.AddWithValue("id", input.Id); command.Parameters.AddWithValue("task", taskId);
                command.Parameters.AddWithValue("actor", actor);
                command.Parameters.AddWithValue("product", input.ProductId); command.Parameters.AddWithValue("slot", input.SlotId); command.Parameters.AddWithValue("qr", input.QrCode.Trim());
                var bouquet = (Guid)(await command.ExecuteScalarAsync(ct))!;
                await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
                await outbox.CommitAsync(ct);
                return Results.Ok(new StaffStockResult(input.Id, bouquet));
            }
            catch (PostgresException ex) when (ex.SqlState is "42501" or "P0002" or "40001" or "22023" or "23505" or "23514" or "23503")
            {
                return ex.SqlState switch
                {
                    "42501" => Results.Forbid(),
                    "P0002" => Results.NotFound(),
                    "40001" or "23505" => Results.Problem(statusCode: 409, detail: "Mã thao tác hoặc mã bó hoa đã được sử dụng. Tải lại để kiểm tra kết quả trước khi tiếp tục."),
                    _ => Results.Problem(statusCode: 400, detail: "Công việc, shop, sản phẩm hoặc ô tủ hiện không cho phép nạp hoa. Tải lại các lựa chọn.")
                };
            }
        }).RequireAuthorization("SellerStaff").Produces<StaffStockResult>()
            .Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> Read(Guid taskId, NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        Guid seller, kiosk; string status;
        await using (var command = data.CreateCommand("SELECT seller_id,kiosk_id,status FROM kiosk_ops.staff_tasks WHERE id=@id AND assignee_id=@actor AND kind='DELIVERY'"))
        {
            command.Parameters.AddWithValue("id", taskId); command.Parameters.AddWithValue("actor", Guid.Parse(http.User.FindFirstValue("sub")!));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            seller = reader.GetGuid(0); kiosk = reader.GetGuid(1); status = reader.GetString(2);
        }
        var sellerClaim = http.User.FindFirstValue("seller_id");
        if (sellerClaim is null || !Guid.TryParse(sellerClaim, out var staffSeller) || staffSeller != seller) return Results.NotFound();
        var slots = new List<StaffStockSlot>();
        if (status == "IN_PROGRESS")
        {
            await using var command = data.CreateCommand("SELECT s.id,s.slot_code,s.zone FROM kiosk_ops.slots s JOIN kiosk_ops.kiosks k ON k.id=s.kiosk_id WHERE s.kiosk_id=@kiosk AND s.current_seller_id=@seller AND s.status='RENTED_EMPTY' AND s.zone='WALK_IN' AND k.status NOT IN ('DISABLED','MAINTENANCE') AND flow.slot_sellable(s.id,@seller) ORDER BY s.slot_code,s.id");
            command.Parameters.AddWithValue("kiosk", kiosk); command.Parameters.AddWithValue("seller", seller);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) slots.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        var actions = new List<StaffStockAction>();
        await using (var command = data.CreateCommand("SELECT id,product_id,slot_id,qr_code,bouquet_id,created_at FROM kiosk_ops.staff_stock_actions WHERE task_id=@id ORDER BY created_at DESC,id DESC"))
        {
            command.Parameters.AddWithValue("id", taskId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) actions.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetGuid(4), reader.GetDateTime(5)));
        }
        return Results.Ok(new StaffStockOptions(status == "IN_PROGRESS" ? await StaffStockProducts.Read(data, seller, ct) : [], slots, actions));
    }
}
