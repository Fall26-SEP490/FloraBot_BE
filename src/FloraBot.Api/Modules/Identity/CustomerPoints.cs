using FloraBot.Api.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Claims;

namespace FloraBot.Api.Modules.Identity;

public sealed record CustomerPointsResponse(long LoyaltyPoints, decimal MaxRedeemPercent);

public static class CustomerPoints
{
    public static void MapCustomerPoints(this WebApplication app)
    {
        app.MapGet("/api/kiosks/{kioskId:guid}/customer/points", async (Guid kioskId, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var customerId))
                return Results.Unauthorized();

            await using var command = source.CreateCommand("""
                SELECT
                    u.id IS NOT NULL AS user_active,
                    ks.seller_id,
                    coalesce(b.balance, 0)::bigint AS balance,
                    coalesce(b.debt, 0)::bigint AS debt,
                    flow.cfg('points_max_redeem_percent') AS max_redeem_percent
                FROM (SELECT 1) _
                LEFT JOIN identity.users u
                    ON u.id = @customerId AND u.role IN ('CUSTOMER', 'SELLER') AND u.status = 'ACTIVE'
                LEFT JOIN identity.get_kiosk_seller_id(@kioskId) ks ON true
                LEFT JOIN identity.shop_loyalty_balances b
                    ON b.customer_id = u.id AND b.seller_id = ks.seller_id
                """);
            command.Parameters.AddWithValue("customerId", customerId);
            command.Parameters.AddWithValue("kioskId", kioskId);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || !reader.GetBoolean(0)) return Results.Unauthorized();
            if (reader.IsDBNull(1)) return Results.NotFound();

            var balance = reader.GetInt64(2);
            var debt = reader.GetInt64(3);
            var maxRedeemPercent = reader.GetDecimal(4);
            var availablePoints = debt > 0 ? 0 : Math.Max(0, balance);

            return Results.Ok(new CustomerPointsResponse(availablePoints, maxRedeemPercent));
        })
        .RequireAuthorization("KioskMember")
        .Produces<CustomerPointsResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }

    public static Task<long?> ReadAsync(FloraDbContext db, Guid customer, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(x => x.Id == customer && (x.Role == "CUSTOMER" || x.Role == "SELLER") && x.Status == "ACTIVE")
            .Select(x => (long?)x.LoyaltyPoints).SingleOrDefaultAsync(ct);
}
