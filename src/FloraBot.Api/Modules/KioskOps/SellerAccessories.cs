using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record SellerAccessoryResponse(Guid Id, string Name, long Price, int StockQuantity, string Status,
    Guid KioskId, string KioskCode, string KioskName, string KioskAddress, string KioskStatus);

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
    }
}
