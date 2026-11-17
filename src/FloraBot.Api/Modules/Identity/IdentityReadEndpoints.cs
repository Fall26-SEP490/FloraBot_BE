using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Identity;

public static class IdentityReadEndpoints
{
    public static void MapIdentityReads(this WebApplication app)
    {
        app.MapGet("/api/sellers/{sellerId:guid}", async (Guid sellerId, FloraDbContext db, HttpContext http) =>
        {
            if (!SameSeller(http, sellerId)) return Results.NotFound();
            var row = await db.Sellers.AsNoTracking().Where(x => x.Id == sellerId).Select(x => new { x.Id, x.ShopName, x.Status, x.PackageId, x.PackageExpiresAt, x.BrandTone }).SingleOrDefaultAsync();
            return row is null ? Results.NotFound() : Results.Ok(row);
        }).RequireAuthorization("Merchant");
        app.MapGet("/api/sellers/{sellerId:guid}/subscriptions", async (Guid sellerId, int? page, Guid? kioskId, FloraDbContext db, HttpContext http) =>
        {
            if (!SameSeller(http, sellerId)) return Results.NotFound();
            var number = page ?? 1;
            if (number is < 1 or > 100000) return Results.BadRequest();
            http.Response.Headers.CacheControl = "no-store";
            var seller = await db.Sellers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sellerId);
            if (seller is null) return Results.NotFound();
            var package = await db.SubscriptionPackages.AsNoTracking().Where(x => x.Id == seller.PackageId && x.BillingCycle == "ANNUAL")
                .Select(x => new SellerPackageResponse(x.Name, x.AnnualFee, x.MaxSlots, x.Status)).SingleOrDefaultAsync();
            var pageSize = page.HasValue ? 25 : 100;
            var subscriptions = await db.Subscriptions.AsNoTracking()
                .Where(x => x.SellerId == sellerId && x.KioskId != null && (kioskId == null || x.KioskId == kioskId))
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .Skip((number - 1) * pageSize).Take(pageSize + 1)
                .Select(x => new SubscriptionResponse(x.Id, x.Package.Name, x.PeriodFrom, x.PeriodTo, x.Price, x.Status, x.KioskId,
                    db.Kiosks.IgnoreQueryFilters().Where(k => k.Id == x.KioskId).Select(k => k.Code).FirstOrDefault(),
                    db.Kiosks.IgnoreQueryFilters().Where(k => k.Id == x.KioskId).Select(k => k.Name).FirstOrDefault(),
                    x.PeriodToTs,
                    db.AuditLogs.Where(a => a.EntityType == "subscriptions" && a.EntityId == x.Id &&
                        (a.Action == "SUBSCRIPTION_REMINDER_QUEUED" || a.Action == "SUBSCRIPTION_REMINDER_SENT" ||
                         a.Action == "SUBSCRIPTION_REMINDER_FAILED" || a.Action == "SUBSCRIPTION_REMINDER_SKIPPED"))
                        .OrderByDescending(a => a.Id).Select(a => a.Action).FirstOrDefault())).ToListAsync();
            return Results.Ok(new SubscriptionOverviewResponse(package, subscriptions.Take(pageSize).ToList(), number, subscriptions.Count > pageSize));
        }).RequireAuthorization("Merchant").Produces<SubscriptionOverviewResponse>();
        app.MapGet("/api/admin/sellers", async (string? status, int? page, string? search, FloraDbContext db, HttpContext http) =>
        {
            if (status is not null && !new[] { "PENDING", "APPROVED", "ACTIVE", "PAST_DUE", "SUSPENDED", "CLOSED" }.Contains(status)) return Results.BadRequest();
            var number = page ?? 1;
            if (number is < 1 or > 100000 || search?.Length > 100) return Results.BadRequest();
            http.Response.Headers.CacheControl = "no-store";
            var pageSize = page.HasValue ? 25 : 100;
            var term = search?.Trim().ToLowerInvariant();
            var rows = await db.Sellers.AsNoTracking().Where(x => (status == null || x.Status == status)
                    && (string.IsNullOrEmpty(term) || x.ShopName.ToLower().Contains(term)))
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .Skip((number - 1) * pageSize).Take(pageSize + 1)
                .Select(x => new AdminSellerResponse(x.Id, x.ShopName, x.Status, x.PackageId, x.PackageExpiresAt, x.Phone, x.Address)).ToListAsync();
            http.Response.Headers["X-Has-More"] = rows.Count > pageSize ? "true" : "false";
            return Results.Ok(rows.Take(pageSize).ToList());
        }).RequireAuthorization("Admin").Produces<List<AdminSellerResponse>>();
    }
    private static bool SameSeller(HttpContext http, Guid id) => http.User.IsInRole("ADMIN") || http.User.FindFirstValue("seller_id") == id.ToString();
}
