using System.Data;
using System.Security.Claims;
using System.Text.Json;
using DotNetCore.CAP;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Payment;

public sealed record SellerPaymentListItem(
    Guid Id,
    long? OrderCode,
    long Amount,
    string Status,
    DateTime? PaidAt,
    DateTime CreatedAt,
    int? ChannelVersion,
    string? ReceiverAccountMasked,
    Guid? CheckoutId
);

public sealed record SellerPaymentsListResponse(
    List<SellerPaymentListItem> Payments,
    int TotalCount,
    int Page,
    int PageSize
);

public sealed record SellerPaymentDetailResponse(
    Guid Id,
    Guid SellerId,
    Guid? ChannelId,
    int? ChannelVersion,
    long? OrderCode,
    long Amount,
    string Status,
    string Purpose,
    string Gateway,
    string? GatewayTxnId,
    object? ReceiverSnapshotMasked,
    DateTime? PaidAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool Reconciled,
    List<CheckoutOrderResponse> Orders
);

public sealed record ReconcilePaymentResponse(
    Guid PaymentId,
    string PreviousStatus,
    string CurrentStatus,
    string ProviderStatus,
    long Amount,
    string? TransactionReference,
    bool Transitioned
);

public sealed record KioskSalesSummary(
    Guid KioskId,
    string KioskCode,
    string KioskName,
    long GrossPaid,
    int OrderCount
);

public sealed record SellerSalesReportResponse(
    Guid SellerId,
    DateTime? From,
    DateTime? To,
    long GrossPaid,
    long TotalRefunded,
    long NetReceipts,
    int PaidOrders,
    int UnclaimedOrders,
    long PendingAmount,
    List<KioskSalesSummary> ByKiosk
);

