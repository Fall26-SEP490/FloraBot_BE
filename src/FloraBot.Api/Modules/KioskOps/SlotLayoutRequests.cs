using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record RequestSlotLayout(int WalkInSlots, int PreorderSlots, string Reason);
public sealed record ReviewSlotLayout(Guid[]? PreorderSlotIds, string? Note);
public sealed record SlotLayoutRequestView(Guid Id, Guid KioskId, Guid SellerId, int WalkInSlots,
    int PreorderSlots, string Reason, string Status, DateTime CreatedAt, DateTime? ReviewedAt, string? ReviewNote);

public static class SlotLayoutRequests
{
    public static void MapSlotLayoutRequests(this WebApplication app)
    {
        app.MapPost("/api/me/kiosks/{kioskId:guid}/layout-requests", async (
            Guid kioskId, RequestSlotLayout input, HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
            try
            {
                await using var command = data.CreateCommand(
                    "SELECT flow.request_slot_layout_change(@actor,@kiosk,@walk,@pre,@reason)");
                command.Parameters.AddWithValue("actor", actor);
                command.Parameters.AddWithValue("kiosk", kioskId);
                command.Parameters.AddWithValue("walk", input.WalkInSlots);
                command.Parameters.AddWithValue("pre", input.PreorderSlots);
                command.Parameters.AddWithValue("reason", input.Reason ?? "");
                var id = (Guid)(await command.ExecuteScalarAsync(ct))!;
                return Results.Created($"/api/me/kiosks/{kioskId}/layout-requests", new { id });
            }
            catch (PostgresException ex) { return Error(ex); }
        }).RequireAuthorization("Merchant").WithTags("KioskOps").Produces(201).ProducesProblem(400).Produces(403).ProducesProblem(409);

        app.MapGet("/api/me/kiosks/{kioskId:guid}/layout-requests", async (
            Guid kioskId, HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("seller_id"), out var seller)) return Results.Forbid();
            await using var command = data.CreateCommand("""
                SELECT r.id,r.kiosk_id,r.seller_id,r.walk_in_slots,r.preorder_slots,r.reason,r.status,
                       r.created_at,r.reviewed_at,r.review_note
                FROM kiosk_ops.slot_layout_requests r JOIN kiosk_ops.kiosks k ON k.id=r.kiosk_id
                WHERE r.kiosk_id=@kiosk AND r.seller_id=@seller AND k.seller_id=@seller
                ORDER BY r.created_at DESC LIMIT 100
                """);
            command.Parameters.AddWithValue("kiosk", kioskId);
            command.Parameters.AddWithValue("seller", seller);
            return Results.Ok(await Read(command, ct));
        }).RequireAuthorization("Merchant").WithTags("KioskOps").Produces<List<SlotLayoutRequestView>>();

        app.MapGet("/api/operations/kiosk-layout-requests", async (
            string? status, HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (status is not (null or "PENDING" or "APPROVED" or "REJECTED")) return Results.BadRequest();
            await using var command = data.CreateCommand("""
                SELECT id,kiosk_id,seller_id,walk_in_slots,preorder_slots,reason,status,
                       created_at,reviewed_at,review_note
                FROM kiosk_ops.slot_layout_requests
                WHERE (@status IS NULL OR status=@status) AND status<>'APPLYING'
                ORDER BY created_at DESC LIMIT 100
                """);
            command.Parameters.Add("status", NpgsqlDbType.Text).Value = (object?)status ?? DBNull.Value;
            return Results.Ok(await Read(command, ct));
        }).RequireAuthorization("OperationsManager").WithTags("KioskOps").Produces<List<SlotLayoutRequestView>>();

        app.MapPost("/api/operations/kiosk-layout-requests/{requestId:guid}/review", async (
            Guid requestId, ReviewSlotLayout input, HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
            try
            {
                await using var command = data.CreateCommand(
                    "SELECT flow.review_slot_layout_change(@actor,@request,@slots,@note)");
                command.Parameters.AddWithValue("actor", actor);
                command.Parameters.AddWithValue("request", requestId);
                command.Parameters.Add("slots", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value =
                    (object?)input.PreorderSlotIds ?? DBNull.Value;
                command.Parameters.Add("note", NpgsqlDbType.Text).Value = (object?)input.Note ?? DBNull.Value;
                await command.ExecuteNonQueryAsync(ct);
                return Results.Ok(new { status = input.PreorderSlotIds is null ? "REJECTED" : "APPROVED" });
            }
            catch (PostgresException ex) { return Error(ex); }
        }).RequireAuthorization("OperationsManager").WithTags("KioskOps").Produces(200).ProducesProblem(400).Produces(403).ProducesProblem(409);
    }

    private static async Task<List<SlotLayoutRequestView>> Read(NpgsqlCommand command, CancellationToken ct)
    {
        var list = new List<SlotLayoutRequestView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetString(5), reader.GetString(6), reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetDateTime(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        return list;
    }

    private static IResult Error(PostgresException ex) => ex.SqlState switch
    {
        "42501" => Results.Forbid(),
        "22023" or "23514" => Results.Problem(statusCode: 400, detail: ex.MessageText),
        "55000" => Results.Problem(statusCode: 503, detail: ex.MessageText),
        "23505" or "P0001" => Results.Problem(statusCode: 409, detail: ex.MessageText),
        _ => throw ex
    };
}
