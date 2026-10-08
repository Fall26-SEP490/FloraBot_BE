using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.KioskOps;

public static class KioskOpsReadEndpoints
{
    public static void MapKioskOpsReads(this WebApplication app)
    {
        app.MapGet("/api/kiosks", async (FloraDbContext db) => await db.Kiosks.AsNoTracking()
            .Where(x => x.Status != "DISABLED").Select(x => new { x.Id, x.Code, x.Name, x.Address, x.Region, x.Status }).ToListAsync()).AllowAnonymous();
        app.MapGet("/api/sellers/{sellerId:guid}/slots", async (Guid sellerId, FloraDbContext db, HttpContext http) =>
        {
            if (!SameSeller(http, sellerId)) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await db.Slots.AsNoTracking().Where(x => x.CurrentSellerId == sellerId).OrderBy(x => x.Kiosk.Code).ThenBy(x => x.SlotCode)
                .Select(x => new SellerSlotResponse(x.Id, x.KioskId, x.SlotCode, x.Status, x.BouquetId, x.Kiosk.Code, x.Kiosk.Name, x.Kiosk.Address,
                    x.Bouquet == null ? null : x.Bouquet.ProductId, x.Bouquet == null ? null : x.Bouquet.QrCode,
                    x.Bouquet == null ? null : x.Bouquet.PriceSnapshot, x.Bouquet == null ? null : x.Bouquet.SellableUntil)).ToListAsync());
        }).RequireAuthorization("Merchant").Produces<List<SellerSlotResponse>>();
        app.MapGet("/api/admin/slots", async (FloraDbContext db, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await db.Slots.AsNoTracking().OrderBy(x => x.Kiosk.Code).ThenBy(x => x.SlotCode)
                .Select(x => new AdminSlotResponse(x.Id, x.KioskId, x.SlotCode, x.Status, x.CurrentSellerId, x.BouquetId,
                    x.Kiosk.Code, x.Kiosk.Name, x.Kiosk.Address, x.Kiosk.Status)).ToListAsync();
        }).RequireAuthorization("Admin").Produces<List<AdminSlotResponse>>();
    }
    private static bool SameSeller(HttpContext http, Guid id) => http.User.IsInRole("ADMIN") || http.User.FindFirstValue("seller_id") == id.ToString();
}
