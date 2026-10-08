using Npgsql;

namespace FloraBot.Api.ReadModels;

public sealed record WebCatalogItem(Guid ProductId, Guid SellerId, string ShopName, string Name, string? Description, long Price, Guid KioskId, string KioskName, string Address, string? Photo, Guid[] Bouquets);
public sealed record WebCatalogPage(List<WebCatalogItem> Items, int Page, bool HasMore);
public static class WebCatalog
{
    public static void MapWebCatalog(this WebApplication app)
    {
        app.MapGet("/api/shop/catalog", async (int? page, string? search, NpgsqlDataSource source, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var number = page ?? 1; if (number is < 1 or > 100000 || search?.Length > 100) return Results.BadRequest();
            await using var command = source.CreateCommand("SELECT product_id,seller_id,shop_name,name,description,price,kiosk_id,kiosk_name,address,photo,bouquets FROM screen.v_web_catalog WHERE name ILIKE @search OR shop_name ILIKE @search OR kiosk_name ILIKE @search ORDER BY name,product_id,kiosk_id LIMIT 26 OFFSET @offset");
            command.Parameters.AddWithValue("search", "%" + (search?.Trim() ?? "") + "%"); command.Parameters.AddWithValue("offset", (number - 1) * 25);
            await using var reader = await command.ExecuteReaderAsync(ct); var items = new List<WebCatalogItem>();
            while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.GetGuid(6), reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetFieldValue<Guid[]>(10)));
            return Results.Ok(new WebCatalogPage(items.Take(25).ToList(), number, items.Count > 25));
        }).AllowAnonymous().Produces<WebCatalogPage>();
    }
}
