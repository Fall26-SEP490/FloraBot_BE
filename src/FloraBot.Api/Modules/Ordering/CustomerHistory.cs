using System.Data;
using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Modules.Identity;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Ordering;

public sealed record CustomerHistoryItem(Guid Id, string OrderCode, DateTime CreatedAt, string Status,
    long TotalAmount, long PointsEarned, long PointsRedeemed, string ShopName, string[] Items);
public sealed record CustomerHistoryPage(List<CustomerHistoryItem> Items, int Page, bool HasMore, long LoyaltyPoints);

public static class CustomerHistory
{
    public static void MapCustomerHistory(this WebApplication app)
    {
        app.MapGet("/api/kiosks/{kioskId:guid}/customer/history", async (Guid kioskId, int? page,
            FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
            return await ReadAsync(page, db, http, ct);
        }).RequireAuthorization("Customer").Produces<CustomerHistoryPage>();
        app.MapGet("/api/member/history", ReadAsync).RequireAuthorization("Member").Produces<CustomerHistoryPage>();
    }
    private static async Task<IResult> ReadAsync(int? page, FloraDbContext db, HttpContext http, CancellationToken ct)
    {
        var number = page ?? 1;
        if (number < 1 || number > 100000) return Results.BadRequest();
        var customer = Guid.Parse(http.User.FindFirstValue("sub")!);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var points = await CustomerPoints.ReadAsync(db, customer, ct);
        if (points is null) return Results.Unauthorized();
        var rows = await db.Orders.AsNoTracking().Where(x => x.CustomerId == customer)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((number - 1) * 25).Take(26)
            .Select(x => new { x.Id, x.OrderCode, x.CreatedAt, x.Status, x.TotalAmount, x.PointsEarned, x.PointsRedeemed, x.SellerId })
            .ToListAsync(ct);
        var visible = rows.Take(25).ToArray();
        var ids = visible.Select(x => x.Id).ToArray();
        var names = await MerchantNames.ReadAsync(db, visible.Select(x => x.SellerId).Distinct().ToArray(), ct);
        var items = await db.OrderItems.AsNoTracking().Where(x => ids.Contains(x.OrderId))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => new { x.OrderId, x.NameSnapshot }).ToListAsync(ct);
        var grouped = items.ToLookup(x => x.OrderId, x => x.NameSnapshot);
        var result = visible.Select(x => new CustomerHistoryItem(x.Id, x.OrderCode, x.CreatedAt, x.Status,
            x.TotalAmount, x.PointsEarned, x.PointsRedeemed, names.GetValueOrDefault(x.SellerId, "Shop hoa"), grouped[x.Id].ToArray())).ToList();
        await transaction.CommitAsync(ct);
        return Results.Ok(new CustomerHistoryPage(result, number, rows.Count > 25, points.Value));
    }

}
