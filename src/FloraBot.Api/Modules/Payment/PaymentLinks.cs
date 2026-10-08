using System.Text.Json;
using System.Security.Claims;
using System.Data;
using FloraBot.Api.Modules.Identity;
using Npgsql;

namespace FloraBot.Api.Modules.Payment;

public static class PaymentLinks
{
    public static void MapPaymentLinks(this WebApplication app)
    {
        app.MapPost("/api/sellers/{sellerId:guid}/subscriptions/{subscriptionId:guid}/payment-link",
            async (Guid sellerId, Guid subscriptionId, HttpContext http, NpgsqlDataSource source, PayOsClient gateway, CancellationToken ct) =>
            {
                await using var connection = await source.OpenConnectionAsync(ct);
                Guid paymentId; long amount;
                await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
                {
                    var pending = await SubscriptionPayments.PendingPaymentAsync(connection, transaction, sellerId, subscriptionId, ct);
                    if (pending is null) return Results.NotFound();
                    paymentId = pending.Value;
                    await using var find = new NpgsqlCommand("SELECT amount FROM payment.payments WHERE id=@id AND status='PENDING' AND kind='CHARGE' AND gateway='PAYOS'", connection, transaction);
                    find.Parameters.AddWithValue("id", paymentId);
                    if (await find.ExecuteScalarAsync(ct) is not long price) return Results.NotFound();
                    amount = price;
                    await transaction.CommitAsync(ct);
                }
                if (!gateway.IsConfigured) return Results.Problem(statusCode: 503, title: "Thanh toán chưa được cấu hình.");
                // Autocommit the mapping before the remote side effect, including on timeout.
                await using var reserve = new NpgsqlCommand("SELECT flow.reserve_gateway_order(@id)", connection);
                reserve.Parameters.AddWithValue("id", paymentId);
                var code = (long)(await reserve.ExecuteScalarAsync(ct))!;
                try
                {
                    var result = await gateway.CreateAsync(code, amount, ct);
                    await using var save = new NpgsqlCommand("UPDATE payment.gateway_orders SET checkout_url=@url WHERE payment_id=@id; SELECT flow.audit(@actor,'PAYMENT_LINK_REQUESTED','payments',@id)", connection);
                    save.Parameters.AddWithValue("url", result.CheckoutUrl); save.Parameters.AddWithValue("id", paymentId);
                    save.Parameters.AddWithValue("actor", Guid.Parse(http.User.FindFirstValue("sub")!));
                    await save.ExecuteNonQueryAsync(ct);
                    return Results.Ok(result);
                }
                catch (Exception ex) when (ex is PaymentGatewayException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException || ex is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    return Results.Problem(statusCode: 502, title: "Chưa lấy được liên kết thanh toán. Vui lòng thử lại.");
                }
            }).RequireAuthorization("Merchant", "SameSeller").WithTags("Payment").Produces<PaymentLinkResponse>();
    }
}
