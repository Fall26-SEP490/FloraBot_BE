using System.Security.Claims;
using System.Text.Json;
using FloraBot.Api.Modules.Ordering;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Payment;

public static class WebPaymentLinks
{
    public static void MapWebPaymentLinks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/member/preorders/{requestId:guid}/payment-link",
            async (Guid requestId, HttpContext http, NpgsqlDataSource source, IPayOsClientResolver resolver, ILoggerFactory loggerFactory, CancellationToken ct) =>
            {
                var logger = loggerFactory.CreateLogger("FloraBot.Api.Modules.Payment.WebPaymentLinks");
                var checkout = await WebOrders.MemberCheckoutAsync(source, requestId, Guid.Parse(http.User.FindFirstValue("sub")!), ct);
                if (checkout is null) return Results.NotFound();

                await using var connection = await source.OpenConnectionAsync(ct);
                Guid payment; long amount; DateTime deadline; Guid sellerId;
                Guid? pinnedChannelId = null;
                string secretRef;
                string expectedBin;
                string expectedAccount;
                string? expectedName;

                await using (var tx = await connection.BeginTransactionAsync(ct))
                {
                    await using var find = connection.CreateCommand();
                    find.Transaction = tx;
                    find.CommandText = "SELECT id, amount, created_at + flow.cfg_min('hold_minutes'), channel_id FROM payment.payments WHERE checkout_id=@checkout AND kind='CHARGE' AND status='PENDING' FOR UPDATE";
                    find.Parameters.AddWithValue("checkout", checkout.Value);
                    await using (var reader = await find.ExecuteReaderAsync(ct))
                    {
                        if (!await reader.ReadAsync(ct)) return Results.NotFound();
                        payment = reader.GetGuid(0);
                        amount = reader.GetInt64(1);
                        deadline = reader.GetDateTime(2);
                        pinnedChannelId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
                    }

                    // Derive seller from orders
                    await using var sellerCmd = connection.CreateCommand();
                    sellerCmd.Transaction = tx;
                    sellerCmd.CommandText = "SELECT seller_id FROM payment.v2_link_get_checkout_sellers(@checkout)";
                    sellerCmd.Parameters.AddWithValue("checkout", checkout.Value);
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

                    if (pinnedChannelId.HasValue)
                    {
                        // Use pinned channel version
                        await using var chCmd = connection.CreateCommand();
                        chCmd.Transaction = tx;
                        chCmd.CommandText = "SELECT secret_ref, expected_bank_bin, expected_bank_account, expected_bank_name FROM payment.payment_channel_versions WHERE id=@id";
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
                        // Find latest active seller channel
                        await using var chCmd = connection.CreateCommand();
                        chCmd.Transaction = tx;
                        chCmd.CommandText = """
                            SELECT id, version, secret_ref, state, expected_bank_bin, expected_bank_account, expected_bank_name
                            FROM payment.payment_channel_versions
                            WHERE seller_id=@sellerId
                            ORDER BY version DESC
                            LIMIT 1
                            """;
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

                        // Pin channel to payment
                        var snapshotObj = new
                        {
                            bin = expectedBin,
                            accountNumber = expectedAccount,
                            accountName = expectedName,
                            provider = "PAYOS"
                        };
                        var snapshotJson = JsonSerializer.Serialize(snapshotObj);

                        await using var pinCmd = connection.CreateCommand();
                        pinCmd.Transaction = tx;
                        pinCmd.CommandText = """
                            UPDATE payment.payments
                            SET seller_id = @sellerId,
                                channel_id = @channelId,
                                channel_version = @version,
                                receiver_snapshot = @snapshot::jsonb,
                                funding_provenance = 'SELLER_DIRECT',
                                is_direct_receipt = true
                            WHERE id = @id AND funding_provenance = 'DIRECT_REQUIRED' AND channel_id IS NULL;
                            """;
                        pinCmd.Parameters.AddWithValue("sellerId", sellerId);
                        pinCmd.Parameters.AddWithValue("channelId", chVersionId);
                        pinCmd.Parameters.AddWithValue("version", version);
                        pinCmd.Parameters.Add("snapshot", NpgsqlDbType.Jsonb).Value = snapshotJson;
                        pinCmd.Parameters.AddWithValue("id", payment);
                        await pinCmd.ExecuteNonQueryAsync(ct);
                    }

                    await tx.CommitAsync(ct);
                }

                if (deadline <= DateTime.UtcNow) return Results.Problem(statusCode: 409, detail: "Đã hết hạn thanh toán. Vui lòng tạo yêu cầu mới.");

                if (!resolver.TryResolveChannelConfig(secretRef, out var channelConfig) || channelConfig is null)
                {
                    return Results.Problem(statusCode: 503, detail: "Cấu hình kênh thanh toán không khả dụng.");
                }

                var gateway = resolver.ResolveClient(channelConfig.Options);
                if (!gateway.IsConfigured) return Results.Problem(statusCode: 503, detail: "Thanh toán trực tuyến chưa được cấu hình. Chưa có khoản tiền nào được ghi nhận.");

                await using var reserve = connection.CreateCommand();
                reserve.CommandText = "SELECT flow.reserve_gateway_order(@id)";
                reserve.Parameters.AddWithValue("id", payment);
                var code = (long)(await reserve.ExecuteScalarAsync(ct))!;
                try
                {
                    var link = await PayOsPaymentLinks.CreateAsync(source, gateway, payment, code, amount, ct,
                        new DateTimeOffset(deadline).ToUnixTimeSeconds());
                    await using var save = connection.CreateCommand();
                    save.CommandText = "UPDATE payment.gateway_orders SET checkout_url=@url WHERE payment_id=@id";
                    save.Parameters.AddWithValue("url", link.CheckoutUrl); save.Parameters.AddWithValue("id", payment); await save.ExecuteNonQueryAsync(ct);
                    return Results.Ok(link);
                }
                catch (Exception ex) when (ex is PaymentGatewayException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException || ex is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    var failureCode = ex is PaymentGatewayException paymentException
                        ? paymentException.FailureCode
                        : ex switch
                        {
                            HttpRequestException => "http_request_failed",
                            JsonException => "invalid_provider_json",
                            InvalidOperationException => "invalid_provider_response",
                            KeyNotFoundException => "missing_provider_field",
                            FormatException => "invalid_provider_format",
                            OperationCanceledException => "provider_timeout",
                            _ => "unexpected_provider_error"
                        };
                    logger.LogWarning(
                        "PayOS preorder payment-link creation failed. RequestId {RequestId}, PaymentId {PaymentId}, OrderCode {OrderCode}, FailureCode {FailureCode}",
                        requestId, payment, code, failureCode);
                    return Results.Problem(statusCode: 502, detail: "Chưa lấy được liên kết thanh toán. Hãy tải lại trạng thái đơn trước khi thử lại.");
                }
            }).RequireAuthorization("PreorderCustomer").Produces<PaymentLinkResponse>();
    }
}
