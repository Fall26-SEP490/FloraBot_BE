using System.Security.Claims;
using System.Text.Json;
using DotNetCore.CAP;
using FloraBot.Api.Data;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Notify;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Ordering;

public sealed record StaffPreorderCandidate(Guid RequestId, Guid OrderId, string OrderCode, Guid ProductId, string ProductName, long Amount, DateTimeOffset PickupAt, DateTimeOffset PickupBefore);
public sealed record StaffPreorderSlot(Guid Id, string SlotCode, string? Zone);
public sealed record StaffPreorderOptions(List<StaffPreorderCandidate> Preorders, List<StaffPreorderSlot> Slots);
public sealed record AssignStaffPreorderInput(Guid Id, Guid AssigneeId, Guid SlotId, string Instructions);
public sealed record StaffPreorderFulfillInput(Guid SlotId, string QrCode, Guid? LoadingAccessId = null);
public sealed record StaffPreorderFulfillResult(Guid RequestId, Guid BouquetId);

public static class StaffPreorders
{
    public static void MapStaffPreorders(this WebApplication app)
    {
        app.MapPost("/api/sellers/{sellerId:guid}/preorders/{requestId:guid}/assignment", AssignAsync)
            .RequireAuthorization("Seller", "SameSeller")
            .WithTags("Ordering")
            .WithSummary("Giao đơn đã thanh toán và ô PREORDER cụ thể cho nhân viên của shop")
            .Produces<StaffTaskResult>();

        app.MapGet("/api/staff/tasks/{taskId:guid}/preorders", Read)
            .RequireAuthorization("SellerStaff")
            .WithTags("Ordering")
            .WithSummary("Đơn đặt trước đã thanh toán có thể giao từ công việc này")
            .Produces<StaffPreorderOptions>();

        app.MapPost("/api/staff/tasks/{taskId:guid}/preorders/{requestId:guid}/fulfill", async (
            Guid taskId,
            Guid requestId,
            StaffPreorderFulfillInput input,
            NpgsqlDataSource data,
            ICapPublisher publisher,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (input.SlotId == Guid.Empty || string.IsNullOrWhiteSpace(input.QrCode) ||
                input.QrCode.Trim().Length > 120 || input.QrCode.Any(char.IsControl))
                return Results.BadRequest();

            return await ConfirmLoadingAsync(data, publisher, requestId, input.SlotId, Actor(http), taskId,
                input.LoadingAccessId, input.QrCode.Trim(), ct);
        })
            .RequireAuthorization("SellerStaff")
            .WithTags("Ordering")
            .WithSummary("Giao đơn preorder đã thanh toán vào ô PREORDER trong công việc được phân công")
            .Produces<StaffPreorderFulfillResult>();
    }

