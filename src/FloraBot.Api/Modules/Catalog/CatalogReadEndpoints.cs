using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Catalog;

public static class CatalogReadEndpoints
{
    public static void MapCatalogReads(this WebApplication app)
    {
        app.MapGet("/api/sellers/{sellerId:guid}/products", async (Guid sellerId, FloraDbContext db, HttpContext http) =>
        {
            if (!SameSeller(http, sellerId)) return Results.NotFound();
            return Results.Ok(await db.FlowerProducts.AsNoTracking().Where(x => x.SellerId == sellerId).OrderBy(x => x.Name)
                .Select(x => new ProductResponse(x.Id, x.Name, x.Description, x.Price, x.Tags, x.Status, x.InventoryKind == "ARTIFICIAL" ? null : x.ShelfLifeHours, x.InventoryKind, x.LengthCm, x.WidthCm, x.HeightCm)).ToListAsync());
        }).RequireAuthorization("Merchant").Produces<List<ProductResponse>>();
    }
    private static bool SameSeller(HttpContext http, Guid id) => http.User.IsInRole("ADMIN") || http.User.FindFirstValue("seller_id") == id.ToString();
}
