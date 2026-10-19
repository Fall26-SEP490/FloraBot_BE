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
            .Where(x => x.Status != "DISABLED").Select(x => new
            {
                x.Id,
                x.Code,
                x.Name,
                x.Address,
                x.Region,
                x.Status,
                x.Latitude,
                x.Longitude,
                LocationComplete = x.Latitude != null && x.Longitude != null && x.Address != null && x.Address.Trim() != ""
            }).ToListAsync()).AllowAnonymous();
        app.MapGet("/api/me/kiosks/{kioskId:guid}/slots", async (Guid kioskId, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (!Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId)) return Results.Forbid();
            var kiosk = await db.Kiosks.AsNoTracking().Where(x => x.Id == kioskId && x.SellerId == sellerId)
                .Select(x => new { x.Code, x.Name, x.Address }).SingleOrDefaultAsync(ct);
            if (kiosk is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            // The global seller filter uses Slot.CurrentSellerId, which is null for FREE slots.
            // Ownership was checked above against Kiosk.SellerId, so scope this unfiltered slot read by kiosk.
            var slots = await db.Slots.IgnoreQueryFilters().AsNoTracking().Where(x => x.KioskId == kioskId)
                .OrderBy(x => x.SlotCode).ThenBy(x => x.Id)
                .Select(x => new { x.Id, x.KioskId, x.SlotCode, x.Status, x.BouquetId }).ToListAsync(ct);
            var bouquetIds = slots.Where(x => x.BouquetId.HasValue).Select(x => x.BouquetId!.Value).ToArray();
            var bouquets = await db.Bouquets.AsNoTracking().Where(x => bouquetIds.Contains(x.Id))
                .Select(x => new { x.Id, x.ProductId, x.QrCode, x.PriceSnapshot, x.SellableUntil })
                .ToDictionaryAsync(x => x.Id, ct);
            return Results.Ok(slots.Select(slot =>
            {
                var bouquet = slot.BouquetId.HasValue ? bouquets.GetValueOrDefault(slot.BouquetId.Value) : null;
                return new SellerSlotResponse(slot.Id, slot.KioskId, slot.SlotCode, slot.Status, slot.BouquetId,
                    kiosk.Code, kiosk.Name, kiosk.Address, bouquet?.ProductId, bouquet?.QrCode,
                    bouquet?.PriceSnapshot, bouquet?.SellableUntil);
            }).ToList());
        })
        .RequireAuthorization("Seller")
        .WithTags("KioskOps")
        .WithSummary("Danh sách các ô trong kiosk đang được Seller đăng nhập quản lý")
        .Produces<List<SellerSlotResponse>>();
        app.MapGet("/api/admin/kiosks", async (FloraDbContext db, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await db.Kiosks.AsNoTracking().OrderBy(x => x.Code)
                .Select(x => new AdminKioskResponse(x.Id, x.Code, x.Name, x.Status, x.SellerId,
                    db.Sellers.Where(s => s.Id == x.SellerId).Select(s => s.ShopName).FirstOrDefault(), x.Slots.Count))
                .ToListAsync());
        }).RequireAuthorization("Admin").Produces<List<AdminKioskResponse>>();
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

public sealed record AdminKioskResponse(Guid Id, string Code, string Name, string Status, Guid? SellerId, string? SellerName, int SlotCount);
