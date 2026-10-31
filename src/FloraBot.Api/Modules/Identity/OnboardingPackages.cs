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
        app.MapGet("/api/kiosk-packages", async (FloraDbContext db, CancellationToken ct) =>
        {
            var packages = await db.SubscriptionPackages.AsNoTracking()
                .Where(x => x.BillingCycle == "ANNUAL" && x.Status == "ACTIVE")
                .OrderBy(x => x.AnnualFee)
                .Select(x => new KioskPackageResponse(
                    x.Id,
                    x.Name,
                    x.AnnualFee,
                    x.MaxSlots,
                    12,
                    x.Status,
                    Description(x.Name, x.MaxSlots)
                )).ToListAsync(ct);
            return Results.Ok(packages);
        }).AllowAnonymous().WithTags("Identity").Produces<List<KioskPackageResponse>>();
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

    private static string Description(string name, int total) =>
        $"Gói {name} 1 năm với tổng sức chứa {total} ô; chủ shop tự phân loại từng ô thành Walk-in hoặc Pre-order.";
}
