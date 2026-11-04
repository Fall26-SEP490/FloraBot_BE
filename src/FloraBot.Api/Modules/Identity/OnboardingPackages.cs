using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Identity;

public sealed record KioskPackageResponse(
    Guid Id,
    string Name,
    long AnnualFee,
    int TotalSlots,
    int TermMonths,
    string Status,
    string Description
);

public sealed record OnboardingPackageInfo(
    Guid Id,
    string Name,
    long AnnualFee,
    int TotalSlots
);

public static class OnboardingPackages
{
    public static void MapOnboardingPackages(this WebApplication app)
    {
        FloraBot.Api.Modules.KioskOps.KioskSlotPricing.MapKioskSlotPricing(app);
    }

    public static async Task<OnboardingPackageInfo?> FindActiveAnnualPackageAsync(FloraDbContext db, Guid packageId, CancellationToken ct)
    {
        return await db.SubscriptionPackages.AsNoTracking()
            .Where(x => x.Id == packageId && x.BillingCycle == "ANNUAL" && x.Status == "ACTIVE")
            .Select(x => new OnboardingPackageInfo(x.Id, x.Name, x.AnnualFee, x.MaxSlots))
            .SingleOrDefaultAsync(ct);
    }

    public static async Task<Dictionary<Guid, string>> PackageNamesAsync(FloraDbContext db, Guid[] ids, CancellationToken ct)
    {
        return await db.SubscriptionPackages.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
    }

    public static async Task<Dictionary<Guid, long>> PackageFeesAsync(FloraDbContext db, Guid[] ids, CancellationToken ct)
    {
        return await db.SubscriptionPackages.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.AnnualFee, ct);
    }

}
