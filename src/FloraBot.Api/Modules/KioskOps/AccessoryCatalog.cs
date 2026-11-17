using System.Data;
using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Modules.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record KioskAccessoryResponse(Guid Id, string Name, long Price, int StockQuantity, string ShopName);
public sealed record PreorderAccessoryCatalogItem(Guid Id, string Name, long Price, int AvailableQuantity, string ShopName);

public static class AccessoryCatalog
{
    public static void MapAccessoryCatalog(this WebApplication app)
    {
        app.MapGet("/api/member/sellers/{sellerId:guid}/catalog/accessories", async (
            Guid sellerId, HttpContext http, FloraDbContext db, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await ReadShopAsync(sellerId, db, ct);
        }).RequireAuthorization("PreorderCustomer").WithTags("Preorders")
          .Produces<List<PreorderAccessoryCatalogItem>>()
          .WithDescription("Optional accessories for a Seller's flower preorder. Shared SHOP stock and unit prices do not depend on the selected pickup kiosk. Stock is reserved only on quote acceptance.");

        app.MapGet("/api/member/kiosks/{kioskId:guid}/catalog/accessories", async (
            Guid kioskId, HttpContext http, FloraDbContext db, NpgsqlDataSource data, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var kiosk = await db.Kiosks.AsNoTracking()
                .Where(x => x.Id == kioskId && x.Status != "DISABLED" && x.Status != "MAINTENANCE")
                .Select(x => new { x.Id, x.SellerId })
                .SingleOrDefaultAsync(ct);
            if (kiosk is null) return Results.NotFound();
            if (kiosk.SellerId is not Guid sellerId)
                return Results.Ok(Array.Empty<PreorderAccessoryCatalogItem>());

            await using (var availability = data.CreateCommand("SELECT flow.kiosk_package_valid(@kiosk) AND NOT flow.is_kiosk_terminating(@kiosk)"))
            {
                availability.Parameters.AddWithValue("kiosk", kioskId);
                if ((bool?)await availability.ExecuteScalarAsync(ct) != true)
                    return Results.Ok(Array.Empty<PreorderAccessoryCatalogItem>());
            }

            return await ReadShopAsync(sellerId, db, ct);
        }).RequireAuthorization("PreorderCustomer").WithTags("Preorders")
          .Produces<List<PreorderAccessoryCatalogItem>>()
          .WithDescription("Compatibility lookup: resolves the kiosk's Seller and returns that Seller's shared SHOP accessories. New preorder clients should load /api/member/sellers/{sellerId}/catalog/accessories directly.");

        app.MapGet("/api/kiosks/{kioskId:guid}/catalog/accessories", async (Guid kioskId, HttpContext http, FloraDbContext db, NpgsqlDataSource data, CancellationToken ct) =>
        {
            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
            await using (var leaseCheck = data.CreateCommand("SELECT flow.kiosk_package_valid(@kiosk)"))
            {
                leaseCheck.Parameters.AddWithValue("kiosk", kioskId);
                if ((bool?)await leaseCheck.ExecuteScalarAsync(ct) != true)
                    return Results.Ok(Array.Empty<KioskAccessoryResponse>());
            }

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var rows = await db.Accessories.AsNoTracking().Where(x => x.KioskId == kioskId && x.Status == "ACTIVE" && x.StockQuantity > 0)
                .OrderBy(x => x.Name).ThenBy(x => x.Id)
                .Select(x => new { x.Id, x.SellerId, x.Name, x.Price, x.StockQuantity }).ToListAsync(ct);
            var merchants = await MerchantNames.ReadActiveAsync(db, rows.Select(x => x.SellerId).Distinct().ToArray(), ct);
            var result = rows.Where(x => merchants.ContainsKey(x.SellerId))
                .Select(x => new KioskAccessoryResponse(x.Id, x.Name, x.Price, x.StockQuantity, merchants[x.SellerId])).ToList();
            await transaction.CommitAsync(ct);
            return Results.Ok(result);
        }).RequireAuthorization("Shopping").WithTags("KioskOps").Produces<List<KioskAccessoryResponse>>();
    }

    private static async Task<IResult> ReadShopAsync(Guid sellerId, FloraDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var merchants = await MerchantNames.ReadActiveAsync(db, [sellerId], ct);
        if (!merchants.TryGetValue(sellerId, out var shopName)) return Results.NotFound();
        var result = await db.Accessories.AsNoTracking()
            .Where(x => x.SellerId == sellerId && x.KioskId == null && x.Status == "ACTIVE" && x.StockQuantity > 0)
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new PreorderAccessoryCatalogItem(x.Id, x.Name, x.Price, x.StockQuantity, shopName))
            .ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(result);
    }
}
