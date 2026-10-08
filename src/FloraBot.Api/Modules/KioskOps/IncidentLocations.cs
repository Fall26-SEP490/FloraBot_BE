using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record IncidentLocation(Guid KioskId, string KioskCode, string KioskName, string Address);
public sealed record IncidentSlot(Guid Id, string SlotCode, string Status);

public static class IncidentLocations
{
    public static Task<List<IncidentLocation>> KiosksAsync(FloraDbContext db, Guid[] ids, CancellationToken ct) =>
        db.Kiosks.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new IncidentLocation(x.Id, x.Code, x.Name, x.Address)).ToListAsync(ct);

    public static Task<List<IncidentSlot>> SlotsAsync(FloraDbContext db, Guid[] ids, CancellationToken ct) =>
        db.Slots.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new IncidentSlot(x.Id, x.SlotCode, x.Status)).ToListAsync(ct);
}
