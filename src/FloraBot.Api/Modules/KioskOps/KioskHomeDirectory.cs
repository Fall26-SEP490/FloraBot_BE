using FloraBot.Api.Data;
using FloraBot.Api.Modules.Identity;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record KioskHomeInfo(Guid Id, string Code, string Name, string Address, string Status, Guid? SellerId, string? ShopName);

public static class KioskHomeDirectory
{
    // Public business metadata only: never return device credentials or hardware identifiers.
    public static async Task<KioskHomeInfo?> ReadAsync(Guid id, FloraDbContext db, CancellationToken ct)
    {
        var kiosk = await db.Kiosks.AsNoTracking().Where(k => k.Id == id && k.Status != "DISABLED")
            .Select(k => new { k.Id, k.Code, k.Name, k.Address, k.Status, k.SellerId }).SingleOrDefaultAsync(ct);
        if (kiosk is null) return null;
        var seller = kiosk.SellerId;
        if (!seller.HasValue)
        {
            var owners = await db.Slots.AsNoTracking().Where(s => s.KioskId == id)
                .Select(s => s.CurrentSellerId).Distinct().Take(2).ToListAsync(ct);
            if (owners.Count == 1) seller = owners[0];
        }
        var names = await MerchantNames.ReadActiveAsync(db, seller.HasValue ? [seller.Value] : [], ct);
        return new(kiosk.Id, kiosk.Code, kiosk.Name, kiosk.Address, kiosk.Status, seller,
            seller.HasValue ? names.GetValueOrDefault(seller.Value) : null);
    }
}
