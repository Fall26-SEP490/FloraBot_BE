using System.Security.Claims;
using System.Text.Json;
using System.Data;
using FloraBot.Api.Modules.Ordering;
using Npgsql;

namespace FloraBot.Api.Modules.Payment;

public sealed record CheckoutOrderResponse(Guid Id, string OrderCode, string Status, long Amount, string TrackingToken, long PointsRedeemed, long DiscountAmount);
public sealed record CheckoutResponse(Guid Id, string PaymentStatus, long Amount, DateTime PayBefore, List<CheckoutOrderResponse> Orders);

public static class KioskPaymentLinks
{
    public static void MapKioskPaymentLinks(this WebApplication app)
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

        app.MapPost("/api/kiosks/{kioskId:guid}/checkouts/{checkoutId:guid}/payment-link",
            async (Guid kioskId, Guid checkoutId, HttpContext http, NpgsqlDataSource source, PayOsClient gateway, CancellationToken ct) =>
            {
                if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
                await using var connection = await source.OpenConnectionAsync(ct);
                Guid paymentId; long amount; DateTime deadline;
                await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
                {
                    var snapshot = await CheckoutAccess.ReadAsync(connection, transaction, checkoutId, kioskId, Customer(http), ct);
                    if (snapshot is null || !snapshot.AwaitingPayment) return Results.NotFound();
                    deadline = snapshot.PayBefore;
                    await using var find = new NpgsqlCommand("SELECT id,amount FROM payment.payments WHERE kind='CHARGE' AND gateway='PAYOS' AND checkout_id=@id AND status='PENDING'", connection, transaction);
                    find.Parameters.AddWithValue("id", checkoutId);
                    await using (var reader = await find.ExecuteReaderAsync(ct))
                    {
                        if (!await reader.ReadAsync(ct)) return Results.NotFound();
                        paymentId = reader.GetGuid(0); amount = reader.GetInt64(1);
                    }
                    await transaction.CommitAsync(ct);
                }
                if (deadline <= DateTime.UtcNow) return Results.Conflict(new { message = "Đã hết thời gian thanh toán. Vui lòng chọn lại hoa." });
                if (!gateway.IsConfigured) return Results.Problem(statusCode: 503, title: "Thanh toán chưa được cấu hình.");
                await using var reserve = new NpgsqlCommand("SELECT flow.reserve_gateway_order(@id)", connection);
                reserve.Parameters.AddWithValue("id", paymentId);
                var code = (long)(await reserve.ExecuteScalarAsync(ct))!;
                try
                {
                    var link = await gateway.CreateAsync(code, amount, ct, new DateTimeOffset(deadline).ToUnixTimeSeconds());
                    await using var save = new NpgsqlCommand("UPDATE payment.gateway_orders SET checkout_url=@url WHERE payment_id=@id", connection);
                    save.Parameters.AddWithValue("url", link.CheckoutUrl); save.Parameters.AddWithValue("id", paymentId);
                    await save.ExecuteNonQueryAsync(ct);
                    return Results.Ok(link);
                }
                catch (Exception ex) when (ex is PaymentGatewayException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException || ex is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    return Results.Problem(statusCode: 502, title: "Chưa lấy được liên kết thanh toán. Vui lòng thử lại.");
                }
            }).RequireAuthorization("Shopping").WithTags("Payment").Produces<PaymentLinkResponse>();
    }

    private static Guid? Customer(HttpContext http) => http.User.IsInRole("CUSTOMER") || http.User.IsInRole("SELLER") ? Guid.Parse(http.User.FindFirstValue("sub")!) : null;
}
