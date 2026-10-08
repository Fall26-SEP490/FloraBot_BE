using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Identity;

public static class IdentityReadEndpoints
{
    public static void MapIdentityReads(this WebApplication app)
    {
        app.MapGet("/api/packages", async (FloraDbContext db) => await db.SubscriptionPackages.AsNoTracking()
            .Where(x => x.Status == "ACTIVE").OrderBy(x => x.MonthlyFee).Select(x => new PackageResponse(x.Id, x.Name, x.MonthlyFee, x.MaxSlots)).ToListAsync()).AllowAnonymous().Produces<List<PackageResponse>>();
        app.MapGet("/api/sellers/{sellerId:guid}", async (Guid sellerId, FloraDbContext db, HttpContext http) =>
        {
            if (!SameSeller(http, sellerId)) return Results.NotFound();
            var row = await db.Sellers.AsNoTracking().Where(x => x.Id == sellerId).Select(x => new { x.Id, x.ShopName, x.Status, x.PackageId, x.PackageExpiresAt, x.BrandTone }).SingleOrDefaultAsync();
            return row is null ? Results.NotFound() : Results.Ok(row);
        }).RequireAuthorization("Merchant");
        app.MapGet("/api/sellers/{sellerId:guid}/subscriptions", async (Guid sellerId, FloraDbContext db, HttpContext http) =>
        {
            if (!SameSeller(http, sellerId)) return Results.NotFound();
            var seller = await db.Sellers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sellerId);
            if (seller is null) return Results.NotFound();
            var package = await db.SubscriptionPackages.AsNoTracking().Where(x => x.Id == seller.PackageId)
                .Select(x => new SellerPackageResponse(x.Name, x.MonthlyFee, x.MaxSlots, x.Status)).SingleOrDefaultAsync();
            var subscriptions = await db.Subscriptions.AsNoTracking().Where(x => x.SellerId == sellerId)
                .OrderByDescending(x => x.CreatedAt).Take(100)
                .Select(x => new SubscriptionResponse(x.Id, x.Package.Name, x.PeriodFrom, x.PeriodTo, x.Price, x.Status)).ToListAsync();
            return Results.Ok(new SubscriptionOverviewResponse(package, subscriptions));
        }).RequireAuthorization("Merchant").Produces<SubscriptionOverviewResponse>();
        app.MapGet("/api/admin/sellers", async (string? status, FloraDbContext db, HttpContext http) =>
        {
            if (status is not null && !new[] { "PENDING", "APPROVED", "ACTIVE", "PAST_DUE", "SUSPENDED", "CLOSED" }.Contains(status)) return Results.BadRequest();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await db.Sellers.AsNoTracking().Where(x => status == null || x.Status == status).OrderByDescending(x => x.CreatedAt)
                .Select(x => new AdminSellerResponse(x.Id, x.ShopName, x.Status, x.PackageId, x.PackageExpiresAt, x.Phone, x.Address)).Take(100).ToListAsync());
        }).RequireAuthorization("Admin").Produces<List<AdminSellerResponse>>();
    }
    private static bool SameSeller(HttpContext http, Guid id) => http.User.IsInRole("ADMIN") || http.User.FindFirstValue("seller_id") == id.ToString();
}
