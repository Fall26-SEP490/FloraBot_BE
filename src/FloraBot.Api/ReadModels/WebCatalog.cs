using Npgsql;

namespace FloraBot.Api.ReadModels;

public sealed record WebCatalogItem(Guid ProductId, Guid SellerId, string ShopName, string Name, string? Description, long Price, Guid KioskId, string KioskName, string Address, string? Zone, string? Photo, Guid[] Bouquets);
public sealed record WebCatalogPage(List<WebCatalogItem> Items, int Page, bool HasMore);
public static class WebCatalog
{
    public static void MapWebCatalog(this WebApplication app)
    {
        app.MapGet("/api/shop/catalog", async (int? page, string? search, NpgsqlDataSource source, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var number = page ?? 1; if (number is < 1 or > 100000 || search?.Length > 100) return Results.BadRequest();
            await using var command = source.CreateCommand("SELECT v.product_id,v.seller_id,v.shop_name,v.name,v.description,v.price,v.kiosk_id,v.kiosk_name,v.address,(SELECT CASE WHEN bool_or(sl.zone = 'PREORDER') THEN 'PREORDER' ELSE NULL END FROM kiosk_ops.slots sl WHERE sl.kiosk_id=v.kiosk_id AND sl.current_seller_id=v.seller_id AND flow.slot_sellable(sl.id,v.seller_id) AND (sl.zone IS NULL OR sl.zone='PREORDER')),v.photo,v.bouquets FROM screen.v_web_catalog v WHERE v.name ILIKE @search OR v.shop_name ILIKE @search OR v.kiosk_name ILIKE @search ORDER BY v.name,v.product_id,v.kiosk_id LIMIT 26 OFFSET @offset");
            command.Parameters.AddWithValue("search", "%" + (search?.Trim() ?? "") + "%"); command.Parameters.AddWithValue("offset", (number - 1) * 25);
            await using var reader = await command.ExecuteReaderAsync(ct); var items = new List<WebCatalogItem>();
            while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.GetGuid(6), reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetFieldValue<Guid[]>(11)));
            return Results.Ok(new WebCatalogPage(items.Take(25).ToList(), number, items.Count > 25));
        }).AllowAnonymous().Produces<WebCatalogPage>();
    }
}
