using System.Security.Claims;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public sealed record KioskRenewalResponse(Guid SubscriptionId, Guid KioskId, Guid SellerId,
    long Amount, DateTime PeriodFrom, DateTime PeriodTo, string Status, string PaymentLinkPath);

public static class KioskRenewals
{
    public static void MapKioskRenewals(this WebApplication app)
    {
        app.MapPost("/api/me/kiosks/{kioskId:guid}/renewals", async (
            Guid kioskId, HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor) ||
                !Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId) ||
                !http.User.IsInRole("SELLER")) return Results.Forbid();

            await using var connection = await data.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                Guid subscriptionId;
                await using (var create = new NpgsqlCommand("SELECT flow.request_kiosk_renewal(@actor,@kiosk)", connection, transaction))
                {
                    create.Parameters.AddWithValue("actor", actor);
                    create.Parameters.AddWithValue("kiosk", kioskId);
                    subscriptionId = (Guid)(await create.ExecuteScalarAsync(ct))!;
                }
                KioskRenewalResponse result;
                await using (var read = new NpgsqlCommand("""
                    SELECT seller_id,kiosk_id,price,period_from_ts,period_to_ts,status
                    FROM identity.subscriptions WHERE id=@id
                    """, connection, transaction))
                {
                    read.Parameters.AddWithValue("id", subscriptionId);
                    await using var reader = await read.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Created renewal not found.");
                    result = new(subscriptionId, reader.GetGuid(1), reader.GetGuid(0), reader.GetInt64(2),
                        reader.GetDateTime(3), reader.GetDateTime(4), reader.GetString(5),
                        $"/api/sellers/{sellerId}/subscriptions/{subscriptionId}/payment-link");
                }
                await transaction.CommitAsync(ct);
                return Results.Ok(result);
            }
            catch (PostgresException ex) when (ex.SqlState is "42501" or "22023" or "23505" or "23P01" or "55000")
            {
                return ex.SqlState switch
                {
                    "42501" => Results.NotFound(),
                    "23505" or "23P01" => Results.Problem(statusCode: 409, detail: "Kiosk đã có yêu cầu gia hạn đang chờ thanh toán."),
                    "55000" => Results.Problem(statusCode: 503, detail: "Đơn giá ô kiosk chưa được cấu hình."),
                    _ => Results.Problem(statusCode: 400, detail: "Kiosk không đủ điều kiện gia hạn.")
                };
            }
        }).RequireAuthorization("Merchant").WithTags("Identity")
          .Produces<KioskRenewalResponse>().ProducesProblem(400).ProducesProblem(409).ProducesProblem(503);
    }
}
