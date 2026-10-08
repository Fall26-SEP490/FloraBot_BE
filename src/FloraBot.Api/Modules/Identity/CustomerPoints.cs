using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public sealed record CustomerPointsResponse(long LoyaltyPoints, decimal MaxRedeemPercent);

public static class CustomerPoints
{
    public static void MapCustomerPoints(this WebApplication app)
    {
        app.MapGet("/api/kiosks/{kioskId:guid}/customer/points", async (Guid kioskId, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
        {
            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
            await using var command = source.CreateCommand("""
                SELECT loyalty_points,flow.cfg('points_max_redeem_percent')
                FROM identity.users WHERE id=@customer AND role IN ('CUSTOMER','SELLER') AND status='ACTIVE'
                """);
            command.Parameters.AddWithValue("customer", Guid.Parse(http.User.FindFirstValue("sub")!));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.Unauthorized();
            return Results.Ok(new CustomerPointsResponse(reader.GetInt64(0), reader.GetDecimal(1)));
        }).RequireAuthorization("KioskMember").Produces<CustomerPointsResponse>();
    }

    public static Task<long?> ReadAsync(FloraDbContext db, Guid customer, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(x => x.Id == customer && (x.Role == "CUSTOMER" || x.Role == "SELLER") && x.Status == "ACTIVE")
            .Select(x => (long?)x.LoyaltyPoints).SingleOrDefaultAsync(ct);
}
