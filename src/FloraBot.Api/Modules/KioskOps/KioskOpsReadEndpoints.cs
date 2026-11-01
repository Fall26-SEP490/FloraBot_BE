using System.Data;
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
            .Where(x => x.Status != "DISABLED").Select(x => new PublicKioskDto(
                x.Id,
                x.Code,
                x.Name,
                x.Address,
                x.Region,
                x.Status,
                x.Latitude,
                x.Longitude,
                x.Latitude != null && x.Longitude != null && x.Address != null && x.Address.Trim() != ""
            )).ToListAsync())
        .AllowAnonymous()
        .WithTags("KioskOps")
        .WithSummary("Danh sách kiosk công khai")
        .Produces<List<PublicKioskDto>>();
        app.MapGet("/api/me/kiosks/{kioskId:guid}/slots", async (Guid kioskId, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (!Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId)) return Results.Forbid();
            // Payment, bouquet and slot reads must describe the same database snapshot.
            await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var kiosk = await db.Kiosks.AsNoTracking().Where(x => x.Id == kioskId && x.SellerId == sellerId)
                .Select(x => new { x.Code, x.Name, x.Address }).SingleOrDefaultAsync(ct);
            if (kiosk is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            // The global seller filter uses Slot.CurrentSellerId, which is null for FREE slots.
            // Ownership was checked above against Kiosk.SellerId, so scope this unfiltered slot read by kiosk.
            var slots = await db.Slots.IgnoreQueryFilters().AsNoTracking().Where(x => x.KioskId == kioskId)
                .OrderBy(x => x.SlotCode).ThenBy(x => x.Id)
                .Select(x => new { x.Id, x.KioskId, x.SlotCode, x.Status, x.Zone, x.BouquetId, x.HoldOrderId }).ToListAsync(ct);
            var bouquetIds = slots.Where(x => x.BouquetId.HasValue).Select(x => x.BouquetId!.Value).ToArray();
            var bouquets = await db.Bouquets.AsNoTracking().Where(x => bouquetIds.Contains(x.Id))
                .Select(x => new { x.Id, x.ProductId, x.QrCode, x.PriceSnapshot, x.SellableUntil, x.Status })
                .ToDictionaryAsync(x => x.Id, ct);
            var orderRows = await db.OrderItems.AsNoTracking()
                .Where(x => x.ItemType == "BOUQUET" && x.BouquetId.HasValue && x.SlotId.HasValue
                    && bouquetIds.Contains(x.BouquetId.Value)
                    && x.Order.SellerId == sellerId && x.Order.KioskId == kioskId)
                .Select(x => new { SlotId = x.SlotId!.Value, BouquetId = x.BouquetId!.Value,
                    x.OrderId, x.Order.CheckoutId, x.Order.OrderCode, x.Order.Status, x.Order.CreatedAt })
                .ToListAsync(ct);
            var orderLookup = orderRows.ToLookup(x => (x.SlotId, x.BouquetId));
            var current = slots.Select(slot =>
            {
                var bouquet = slot.BouquetId.HasValue ? bouquets.GetValueOrDefault(slot.BouquetId.Value) : null;
                var candidates = orderLookup[(slot.Id, slot.BouquetId ?? Guid.Empty)];
                var order = slot.HoldOrderId.HasValue
                    ? candidates.FirstOrDefault(x => x.OrderId == slot.HoldOrderId.Value)
                    : slot.Status is "PENDING_REMOVAL" or "PENDING_RELEASE" or "FAULT" || bouquet?.Status == "SOLD"
                        ? candidates.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.OrderId).FirstOrDefault()
                        : null;
                return new { Slot = slot, Bouquet = bouquet, Order = order };
            }).ToList();
            // Payment has no SellerId filter: query only checkouts selected from this Seller's own orders.
            var checkoutIds = current.Where(x => x.Order is not null).Select(x => x.Order!.CheckoutId).Distinct().ToArray();
            var charges = await db.Payments.AsNoTracking()
                .Where(x => x.Kind == "CHARGE" && x.Purpose == "ORDER_CHECKOUT"
                    && x.CheckoutId.HasValue && checkoutIds.Contains(x.CheckoutId.Value))
                .Select(x => new { CheckoutId = x.CheckoutId!.Value, x.Status })
                .ToDictionaryAsync(x => x.CheckoutId, x => x.Status, ct);
            var response = current.Select(x =>
            {
                var slot = x.Slot;
                var bouquet = x.Bouquet;
                var order = x.Order;
                return new SellerSlotResponse(slot.Id, slot.KioskId, slot.SlotCode, slot.Status, slot.Zone, slot.BouquetId,
                    kiosk.Code, kiosk.Name, kiosk.Address, bouquet?.ProductId, bouquet?.QrCode,
                    bouquet?.PriceSnapshot, bouquet?.SellableUntil, bouquet?.Status,
                    order?.OrderId, order?.OrderCode, order?.Status,
                    order is null ? null : charges.GetValueOrDefault(order.CheckoutId));
            }).ToList();
            await snapshot.CommitAsync(ct);
            return Results.Ok(response);
        })
        .RequireAuthorization("Seller")
        .WithTags("KioskOps")
        .WithSummary("Danh sách các ô trong kiosk đang được Seller đăng nhập quản lý")
        .WithDescription("status là trạng thái ô; bouquetStatus là trạng thái bó đang nằm trong ô. orderId/orderCode/orderStatus/paymentStatus mô tả đơn đang giữ bó này, hoặc đơn gần nhất khi ô chờ thu hồi. paymentStatus chỉ lấy từ khoản CHARGE. Ô trống hoặc hoa đang bán lại không mang trạng thái đơn cũ.")
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
                .Select(x => new AdminSlotResponse(x.Id, x.KioskId, x.SlotCode, x.Status, x.Zone, x.CurrentSellerId, x.BouquetId,
                    x.Kiosk.Code, x.Kiosk.Name, x.Kiosk.Address, x.Kiosk.Status)).ToListAsync();
        }).RequireAuthorization("Admin").Produces<List<AdminSlotResponse>>();
    }
    private static bool SameSeller(HttpContext http, Guid id) => http.User.IsInRole("ADMIN") || http.User.FindFirstValue("seller_id") == id.ToString();
}

public sealed record AdminKioskResponse(Guid Id, string Code, string Name, string Status, Guid? SellerId, string? SellerName, int SlotCount);
public sealed record PublicKioskDto(Guid Id, string Code, string Name, string Address, string Region, string Status, double? Latitude, double? Longitude, bool LocationComplete);
