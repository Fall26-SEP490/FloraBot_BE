using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record SellerAccessoryResponse(Guid Id, string Name, long Price, int StockQuantity, string Status,
    Guid KioskId, string KioskCode, string KioskName, string KioskAddress, string KioskStatus);
public sealed record CreateSellerAccessory(Guid KioskId, string Name, long Price);
public sealed record UpdateSellerAccessory(string Name, long Price);
public sealed record ChangeAccessoryStatus(string Status);

public static class SellerAccessories
{
    public static void MapSellerAccessories(this WebApplication app)
    {
        app.MapGet("/api/sellers/{sellerId:guid}/accessories", async (Guid sellerId, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await db.Accessories.AsNoTracking().Where(x => x.SellerId == sellerId)
                .OrderBy(x => x.Kiosk.Code).ThenBy(x => x.Name).ThenBy(x => x.Id)
                .Select(x => new SellerAccessoryResponse(x.Id, x.Name, x.Price, x.StockQuantity, x.Status,
                    x.KioskId, x.Kiosk.Code, x.Kiosk.Name, x.Kiosk.Address, x.Kiosk.Status)).ToListAsync(ct));
        }).RequireAuthorization("Merchant", "SameSeller").WithTags("KioskOps").Produces<List<SellerAccessoryResponse>>();

        app.MapGet("/api/sellers/{sellerId:guid}/accessories/{id:guid}", async (
            Guid sellerId, Guid id, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var item = await db.Accessories.AsNoTracking().Where(x => x.SellerId == sellerId && x.Id == id)
                .Select(x => new SellerAccessoryResponse(x.Id, x.Name, x.Price, x.StockQuantity, x.Status,
                    x.KioskId, x.Kiosk.Code, x.Kiosk.Name, x.Kiosk.Address, x.Kiosk.Status)).SingleOrDefaultAsync(ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        }).RequireAuthorization("Seller", "SameSeller").WithTags("KioskOps").Produces<SellerAccessoryResponse>();

        app.MapPost("/api/sellers/{sellerId:guid}/accessories", async (
            Guid sellerId, CreateSellerAccessory input, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            if (!Valid(input.Name, input.Price) || input.KioskId == Guid.Empty) return Results.BadRequest();
            var actor = Actor(http);
            if (actor == Guid.Empty) return Results.Unauthorized();
            await using var conn = await data.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            if (!await ActiveSeller(conn, tx, actor, sellerId, ct)) return Results.Forbid();
            if (!await OwnsKiosk(conn, tx, sellerId, input.KioskId, ct)) return Results.NotFound();
            var id = Guid.CreateVersion7();
            try
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO kiosk_ops.accessories(id,seller_id,kiosk_id,name,price,stock_quantity,status)
                    VALUES(@id,@seller,@kiosk,@name,@price,0,'ACTIVE')
                    """, conn, tx);
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("seller", sellerId);
                insert.Parameters.AddWithValue("kiosk", input.KioskId);
                insert.Parameters.AddWithValue("name", input.Name.Trim());
                insert.Parameters.AddWithValue("price", input.Price);
                await insert.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            { return Results.Problem(statusCode: 409, detail: "Kiosk đã có phụ kiện cùng tên."); }
            await Audit(conn, tx, actor, id, "ACCESSORY_CREATED", ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/sellers/{sellerId}/accessories/{id}", new { id });
        }).RequireAuthorization("Seller", "SameSeller").WithTags("KioskOps");

        app.MapPut("/api/sellers/{sellerId:guid}/accessories/{id:guid}", async (
            Guid sellerId, Guid id, UpdateSellerAccessory input, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
            await Change(sellerId, id, input.Name, input.Price, null, data, http, ct))
            .RequireAuthorization("Seller", "SameSeller").WithTags("KioskOps");

        app.MapPatch("/api/sellers/{sellerId:guid}/accessories/{id:guid}/status", async (
            Guid sellerId, Guid id, ChangeAccessoryStatus input, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
            await Change(sellerId, id, null, null, input.Status, data, http, ct))
            .RequireAuthorization("Seller", "SameSeller").WithTags("KioskOps");
    }

    private static Guid Actor(HttpContext http) => Guid.TryParse(http.User.FindFirstValue("sub"), out var id) ? id : Guid.Empty;
    private static bool Valid(string name, long price) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length is >= 2 and <= 120 &&
        !name.Any(char.IsControl) && price > 0;

    private static async Task<bool> ActiveSeller(NpgsqlConnection conn, NpgsqlTransaction tx, Guid actor, Guid seller, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT 1 FROM identity.users u JOIN identity.sellers s ON s.id=u.seller_id
            WHERE u.id=@actor AND u.role='SELLER' AND u.status='ACTIVE'
              AND s.id=@seller AND s.status='ACTIVE' FOR SHARE OF u,s
            """, conn, tx);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("seller", seller);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> OwnsKiosk(NpgsqlConnection conn, NpgsqlTransaction tx, Guid seller, Guid kiosk, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT 1 FROM kiosk_ops.kiosks k
            WHERE k.id=@kiosk AND k.seller_id=@seller AND k.status<>'DISABLED'
              AND flow.kiosk_package_valid(k.id)
            FOR UPDATE
            """, conn, tx);
        cmd.Parameters.AddWithValue("kiosk", kiosk);
        cmd.Parameters.AddWithValue("seller", seller);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<IResult> Change(Guid sellerId, Guid id, string? name, long? price, string? status,
        NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        if (status is null ? !Valid(name!, price.GetValueOrDefault()) : status is not ("ACTIVE" or "INACTIVE"))
            return Results.BadRequest();
        var actor = Actor(http);
        if (actor == Guid.Empty) return Results.Unauthorized();
        await using var conn = await data.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await ActiveSeller(conn, tx, actor, sellerId, ct)) return Results.Forbid();
        Guid kioskId;
        await using (var lookup = new NpgsqlCommand("SELECT kiosk_id FROM kiosk_ops.accessories WHERE id=@id AND seller_id=@seller", conn, tx))
        {
            lookup.Parameters.AddWithValue("id", id);
            lookup.Parameters.AddWithValue("seller", sellerId);
            var value = await lookup.ExecuteScalarAsync(ct);
            if (value is not Guid parsed) return Results.NotFound();
            kioskId = parsed;
        }
        if (!await OwnsKiosk(conn, tx, sellerId, kioskId, ct)) return Results.Problem(statusCode: 409, detail: "Kiosk chưa có kỳ thuê hợp lệ.");
        try
        {
            await using var update = new NpgsqlCommand(status is null ? """
                UPDATE kiosk_ops.accessories SET name=@name,price=@price,updated_at=public.app_now()
                WHERE id=@id AND seller_id=@seller
                """ : """
                UPDATE kiosk_ops.accessories SET status=@status,updated_at=public.app_now()
                WHERE id=@id AND seller_id=@seller
                """, conn, tx);
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("seller", sellerId);
            if (status is null)
            {
                update.Parameters.AddWithValue("name", name!.Trim());
                update.Parameters.AddWithValue("price", price!.Value);
            }
            else update.Parameters.AddWithValue("status", status);
            await update.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        { return Results.Problem(statusCode: 409, detail: "Kiosk đã có phụ kiện cùng tên."); }
        await Audit(conn, tx, actor, id, status is null ? "ACCESSORY_UPDATED" : "ACCESSORY_STATUS_CHANGED", ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task Audit(NpgsqlConnection conn, NpgsqlTransaction tx, Guid actor, Guid id, string action, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT flow.audit(@actor,@action,'accessories',@id)", conn, tx);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
