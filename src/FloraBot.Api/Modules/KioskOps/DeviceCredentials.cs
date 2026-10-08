using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.KioskOps;

public static class DeviceCredentials
{
    public static Task<Guid?> FindEnabledAsync(FloraDbContext db, string keyHash, CancellationToken ct) =>
        db.Kiosks.AsNoTracking().Where(k => k.ApiKeyHash == keyHash && k.Status != "DISABLED")
            .Select(k => (Guid?)k.Id).SingleOrDefaultAsync(ct);
}