public static class SellerPaymentsEndpoints
{
    private static async Task<(bool Ok, IResult? Error, Guid ActorId)> CheckActiveActorAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, HttpContext http, Guid sellerId, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId))
        {
            return (false, Results.Unauthorized(), Guid.Empty);
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, role, status, seller_id FROM payment.v2_sp_get_actor(@id)";
        cmd.Parameters.AddWithValue("id", actorId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return (false, Results.Unauthorized(), Guid.Empty);
        }

        var role = reader.GetString(1);
        var status = reader.GetString(2);
        var userSellerId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3);

        if (status != "ACTIVE")
        {
            return (false, Results.Problem(statusCode: 403, title: "Tài khoản bị khóa", detail: "Tài khoản người dùng không ở trạng thái hoạt động."), actorId);
        }

        if (role == "ADMIN") return (true, null, actorId);

        if (role == "SELLER")
        {
            if (userSellerId != sellerId)
            {
                return (false, Results.Problem(statusCode: 403, title: "Quyền truy cập bị từ chối", detail: "Chỉ chủ cửa hàng mới có quyền xem thông tin thanh toán của mình."), actorId);
            }
            return (true, null, actorId);
        }

        return (false, Results.Problem(statusCode: 403, title: "Quyền truy cập bị từ chối", detail: "Vai trò không có quyền truy cập."), actorId);
    }

    public static void MapSellerPayments(this IEndpointRouteBuilder app)
    {
        // 1. GET /api/sellers/{sellerId}/payments (filter, page)
        app.MapGet("/api/sellers/{sellerId:guid}/payments",
            async (Guid sellerId, HttpContext http, NpgsqlDataSource source,
                   string? status, long? orderCode, DateTime? from, DateTime? to,
                   int? page, int? pageSize, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                await using var connection = await source.OpenConnectionAsync(ct);

                var (actorOk, actorError, _) = await CheckActiveActorAsync(connection, null, http, sellerId, ct);
                if (!actorOk) return actorError!;

                var currentPage = Math.Max(1, page ?? 1);
                var size = Math.Clamp(pageSize ?? 20, 1, 100);
                var offset = (currentPage - 1) * size;

                await using var countCmd = connection.CreateCommand();
                countCmd.CommandText = """
                    SELECT COUNT(*)
                    FROM payment.payments p
                    LEFT JOIN payment.gateway_orders g ON g.payment_id = p.id
                    WHERE p.seller_id = @sellerId AND p.kind = 'CHARGE'
                      AND (@status IS NULL OR p.status = @status)
                      AND (@orderCode IS NULL OR g.order_code = @orderCode)
                      AND (@from IS NULL OR p.created_at >= @from)
                      AND (@to IS NULL OR p.created_at <= @to)
                    """;
                countCmd.Parameters.AddWithValue("sellerId", sellerId);
                countCmd.Parameters.Add("status", NpgsqlDbType.Text).Value = (object?)status ?? DBNull.Value;
                countCmd.Parameters.Add("orderCode", NpgsqlDbType.Bigint).Value = (object?)orderCode ?? DBNull.Value;
                countCmd.Parameters.Add("from", NpgsqlDbType.TimestampTz).Value = (object?)from ?? DBNull.Value;
                countCmd.Parameters.Add("to", NpgsqlDbType.TimestampTz).Value = (object?)to ?? DBNull.Value;

                var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

                await using var queryCmd = connection.CreateCommand();
                queryCmd.CommandText = """
                    SELECT p.id, g.order_code, p.amount, p.status, p.paid_at, p.created_at,
                           p.channel_version, p.receiver_snapshot->>'accountNumber', p.checkout_id
                    FROM payment.payments p
                    LEFT JOIN payment.gateway_orders g ON g.payment_id = p.id
                    WHERE p.seller_id = @sellerId AND p.kind = 'CHARGE'
                      AND (@status IS NULL OR p.status = @status)
                      AND (@orderCode IS NULL OR g.order_code = @orderCode)
                      AND (@from IS NULL OR p.created_at >= @from)
                      AND (@to IS NULL OR p.created_at <= @to)
                    ORDER BY p.created_at DESC, p.id DESC
                    LIMIT @limit OFFSET @offset
                    """;
                queryCmd.Parameters.AddWithValue("sellerId", sellerId);
                queryCmd.Parameters.Add("status", NpgsqlDbType.Text).Value = (object?)status ?? DBNull.Value;
                queryCmd.Parameters.Add("orderCode", NpgsqlDbType.Bigint).Value = (object?)orderCode ?? DBNull.Value;
                queryCmd.Parameters.Add("from", NpgsqlDbType.TimestampTz).Value = (object?)from ?? DBNull.Value;
                queryCmd.Parameters.Add("to", NpgsqlDbType.TimestampTz).Value = (object?)to ?? DBNull.Value;
                queryCmd.Parameters.AddWithValue("limit", size);
                queryCmd.Parameters.AddWithValue("offset", offset);

                var items = new List<SellerPaymentListItem>();
                await using var reader = await queryCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetGuid(0);
                    var oc = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                    var amt = reader.GetInt64(2);
                    var st = reader.GetString(3);
                    var paidAt = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4);
                    var createdAt = reader.GetDateTime(5);
                    var chVer = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
                    var rawAccount = reader.IsDBNull(7) ? null : reader.GetString(7);
                    var checkoutId = reader.IsDBNull(8) ? (Guid?)null : reader.GetGuid(8);

                    items.Add(new SellerPaymentListItem(
                        id, oc, amt, st, paidAt, createdAt, chVer,
                        SellerPaymentChannels.MaskAccountNumber(rawAccount), checkoutId));
                }

                return Results.Ok(new SellerPaymentsListResponse(items, totalCount, currentPage, size));
            }).RequireAuthorization("Merchant", "SameSeller").WithTags("Payment").Produces<SellerPaymentsListResponse>();

        // 2. GET /api/sellers/{sellerId}/payments/{paymentId} (detail)
        app.MapGet("/api/sellers/{sellerId:guid}/payments/{paymentId:guid}",
            async (Guid sellerId, Guid paymentId, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                await using var connection = await source.OpenConnectionAsync(ct);

                var (actorOk, actorError, _) = await CheckActiveActorAsync(connection, null, http, sellerId, ct);
                if (!actorOk) return actorError!;

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT p.id, p.seller_id, p.channel_id, p.channel_version, g.order_code, p.amount,
                           p.status, p.purpose, p.gateway, p.gateway_txn_id, p.receiver_snapshot,
                           p.paid_at, p.created_at, p.updated_at, p.checkout_id
                    FROM payment.payments p
                    LEFT JOIN payment.gateway_orders g ON g.payment_id = p.id
                    WHERE p.id = @paymentId AND p.seller_id = @sellerId
                    """;
                cmd.Parameters.AddWithValue("paymentId", paymentId);
                cmd.Parameters.AddWithValue("sellerId", sellerId);

                Guid id; Guid sId; Guid? chId; int? chVer; long? orderCode; long amount;
                string status; string purpose; string gateway; string? gatewayTxnId;
                string? snapshotRaw; DateTime? paidAt; DateTime createdAt; DateTime updatedAt;
                Guid? checkoutId;

                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct))
                    {
                        return Results.NotFound(new { message = "Không tìm thấy giao dịch thanh toán." });
                    }
                    id = reader.GetGuid(0);
                    sId = reader.GetGuid(1);
                    chId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
                    chVer = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                    orderCode = reader.IsDBNull(4) ? null : reader.GetInt64(4);
                    amount = reader.GetInt64(5);
                    status = reader.GetString(6);
                    purpose = reader.GetString(7);
                    gateway = reader.GetString(8);
                    gatewayTxnId = reader.IsDBNull(9) ? null : reader.GetString(9);
                    snapshotRaw = reader.IsDBNull(10) ? null : reader.GetString(10);
                    paidAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11);
                    createdAt = reader.GetDateTime(12);
                    updatedAt = reader.GetDateTime(13);
                    checkoutId = reader.IsDBNull(14) ? null : reader.GetGuid(14);
                }

                object? maskedSnapshot = null;
                if (!string.IsNullOrWhiteSpace(snapshotRaw))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(snapshotRaw);
                        var root = doc.RootElement;
                        var bin = root.TryGetProperty("bin", out var b) ? b.GetString() : null;
                        var rawAcc = root.TryGetProperty("accountNumber", out var a) ? a.GetString() : null;
                        var name = root.TryGetProperty("accountName", out var n) ? n.GetString() : null;
                        var prov = root.TryGetProperty("provider", out var pr) ? pr.GetString() : "PAYOS";

                        maskedSnapshot = new
                        {
                            bin,
                            accountNumberMasked = SellerPaymentChannels.MaskAccountNumber(rawAcc),
                            accountName = name,
                            provider = prov
                        };
                    }
                    catch (JsonException) { }
                }

                // Query associated orders
                var orders = new List<CheckoutOrderResponse>();
                if (checkoutId.HasValue)
                {
                    await using var orderCmd = connection.CreateCommand();
                    orderCmd.CommandText = """
                        SELECT id, order_code, status, total_amount, tracking_token, points_redeemed, discount_amount
                        FROM payment.v2_sp_get_checkout_orders(@checkoutId)
                        """;
                    orderCmd.Parameters.AddWithValue("checkoutId", checkoutId.Value);
                    await using var oReader = await orderCmd.ExecuteReaderAsync(ct);
                    while (await oReader.ReadAsync(ct))
                    {
                        orders.Add(new CheckoutOrderResponse(
                            oReader.GetGuid(0),
                            oReader.GetString(1),
                            oReader.GetString(2),
                            oReader.GetInt64(3),
                            oReader.GetString(4),
                            oReader.GetInt64(5),
                            oReader.GetInt64(6)
                        ));
                    }
                }

                var reconciled = status == "SUCCEEDED" && !string.IsNullOrWhiteSpace(gatewayTxnId);
                var detail = new SellerPaymentDetailResponse(
                    id, sId, chId, chVer, orderCode, amount, status, purpose, gateway,
                    gatewayTxnId, maskedSnapshot, paidAt, createdAt, updatedAt, reconciled, orders);

                return Results.Ok(detail);
            }).RequireAuthorization("Merchant", "SameSeller").WithTags("Payment").Produces<SellerPaymentDetailResponse>();

        // 3. POST /api/sellers/{sellerId}/payments/{paymentId}/reconcile (signed provider read)
        app.MapPost("/api/sellers/{sellerId:guid}/payments/{paymentId:guid}/reconcile",
            async (Guid sellerId, Guid paymentId, HttpContext http, NpgsqlDataSource source,
                   IPayOsClientResolver resolver, ICapPublisher publisher, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                await using var connection = await source.OpenConnectionAsync(ct);

                var (actorOk, actorError, actorId) = await CheckActiveActorAsync(connection, null, http, sellerId, ct);
                if (!actorOk) return actorError!;

                // Advisory lock to serialize concurrent reconciliations on the same payment
                await using var gate = connection.CreateCommand();
                gate.CommandText = "SELECT pg_advisory_xact_lock(hashtext('reconcile:' || @pid::text))";
                gate.Parameters.AddWithValue("pid", paymentId);
                await gate.ExecuteNonQueryAsync(ct);

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT p.id, p.checkout_id, p.amount, p.status, p.gateway_txn_id, g.order_code,
                           cv.secret_ref, cv.expected_bank_bin, cv.expected_bank_account
                    FROM payment.payments p
                    JOIN payment.gateway_orders g ON g.payment_id = p.id
                    LEFT JOIN payment.payment_channel_versions cv ON cv.id = p.channel_id
                    WHERE p.id = @paymentId AND p.seller_id = @sellerId AND p.kind = 'CHARGE'
                    """;
                cmd.Parameters.AddWithValue("paymentId", paymentId);
                cmd.Parameters.AddWithValue("sellerId", sellerId);

                Guid id; Guid checkoutId; long amount; string previousStatus;
                string? existingTxn; long orderCode; string? secretRef;
                string? expectedBin; string? expectedAccount;

                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct))
                    {
                        return Results.NotFound(new { message = "Không tìm thấy giao dịch thanh toán." });
                    }
                    id = reader.GetGuid(0);
                    checkoutId = reader.GetGuid(1);
                    amount = reader.GetInt64(2);
                    previousStatus = reader.GetString(3);
                    existingTxn = reader.IsDBNull(4) ? null : reader.GetString(4);
                    orderCode = reader.GetInt64(5);
                    secretRef = reader.IsDBNull(6) ? null : reader.GetString(6);
                    expectedBin = reader.IsDBNull(7) ? null : reader.GetString(7);
                    expectedAccount = reader.IsDBNull(8) ? null : reader.GetString(8);
                }

                if (string.IsNullOrWhiteSpace(secretRef) || expectedBin is null || expectedAccount is null ||
                    !resolver.TryResolveChannelConfig(secretRef, out var channelConfig) || channelConfig is null ||
                    !channelConfig.MatchesReceiver(sellerId, expectedBin, expectedAccount))
                {
                    return Results.Problem(statusCode: 503, title: "Kênh thanh toán đã gắn vào giao dịch không khả dụng trên máy chủ.");
                }

                // Query signed provider authority using pinned channel
                var client = resolver.ResolveClient(channelConfig.Options);
                JsonElement providerData;
                try
                {
                    providerData = await client.GetPaymentRequestAsync(orderCode, ct);
                }
                catch (PaymentGatewayException ex)
                {
                    return Results.Problem(statusCode: 502, title: "Không thể tra cứu trạng thái giao dịch từ cổng thanh toán.", detail: ex.FailureCode);
                }

                var providerStatus = providerData.TryGetProperty("status", out var ps) ? ps.GetString() ?? "UNKNOWN" : "UNKNOWN";
                var providerAmount = providerData.TryGetProperty("amount", out var pa) ? pa.GetInt64() : 0;
                var providerOrderCode = providerData.TryGetProperty("orderCode", out var poc) ? poc.GetInt64() : 0;

                if (providerOrderCode != orderCode)
                {
                    return Results.Problem(statusCode: 422, title: "Mã đơn hàng tại cổng thanh toán không khớp với giao dịch.", detail: "order_code_mismatch");
                }

                // If provider GET returned account, validate it matches expected account
                if (expectedAccount is not null && providerData.TryGetProperty("accountNumber", out var returnedAcc))
                {
                    var accStr = returnedAcc.GetString();
                    if (!string.IsNullOrWhiteSpace(accStr) && !string.Equals(accStr.Trim(), expectedAccount.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.Problem(statusCode: 422, title: "Số tài khoản nhận tiền tại cổng thanh toán không khớp với tài khoản người bán.", detail: "receiver_account_mismatch");
                    }
                }

                var transitioned = false;
                var currentStatus = previousStatus;
                string? txnRef = null;

                if (providerStatus == "PAID" && providerAmount == amount)
                {
                    // Extract real provider transaction reference from provider authority; do not invent reference!
                    if (providerData.TryGetProperty("transactions", out var txns) && txns.ValueKind == JsonValueKind.Array && txns.GetArrayLength() > 0)
                    {
                        foreach (var txn in txns.EnumerateArray())
                        {
                            if (txn.TryGetProperty("reference", out var refProp) && refProp.ValueKind == JsonValueKind.String)
                            {
                                var rVal = refProp.GetString();
                                if (!string.IsNullOrWhiteSpace(rVal)) { txnRef = rVal; break; }
                            }
                        }
                    }

                    txnRef ??= existingTxn;
                    if (string.IsNullOrWhiteSpace(txnRef))
                    {
                        return Results.Problem(statusCode: 422, title: "Không tìm thấy mã giao dịch đối soát từ nhà cung cấp.", detail: "missing_provider_reference");
                    }

                    if (previousStatus != "SUCCEEDED")
                    {
                        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
                        var tx = (NpgsqlTransaction)outbox.DbTransaction!;

                        await using var settleCmd = new NpgsqlCommand("SELECT flow.checkout_paid(@checkout, @txn, true, @amount)", connection, tx);
                        settleCmd.Parameters.AddWithValue("checkout", checkoutId);
                        settleCmd.Parameters.AddWithValue("txn", txnRef);
                        settleCmd.Parameters.AddWithValue("amount", amount);
                        await settleCmd.ExecuteNonQueryAsync(ct);

                        await PaymentEvents.PublishSucceededAsync(connection, tx, publisher, id, ct);
                        await outbox.CommitAsync(ct);

                        transitioned = true;
                        currentStatus = "SUCCEEDED";
                    }
                }

                return Results.Ok(new ReconcilePaymentResponse(
                    id, previousStatus, currentStatus, providerStatus, amount, txnRef ?? existingTxn, transitioned));
            }).RequireAuthorization("Merchant", "SameSeller").WithTags("Payment").Produces<ReconcilePaymentResponse>();

        // 4. GET /api/sellers/{sellerId}/reports/sales (gross actual paid / refund / GMV named, no commission)
        app.MapGet("/api/sellers/{sellerId:guid}/reports/sales",
            async (Guid sellerId, HttpContext http, NpgsqlDataSource source,
                   DateTime? from, DateTime? to, Guid? kioskId, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                await using var connection = await source.OpenConnectionAsync(ct);

                var (actorOk, actorError, _) = await CheckActiveActorAsync(connection, null, http, sellerId, ct);
                if (!actorOk) return actorError!;

                await using var summaryCmd = connection.CreateCommand();
                summaryCmd.CommandText = """
                    SELECT gross_paid, total_refunded, paid_orders, unclaimed_orders, pending_amount
                    FROM payment.v2_sp_get_sales_summary(@sellerId, @from, @to, @kioskId)
                    """;
                summaryCmd.Parameters.AddWithValue("sellerId", sellerId);
                summaryCmd.Parameters.Add("from", NpgsqlDbType.TimestampTz).Value = (object?)from ?? DBNull.Value;
                summaryCmd.Parameters.Add("to", NpgsqlDbType.TimestampTz).Value = (object?)to ?? DBNull.Value;
                summaryCmd.Parameters.Add("kioskId", NpgsqlDbType.Uuid).Value = (object?)kioskId ?? DBNull.Value;

                long grossPaid = 0; long totalRefunded = 0; int paidOrders = 0; int unclaimed = 0; long pendingAmt = 0;
                await using (var reader = await summaryCmd.ExecuteReaderAsync(ct))
                {
                    if (await reader.ReadAsync(ct))
                    {
                        grossPaid = reader.GetInt64(0);
                        totalRefunded = reader.GetInt64(1);
                        paidOrders = Convert.ToInt32(reader.GetInt64(2));
                        unclaimed = Convert.ToInt32(reader.GetInt64(3));
                        pendingAmt = reader.GetInt64(4);
                    }
                }

                var netReceipts = grossPaid - totalRefunded;

                // Breakdown by kiosk
                await using var kioskCmd = connection.CreateCommand();
                kioskCmd.CommandText = """
                    SELECT id, code, name, gross_paid, order_count
                    FROM payment.v2_sp_get_sales_by_kiosk(@sellerId, @from, @to, @kioskId)
                    """;
                kioskCmd.Parameters.AddWithValue("sellerId", sellerId);
                kioskCmd.Parameters.Add("from", NpgsqlDbType.TimestampTz).Value = (object?)from ?? DBNull.Value;
                kioskCmd.Parameters.Add("to", NpgsqlDbType.TimestampTz).Value = (object?)to ?? DBNull.Value;
                kioskCmd.Parameters.Add("kioskId", NpgsqlDbType.Uuid).Value = (object?)kioskId ?? DBNull.Value;

                var byKiosk = new List<KioskSalesSummary>();
                await using var kReader = await kioskCmd.ExecuteReaderAsync(ct);
                while (await kReader.ReadAsync(ct))
                {
                    byKiosk.Add(new KioskSalesSummary(
                        kReader.GetGuid(0),
                        kReader.GetString(1),
                        kReader.GetString(2),
                        kReader.GetInt64(3),
                        Convert.ToInt32(kReader.GetInt64(4))
                    ));
                }

                var report = new SellerSalesReportResponse(
                    sellerId, from, to, grossPaid, totalRefunded, netReceipts,
                    paidOrders, unclaimed, pendingAmt, byKiosk);

                return Results.Ok(report);
            }).RequireAuthorization("Merchant", "SameSeller").WithTags("Payment").Produces<SellerSalesReportResponse>();
    }
}
