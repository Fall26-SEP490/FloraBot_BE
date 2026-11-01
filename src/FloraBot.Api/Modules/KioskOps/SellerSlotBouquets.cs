using System.Security.Claims;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record RemoveSlotBouquetRequest(Guid BouquetId, string Reason, bool ConfirmedRemoved);
public sealed record RemoveSlotBouquetResponse(Guid KioskId, Guid SlotId, string Status,
    Guid? BouquetId, Guid RemovedBouquetId, string? Zone);

public static class SellerSlotBouquets
{
    public static void MapSellerSlotBouquets(this WebApplication app)
    {
        app.MapDelete("/api/me/kiosks/{kioskId:guid}/slots/{slotId:guid}/bouquet", async (
            Guid kioskId, Guid slotId, [FromBody] RemoveSlotBouquetRequest input,
            HttpContext http, NpgsqlDataSource data, ICapPublisher publisher, CancellationToken ct) =>
        {
            if (input.BouquetId == Guid.Empty || !input.ConfirmedRemoved ||
                string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length is < 5 or > 500)
                return Results.Problem(statusCode: 400,
                    detail: "Cần bouquetId, xác nhận đã lấy hoa khỏi ô và lý do từ 5 đến 500 ký tự.");

            var actor = Guid.Parse(http.User.FindFirstValue("sub")!);
            await using var connection = await data.OpenConnectionAsync(ct);
            using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
            var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            try
            {
                await using var command = new NpgsqlCommand("""
                    SET LOCAL lock_timeout = '5s';
                    SELECT flow.seller_remove_slot_bouquet(@kiosk,@slot,@actor,@bouquet,@reason,@confirmed);
                    """, connection, transaction);
                command.Parameters.AddWithValue("kiosk", kioskId);
                command.Parameters.AddWithValue("slot", slotId);
                command.Parameters.AddWithValue("actor", actor);
                command.Parameters.AddWithValue("bouquet", input.BouquetId);
                command.Parameters.AddWithValue("reason", input.Reason.Trim());
                command.Parameters.AddWithValue("confirmed", input.ConfirmedRemoved);
                var status = (string)(await command.ExecuteScalarAsync(ct))!;
                await using var read = new NpgsqlCommand("SELECT zone FROM kiosk_ops.slots WHERE id=@slot", connection, transaction);
                read.Parameters.AddWithValue("slot", slotId);
                var zone = await read.ExecuteScalarAsync(ct);
                await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
                await outbox.CommitAsync(ct);
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new RemoveSlotBouquetResponse(kioskId, slotId, status, null,
                    input.BouquetId, zone is string value ? value : null));
            }
            catch (PostgresException ex) when (ex.SqlState is "42501" or "P0002" or "22023" or "55P03" or "40P01")
            {
                return ex.SqlState switch
                {
                    "42501" => Results.Forbid(),
                    "P0002" => Results.NotFound(),
                    "22023" => Results.Problem(statusCode: 409, detail: ex.MessageText),
                    _ => Results.Problem(statusCode: 409, detail: "Ô đang được xử lý đồng thời. Hãy tải lại trạng thái trước khi thử lại.")
                };
            }
        })
        .RequireAuthorization("Seller")
        .WithTags("KioskOps")
        .WithSummary("Seller thu hồi bó hoa khỏi ô để nạp hoa mới")
        .WithDescription("Chỉ STOCKED/PENDING_REMOVAL chưa giữ cho đơn. Xác nhận đã lấy hoa thật khỏi ô. Giữ zone, trả RENTED_EMPTY; không gửi lệnh mở cửa. bouquetId giúp chặn thao tác từ dữ liệu ô đã cũ.")
        .Produces<RemoveSlotBouquetResponse>()
        .ProducesProblem(400).ProducesProblem(409)
        .Produces(401).Produces(403).Produces(404);
    }
}
