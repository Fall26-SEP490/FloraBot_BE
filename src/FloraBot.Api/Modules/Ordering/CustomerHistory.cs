using System.Data;
using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Modules.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace FloraBot.Api.Modules.Ordering;

public sealed record CustomerHistoryItem(Guid Id, string OrderCode, DateTime CreatedAt, string Status,
    long TotalAmount, long PointsEarned, long PointsRedeemed, string ShopName, string[] Items);
public sealed record CustomerHistoryPage(List<CustomerHistoryItem> Items, int Page, bool HasMore, long LoyaltyPoints, bool CanForgetAccount = true);

public static class CustomerHistory
{
    public static void MapCustomerHistory(this WebApplication app)
    {
        app.MapGet("/api/kiosks/{kioskId:guid}/customer/history", async (Guid kioskId, int? page,
            FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
            return await ReadAsync(page, db, http, ct);
        }).RequireAuthorization("KioskMember").Produces<CustomerHistoryPage>();
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
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new { x.OrderId, x.NameSnapshot }).ToListAsync(ct);
        var grouped = items.ToLookup(x => x.OrderId);
        var preorderFlowers = new Dictionary<Guid, string>();
        if (ids.Length > 0)
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var flowers = new NpgsqlCommand("""
                SELECT r.order_id,'Hoa đặt riêng: ' || coalesce(r.display_snapshot->>'product','Bó hoa đặt trước')
                FROM ordering.web_requests r
                WHERE r.order_id=ANY(@ids) AND r.kind='CUSTOM'
                  AND NOT EXISTS (SELECT 1 FROM ordering.order_items i
                    WHERE i.order_id=r.order_id AND i.item_type='BOUQUET')
                """, connection, (NpgsqlTransaction)transaction.GetDbTransaction());
            flowers.Parameters.AddWithValue("ids", ids);
            await using var reader = await flowers.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) preorderFlowers[reader.GetGuid(0)] = reader.GetString(1);
        }
        var result = visible.Select(x =>
        {
            var namesForOrder = grouped[x.Id].Select(item => item.NameSnapshot).ToList();
            if (preorderFlowers.TryGetValue(x.Id, out var preorderFlower)) namesForOrder.Insert(0, preorderFlower);
            return new CustomerHistoryItem(x.Id, x.OrderCode, x.CreatedAt, x.Status,
                x.TotalAmount, x.PointsEarned, x.PointsRedeemed, names.GetValueOrDefault(x.SellerId, "Shop hoa"), namesForOrder.ToArray());
        }).ToList();
        await transaction.CommitAsync(ct);
        return Results.Ok(new CustomerHistoryPage(result, number, rows.Count > 25, points.Value, http.User.IsInRole("CUSTOMER")));
    }

}
