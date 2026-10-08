using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Identity;

public static class MerchantNames
{
    public static Task<Dictionary<Guid, string>> ReadActiveAsync(FloraDbContext db, Guid[] ids, CancellationToken ct) =>
        db.Sellers.AsNoTracking().Where(x => ids.Contains(x.Id) && x.Status == "ACTIVE").ToDictionaryAsync(x => x.Id, x => x.ShopName, ct);

    public static Task<Dictionary<Guid, string>> ReadAsync(FloraDbContext db, Guid[] ids, CancellationToken ct) =>
        db.Sellers.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ShopName, ct);
}
