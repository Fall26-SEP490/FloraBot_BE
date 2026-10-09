using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Catalog;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.ReadModels;

namespace FloraBot.Api.Infrastructure;

public sealed record ProductResponse(
    Guid Id,
    string Name,
    string? Description,
    long Price,
    List<string> Tags,
    string Status,
    int? ShelfLifeHours,
    string? InventoryKind = null,
    decimal? LengthCm = null,
    decimal? WidthCm = null,
    decimal? HeightCm = null);
public sealed record SubscriptionResponse(Guid Id, string PackageName, DateOnly PeriodFrom, DateOnly PeriodTo, long Price, string Status);
public sealed record SellerPackageResponse(string Name, long MonthlyFee, int MaxSlots, string Status);
public sealed record SubscriptionOverviewResponse(SellerPackageResponse? Package, List<SubscriptionResponse> Subscriptions);
public sealed record KioskCatalogResponse(Guid BouquetId, string Name, long Price, string SlotCode, string ShopName);
public sealed record PackageResponse(Guid Id, string Name, long MonthlyFee, int MaxSlots);
public sealed record AdminSellerResponse(Guid Id, string ShopName, string Status, Guid? PackageId, DateOnly? PackageExpiresAt, string Phone, string? Address);
public sealed record AdminSlotResponse(Guid Id, Guid KioskId, string SlotCode, string Status, Guid? CurrentSellerId, Guid? BouquetId, string KioskCode, string KioskName, string KioskAddress, string KioskStatus);
public sealed record SellerSlotResponse(Guid Id, Guid KioskId, string SlotCode, string Status, Guid? BouquetId, string KioskCode, string KioskName, string KioskAddress, Guid? ProductId, string? QrCode, long? PriceSnapshot, DateTime? SellableUntil);

public static class ReadEndpoints
{
    public static void MapReads(this WebApplication app)
    {
        app.MapIdentityReads();
        app.MapCatalogReads();
        app.MapKioskOpsReads();
        app.MapScreenReads();
    }
}
