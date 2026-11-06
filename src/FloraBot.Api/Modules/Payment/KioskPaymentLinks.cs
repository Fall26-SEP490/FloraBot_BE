using System.Security.Claims;
using System.Data;
using FloraBot.Api.Modules.Ordering;
using Npgsql;

namespace FloraBot.Api.Modules.Payment;

public sealed record CheckoutOrderResponse(Guid Id, string OrderCode, string Status, long Amount, string TrackingToken, long PointsRedeemed, long DiscountAmount);
public sealed record CheckoutResponse(Guid Id, string PaymentStatus, long Amount, DateTime PayBefore, List<CheckoutOrderResponse> Orders);

public static class KioskPaymentLinks
{
    public static void MapKioskPaymentLinks(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/kiosks/{kioskId:guid}/checkouts/{checkoutId:guid}",
            async (Guid kioskId, Guid checkoutId, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
            {
                if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
                await using var connection = await source.OpenConnectionAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
                var snapshot = await CheckoutAccess.ReadAsync(connection, transaction, checkoutId, kioskId, Customer(http), ct);
                if (snapshot is null) return Results.NotFound();
                await using var query = new NpgsqlCommand("SELECT status,amount FROM payment.payments WHERE kind='CHARGE' AND gateway='PAYOS' AND checkout_id=@id", connection, transaction);
                query.Parameters.AddWithValue("id", checkoutId);
                string status; long amount;
                await using (var reader = await query.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct)) return Results.NotFound();
                    status = reader.GetString(0); amount = reader.GetInt64(1);
                }
                await transaction.CommitAsync(ct);
                var orders = snapshot.Orders.Select(x => new CheckoutOrderResponse(x.Id, x.OrderCode, x.Status, x.Amount, x.TrackingToken, x.PointsRedeemed, x.DiscountAmount)).ToList();
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new CheckoutResponse(checkoutId, status, amount, snapshot.PayBefore, orders));
            }).RequireAuthorization("Shopping").WithTags("Payment").Produces<CheckoutResponse>();

        app.MapPost("/api/kiosks/{kioskId:guid}/checkouts/{checkoutId:guid}/cancel",
            CancelWalkInCheckout).RequireAuthorization("Shopping").WithTags("Payment")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost("/api/kiosks/{kioskId:guid}/checkouts/{checkoutId:guid}/payment-link",
            async (Guid kioskId, Guid checkoutId, HttpContext http, NpgsqlDataSource source, IPayOsClientResolver resolver, ILogger<PayOsClient> logger, CancellationToken ct) =>
            {
                if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
                await using var connection = await source.OpenConnectionAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(ct);
                var snapshot = await CheckoutAccess.ReadAsync(connection, transaction, checkoutId, kioskId, Customer(http), ct);
                if (snapshot is null || !snapshot.AwaitingPayment) return Results.NotFound();
                http.Response.Headers.CacheControl = "no-store";
                return await SellerCheckoutPaymentLinks.CreateAsync(connection, transaction, checkoutId, snapshot.PayBefore, resolver, logger, ct);
            }).RequireAuthorization("Shopping").WithTags("Payment").Produces<PaymentLinkResponse>();
    }

    private static async Task<IResult> CancelWalkInCheckout(Guid kioskId, Guid checkoutId, HttpContext http, NpgsqlDataSource source, CancellationToken ct)
    {
        if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Take the same payment lock as checkout_paid and expire_pending_payments before touching orders or stock.
        await using var find = new NpgsqlCommand("""
            SELECT status FROM payment.payments
            WHERE checkout_id=@checkout AND kind='CHARGE' AND purpose='ORDER_CHECKOUT' AND gateway='PAYOS'
            FOR UPDATE
            """, connection, transaction);
        find.Parameters.AddWithValue("checkout", checkoutId);
        if (await find.ExecuteScalarAsync(ct) is not string status) return Results.NotFound();
        var canCancel = await CheckoutAccess.CanCancelWalkInAsync(connection, transaction, checkoutId, kioskId, Customer(http), ct);
        if (canCancel is null) return Results.NotFound();
        if (status is "CANCELLED" or "EXPIRED" or "FAILED") return Results.NoContent();

        if (status == "PENDING" && canCancel.Value)
        {
            await using var cancel = new NpgsqlCommand("""
                UPDATE payment.payments SET status='CANCELLED'
                WHERE checkout_id=@checkout AND kind='CHARGE' AND status='PENDING'
                """, connection, transaction);
            cancel.Parameters.AddWithValue("checkout", checkoutId);
            if (await cancel.ExecuteNonQueryAsync(ct) == 1)
            {
                await using var release = new NpgsqlCommand("""
                    SET LOCAL TIME ZONE 'Asia/Ho_Chi_Minh';
                    SELECT flow.release_checkout(@checkout,'CANCELLED');
                    """, connection, transaction);
                release.Parameters.AddWithValue("checkout", checkoutId);
                await release.ExecuteNonQueryAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.NoContent();
            }
        }

        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Lượt mua không còn ở trạng thái chờ thanh toán.");
    }

    private static Guid? Customer(HttpContext http) => http.User.IsInRole("CUSTOMER") || http.User.IsInRole("SELLER") ? Guid.Parse(http.User.FindFirstValue("sub")!) : null;
}