    internal static async Task<IResult> ConfirmLoadingAsync(NpgsqlDataSource data, ICapPublisher publisher,
        Guid requestId, Guid slotId, Guid actor, Guid? task, Guid? challenge, string qr, CancellationToken ct)
    {
        if (challenge is null || challenge == Guid.Empty) return Results.NotFound();
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        try
        {
            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            await using var command = new NpgsqlCommand(
                "SELECT flow.complete_preorder_loading(@challenge,@request,@slot,@actor,@task,@qr)",
                connection, transaction);
            command.Parameters.AddWithValue("challenge", challenge.Value);
            command.Parameters.AddWithValue("request", requestId);
            command.Parameters.AddWithValue("slot", slotId);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("task", NpgsqlDbType.Uuid, (object?)task ?? DBNull.Value);
            command.Parameters.AddWithValue("qr", qr);
            if (await command.ExecuteScalarAsync(ct) is not Guid bouquetId) return Results.NotFound();
            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);
            return task is null
                ? Results.Ok(new FlowResult(JsonSerializer.SerializeToElement(bouquetId)))
                : Results.Ok(new StaffPreorderFulfillResult(requestId, bouquetId));
        }
        catch (PostgresException ex) when (ex.SqlState == "22023")
        {
            return Results.Problem(statusCode: 409, detail: "Chưa có tín hiệu có hoa hợp lệ sau khi mở ô, hoặc lượt nạp đã hết hạn.");
        }
        catch (PostgresException ex) when (Handled(ex) || ex.SqlState == "40P01")
        {
            return ex.SqlState == "40P01" ? Results.Conflict() : Problem(ex);
        }
    }

    private static async Task<IResult> AssignAsync(Guid sellerId, Guid requestId, AssignStaffPreorderInput input,
        NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        if (requestId == Guid.Empty || input.Id == Guid.Empty || input.AssigneeId == Guid.Empty
            || input.SlotId == Guid.Empty || input.Instructions?.Trim().Length is not (>= 10 and <= 2000))
            return Results.BadRequest();
        if (!await WebOrders.Owns(data, requestId, null, sellerId, ct)) return Results.NotFound();

        await using var command = data.CreateCommand(
            "SELECT flow.assign_preorder_staff_task(@id,@actor,@staff,@request,@slot,@instructions)");
        command.Parameters.AddWithValue("id", input.Id);
        command.Parameters.AddWithValue("actor", Actor(http));
        command.Parameters.AddWithValue("staff", input.AssigneeId);
        command.Parameters.AddWithValue("request", requestId);
        command.Parameters.AddWithValue("slot", input.SlotId);
        command.Parameters.AddWithValue("instructions", input.Instructions.Trim());
        try
        {
            return await command.ExecuteScalarAsync(ct) is Guid taskId
                ? Results.Ok(new StaffTaskResult(taskId))
                : Results.NotFound();
        }
        catch (PostgresException ex) when (Handled(ex) || ex.SqlState == "40P01")
        {
            return ex.SqlState == "40P01" ? Results.Conflict() : Problem(ex);
        }
    }

    private static async Task<IResult> Read(Guid taskId, NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = Actor(http);
        var sellerClaim = http.User.FindFirstValue("seller_id");
        if (!Guid.TryParse(sellerClaim, out var sellerFromToken)) return Results.NotFound();

        Guid sellerId;
        Guid kioskId;
        string status;
        await using (var task = data.CreateCommand("""
            SELECT t.seller_id,t.kiosk_id,t.status
            FROM kiosk_ops.staff_tasks t
            JOIN identity.users u ON u.id=t.assignee_id AND u.seller_id=t.seller_id
                AND u.role='SELLER_STAFF' AND u.status='ACTIVE'
            WHERE t.id=@task AND t.assignee_id=@actor AND t.seller_id=@seller AND t.kind='DELIVERY'
              AND NOT EXISTS (SELECT 1 FROM kiosk_ops.staff_inventory_tasks WHERE task_id=@task)
            """))
        {
            task.Parameters.AddWithValue("task", taskId);
            task.Parameters.AddWithValue("actor", actor);
            task.Parameters.AddWithValue("seller", sellerFromToken);
            await using var reader = await task.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            sellerId = reader.GetGuid(0);
            kioskId = reader.GetGuid(1);
            status = reader.GetString(2);
        }

        var slots = new List<StaffPreorderSlot>();
        var preorders = new List<StaffPreorderCandidate>();
        if (status == "IN_PROGRESS")
        {
            await using (var command = data.CreateCommand("""
                SELECT s.id,s.slot_code,s.zone
                FROM kiosk_ops.slots s
                JOIN kiosk_ops.kiosks k ON k.id=s.kiosk_id
                WHERE s.kiosk_id=@kiosk AND s.current_seller_id=@seller AND s.status='RENTED_EMPTY'
                  AND s.zone='PREORDER'
                  AND k.status NOT IN ('DISABLED','MAINTENANCE')
                  AND flow.slot_sellable(s.id,@seller)
                  AND NOT EXISTS (
                      SELECT 1 FROM kiosk_ops.staff_preorder_tasks scope
                      WHERE scope.task_id=@task AND scope.slot_id<>s.id
                  )
                ORDER BY s.slot_code,s.id
                """))
            {
                command.Parameters.AddWithValue("kiosk", kioskId);
                command.Parameters.AddWithValue("seller", sellerId);
                command.Parameters.AddWithValue("task", taskId);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    slots.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            await using (var command = data.CreateCommand("""
                SELECT r.id,o.id,o.order_code,r.product_id,p.name,o.total_amount,r.pickup_at,r.pickup_before
                FROM ordering.web_requests r
                JOIN ordering.orders o ON o.id=r.order_id
                JOIN catalog.flower_products p ON p.id=r.product_id
                WHERE r.seller_id=@seller AND r.kiosk_id=@kiosk AND r.kind='CUSTOM' AND r.state='ORDERED'
                  AND o.status='PAID'
                  AND public.app_now() < r.pickup_before
                  AND NOT EXISTS (SELECT 1 FROM ordering.order_items i WHERE i.order_id=o.id AND i.item_type='BOUQUET')
                  AND NOT EXISTS (
                      SELECT 1 FROM kiosk_ops.staff_preorder_tasks scope
                      WHERE scope.task_id=@task AND scope.request_id<>r.id
                  )
                ORDER BY r.pickup_at,r.id
                """))
            {
                command.Parameters.AddWithValue("seller", sellerId);
                command.Parameters.AddWithValue("task", taskId);
                command.Parameters.AddWithValue("kiosk", kioskId);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    preorders.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3),
                        reader.GetString(4), reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6),
                        reader.GetFieldValue<DateTimeOffset>(7)));
            }
        }

        return Results.Ok(new StaffPreorderOptions(preorders, slots));
    }

    private static Guid Actor(HttpContext http) => Guid.Parse(http.User.FindFirstValue("sub")!);
    private static bool Handled(PostgresException ex) => ex.SqlState is "42501" or "P0002" or "40001" or "22023" or "23505" or "23514" or "23503";
    private static IResult Problem(PostgresException ex) => ex.SqlState switch
    {
        "42501" => Results.Forbid(),
        "P0002" => Results.NotFound(),
        "40001" or "23505" => Results.Problem(statusCode: 409, detail: "Đơn hoặc mã bó hoa đã được xử lý. Tải lại công việc để kiểm tra trước khi thử lại."),
        "22023" => Results.Problem(statusCode: 409, detail: "Công việc, đơn thanh toán hoặc ô PREORDER hiện không cho phép thao tác này."),
        _ => Results.Problem(statusCode: 400, detail: "Đơn phải đã thanh toán; chọn ô PREORDER trống tại đúng kiosk trong thời gian nhận hàng.")
    };
}
