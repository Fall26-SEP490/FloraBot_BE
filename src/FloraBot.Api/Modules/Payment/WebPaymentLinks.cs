using System.Security.Claims;
using System.Text.Json;
using FloraBot.Api.Modules.Ordering;
using Npgsql;

namespace FloraBot.Api.Modules.Payment;

public static class WebPaymentLinks
{
    public static void MapWebPaymentLinks(this WebApplication app)
    {
        app.MapPost("/api/member/preorders/{requestId:guid}/payment-link", async (Guid requestId, HttpContext http, NpgsqlDataSource source, PayOsClient gateway, CancellationToken ct) =>
        {
            var checkout = await WebOrders.MemberCheckoutAsync(source, requestId, Guid.Parse(http.User.FindFirstValue("sub")!), ct);
            if (checkout is null) return Results.NotFound();
            await using var find = source.CreateCommand("SELECT id,amount,created_at+flow.cfg_min('hold_minutes') FROM payment.payments WHERE checkout_id=@checkout AND kind='CHARGE' AND status='PENDING'");
            find.Parameters.AddWithValue("checkout", checkout.Value);
            Guid payment; long amount; DateTime deadline;
            await using (var reader = await find.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return Results.NotFound();
                payment = reader.GetGuid(0); amount = reader.GetInt64(1); deadline = reader.GetDateTime(2);
            }
            if (deadline <= DateTime.UtcNow) return Results.Problem(statusCode: 409, detail: "Đã hết hạn thanh toán. Vui lòng tạo yêu cầu mới.");
            if (!gateway.IsConfigured) return Results.Problem(statusCode: 503, detail: "Thanh toán trực tuyến chưa được cấu hình. Chưa có khoản tiền nào được ghi nhận.");
            await using var reserve = source.CreateCommand("SELECT flow.reserve_gateway_order(@id)"); reserve.Parameters.AddWithValue("id", payment);
            var code = (long)(await reserve.ExecuteScalarAsync(ct))!;
            try
            {
                var link = await gateway.CreateAsync(code, amount, ct, new DateTimeOffset(deadline).ToUnixTimeSeconds());
                await using var save = source.CreateCommand("UPDATE payment.gateway_orders SET checkout_url=@url WHERE payment_id=@id");
                save.Parameters.AddWithValue("url", link.CheckoutUrl); save.Parameters.AddWithValue("id", payment); await save.ExecuteNonQueryAsync(ct);
                return Results.Ok(link);
            }
            catch (Exception ex) when (ex is PaymentGatewayException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            { return Results.Problem(statusCode: 502, detail: "Chưa lấy được liên kết thanh toán. Hãy tải lại trạng thái đơn trước khi thử lại."); }
        }).RequireAuthorization("Member").Produces<PaymentLinkResponse>();
    }
}
