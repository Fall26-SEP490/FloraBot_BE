using System.Data;
using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Modules.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record KioskAccessoryResponse(Guid Id, string Name, long Price, int StockQuantity, string ShopName);

public static class AccessoryCatalog
{
    public static void MapAccessoryCatalog(this WebApplication app)
    {
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
}
