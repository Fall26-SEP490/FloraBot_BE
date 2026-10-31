using System.Security.Claims;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record SellerSetSlotZoneRequest(string Zone);
public sealed record SellerSlotZoneResponse(Guid KioskId, Guid SlotId, string Zone);

public static class SellerSlotZones
{
    public static void MapSellerSlotZones(this WebApplication app)
    {
        app.MapPut("/api/me/kiosks/{kioskId:guid}/slots/{slotId:guid}/zone", async (
            Guid kioskId,
            Guid slotId,
            SellerSetSlotZoneRequest input,
            HttpContext http,
            NpgsqlDataSource data,
            ICapPublisher publisher,
            CancellationToken ct) =>
        {
            var zone = input.Zone?.Trim().ToUpperInvariant();
            if (zone is not ("WALK_IN" or "PREORDER"))
                return Results.Problem(statusCode: 400, detail: "Zone phải là WALK_IN hoặc PREORDER.");

            var actor = Guid.Parse(http.User.FindFirstValue("sub")!);
            await using var connection = await data.OpenConnectionAsync(ct);
            using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
            var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);

            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT flow.seller_set_slot_zone(@kiosk,@slot,@actor,@zone)", connection, transaction);
                command.Parameters.AddWithValue("kiosk", kioskId);
                command.Parameters.AddWithValue("slot", slotId);
                command.Parameters.AddWithValue("actor", actor);
                command.Parameters.AddWithValue("zone", zone);
                await command.ExecuteNonQueryAsync(ct);

                await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
                await outbox.CommitAsync(ct);
                return Results.Ok(new SellerSlotZoneResponse(kioskId, slotId, zone));
            }
            catch (PostgresException ex) when (ex.SqlState is "42501" or "P0002" or "22023")
            {
                return ex.SqlState switch
                {
                    "42501" => Results.Forbid(),
                    "P0002" => Results.NotFound(),
                    _ => Results.Problem(statusCode: 409, detail: ex.MessageText)
                };
            }
        })
        .RequireAuthorization("Seller")
        .WithTags("KioskOps")
        .WithSummary("Seller phân loại ô trống thuộc kiosk mình quản lý")
        .Produces<SellerSlotZoneResponse>();
    }
}
