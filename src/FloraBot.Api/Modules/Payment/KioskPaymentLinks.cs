using System.Security.Claims;
using System.Text.Json;
using System.Data;
using FloraBot.Api.Modules.Ordering;
using Npgsql;
using NpgsqlTypes;

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

        app.MapPost("/api/kiosks/{kioskId:guid}/checkouts/{checkoutId:guid}/payment-link",
            async (Guid kioskId, Guid checkoutId, HttpContext http, NpgsqlDataSource source, IPayOsClientResolver resolver, ILogger<PayOsClient> logger, CancellationToken ct) =>
            {
                if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
                await using var connection = await source.OpenConnectionAsync(ct);
                Guid paymentId; long amount; DateTime deadline; Guid sellerId;
                Guid? pinnedChannelId = null;
                string secretRef;
                string expectedBin;
                string expectedAccount;
                string? expectedName;

                await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
                {
                    var snapshot = await CheckoutAccess.ReadAsync(connection, transaction, checkoutId, kioskId, Customer(http), ct);
                    if (snapshot is null || !snapshot.AwaitingPayment) return Results.NotFound();
                    deadline = snapshot.PayBefore;

                    // Derive seller from actual checkout orders
                    await using var sellerCmd = new NpgsqlCommand("SELECT DISTINCT seller_id FROM ordering.orders WHERE checkout_id=@id", connection, transaction);
                    sellerCmd.Parameters.AddWithValue("id", checkoutId);
                    var sellers = new List<Guid>();
                    await using (var sReader = await sellerCmd.ExecuteReaderAsync(ct))
                    {
                        while (await sReader.ReadAsync(ct)) sellers.Add(sReader.GetGuid(0));
                    }
                    if (sellers.Count == 0) return Results.NotFound();
                    if (sellers.Count > 1)
                    {
                        return Results.Problem(statusCode: 400, title: "Giỏ hàng có nhiều người bán khác nhau.", detail: "mixed_seller_checkout");
                    }
                    sellerId = sellers[0];

                    await using var find = new NpgsqlCommand("SELECT id, amount, channel_id FROM payment.payments WHERE kind='CHARGE' AND gateway='PAYOS' AND checkout_id=@id AND status='PENDING'", connection, transaction);
                    find.Parameters.AddWithValue("id", checkoutId);
                    await using (var reader = await find.ExecuteReaderAsync(ct))
                    {
                        if (!await reader.ReadAsync(ct)) return Results.NotFound();
                        paymentId = reader.GetGuid(0);
                        amount = reader.GetInt64(1);
                        pinnedChannelId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
                    }

                    if (pinnedChannelId.HasValue)
                    {
                        // Use existing pinned channel version (preserves old pending link reconciliation even if channel was later paused or rotated)
                        await using var chCmd = new NpgsqlCommand("SELECT secret_ref, expected_bank_bin, expected_bank_account, expected_bank_name FROM payment.payment_channel_versions WHERE id=@id", connection, transaction);
                        chCmd.Parameters.AddWithValue("id", pinnedChannelId.Value);
                        await using var chReader = await chCmd.ExecuteReaderAsync(ct);
                        if (!await chReader.ReadAsync(ct))
                        {
                            return Results.Problem(statusCode: 503, title: "Kênh thanh toán đã gắn vào đơn không tồn tại.");
                        }
                        secretRef = chReader.GetString(0);
                        expectedBin = chReader.GetString(1);
                        expectedAccount = chReader.GetString(2);
                        expectedName = chReader.IsDBNull(3) ? null : chReader.GetString(3);
                    }
                    else
                    {
                        // Find latest seller channel version
                        await using var chCmd = new NpgsqlCommand("""
                            SELECT id, version, secret_ref, state, expected_bank_bin, expected_bank_account, expected_bank_name
                            FROM payment.payment_channel_versions
                            WHERE seller_id=@sellerId
                            ORDER BY version DESC
                            LIMIT 1
                            """, connection, transaction);
                        chCmd.Parameters.AddWithValue("sellerId", sellerId);
                        Guid chVersionId; int version; string state;
                        await using (var chReader = await chCmd.ExecuteReaderAsync(ct))
                        {
                            if (!await chReader.ReadAsync(ct))
                            {
                                return Results.Problem(statusCode: 503, title: "Người bán chưa cấu hình kênh thanh toán trực tiếp. Chưa có khoản tiền nào được ghi nhận.", detail: "missing_seller_channel");
                            }
                            chVersionId = chReader.GetGuid(0);
                            version = chReader.GetInt32(1);
                            secretRef = chReader.GetString(2);
                            state = chReader.GetString(3);
                            expectedBin = chReader.GetString(4);
                            expectedAccount = chReader.GetString(5);
                            expectedName = chReader.IsDBNull(6) ? null : chReader.GetString(6);
                        }

                        if (state != "ACTIVE")
                        {
                            return Results.Problem(statusCode: 409, title: "Kênh thanh toán của người bán đang tạm dừng hoặc chưa sẵn sàng.", detail: "channel_not_active");
                        }

                        // Pin channel version, receiver snapshot, and seller_id to payment
                        var snapshotObj = new
                        {
                            bin = expectedBin,
                            accountNumber = expectedAccount,
                            accountName = expectedName,
                            provider = "PAYOS"
                        };
                        var snapshotJson = JsonSerializer.Serialize(snapshotObj);

                        await using var pinCmd = new NpgsqlCommand("""
                            UPDATE payment.payments
                            SET seller_id = @sellerId,
                                channel_id = @channelId,
                                channel_version = @version,
                                receiver_snapshot = @snapshot::jsonb,
                                funding_provenance = 'SELLER_DIRECT',
                                is_direct_receipt = true
                            WHERE id = @id AND funding_provenance = 'DIRECT_REQUIRED' AND channel_id IS NULL;
                            """, connection, transaction);
                        pinCmd.Parameters.AddWithValue("sellerId", sellerId);
                        pinCmd.Parameters.AddWithValue("channelId", chVersionId);
                        pinCmd.Parameters.AddWithValue("version", version);
                        pinCmd.Parameters.Add("snapshot", NpgsqlDbType.Jsonb).Value = snapshotJson;
                        pinCmd.Parameters.AddWithValue("id", paymentId);
                        await pinCmd.ExecuteNonQueryAsync(ct);
                    }

                    await transaction.CommitAsync(ct);
                }

                if (deadline <= DateTime.UtcNow) return Results.Conflict(new { message = "Đã hết thời gian thanh toán. Vui lòng chọn lại hoa." });

                if (!resolver.TryResolveChannelConfig(secretRef, out var channelConfig) || channelConfig is null)
                {
                    return Results.Problem(statusCode: 503, title: "Cấu hình kênh thanh toán không khả dụng.");
                }

                var gateway = resolver.ResolveClient(channelConfig.Options);
                if (!gateway.IsConfigured) return Results.Problem(statusCode: 503, title: "Thanh toán chưa được cấu hình.");

                await using var reserve = new NpgsqlCommand("SELECT flow.reserve_gateway_order(@id)", connection);
                reserve.Parameters.AddWithValue("id", paymentId);
                var code = (long)(await reserve.ExecuteScalarAsync(ct))!;
                try
                {
                    var link = await PayOsPaymentLinks.CreateAsync(source, gateway, paymentId, code, amount, ct,
                        new DateTimeOffset(deadline).ToUnixTimeSeconds());
                    await using var save = new NpgsqlCommand("UPDATE payment.gateway_orders SET checkout_url=@url WHERE payment_id=@id", connection);
                    save.Parameters.AddWithValue("url", link.CheckoutUrl); save.Parameters.AddWithValue("id", paymentId);
                    await save.ExecuteNonQueryAsync(ct);
                    return Results.Ok(link);
                }
                catch (Exception ex) when (ex is PaymentGatewayException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException || ex is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    var failureCode = ex is PaymentGatewayException gatewayError
                        ? gatewayError.FailureCode
                        : ex.GetType().Name;
                    logger.LogWarning(
                        "payOS payment-link creation failed for checkout {CheckoutId}: {FailureCode}",
                        checkoutId,
                        failureCode);
                    return Results.Problem(statusCode: 502, title: "Chưa lấy được liên kết thanh toán. Vui lòng thử lại.");
                }
            }).RequireAuthorization("Shopping").WithTags("Payment").Produces<PaymentLinkResponse>();
    }

    private static Guid? Customer(HttpContext http) => http.User.IsInRole("CUSTOMER") || http.User.IsInRole("SELLER") ? Guid.Parse(http.User.FindFirstValue("sub")!) : null;
}
