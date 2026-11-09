using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Catalog;

public static class CatalogReadEndpoints
{
    public static void MapCatalogReads(this WebApplication app)
    {
        app.MapGet("/api/me/products", async (FloraDbContext db, HttpContext http) =>
        {
            if (!Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId)) return Results.Forbid();
            return Results.Ok(await db.FlowerProducts.AsNoTracking().Where(x => x.SellerId == sellerId).OrderBy(x => x.Name)
                .Select(x => new ProductResponse(x.Id, x.Name, x.Description, x.Price, x.Tags, x.Status, x.InventoryKind == "ARTIFICIAL" ? null : x.ShelfLifeHours, x.InventoryKind, x.LengthCm, x.WidthCm, x.HeightCm, x.Quantity)).ToListAsync());
        }).RequireAuthorization("Seller").Produces<List<ProductResponse>>()
          .WithSummary("Danh sách mẫu hoa và tồn bó tại shop")
          .WithDescription("quantity là số bó còn tại shop; không bao gồm các bó đã nạp trong kiosk và không được tính từ số ô tủ.");
    }
}
