using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Payment;

internal static class SellerCheckoutPaymentLinks
{
    // The caller has already verified access to this checkout. Take the charge lock before binding or reserving a code.
    internal static async Task<IResult> CreateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid checkoutId, DateTime? kioskDeadline, IPayOsClientResolver resolver, ILogger logger, CancellationToken ct)
    {
        Guid paymentId;
        Guid? channelId;
        long amount;
        DateTime deadline;
        string provenance;
        await using (var find = new NpgsqlCommand("""
            SELECT id, amount, channel_id, funding_provenance::text, created_at + flow.cfg_min('hold_minutes')
            FROM payment.payments
            WHERE checkout_id=@checkout AND kind='CHARGE' AND purpose='ORDER_CHECKOUT' AND gateway='PAYOS' AND status='PENDING'
            FOR UPDATE
            """, connection, transaction))
        {
            find.Parameters.AddWithValue("checkout", checkoutId);
            await using var row = await find.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) return Results.NotFound();
            paymentId = row.GetGuid(0);
            amount = row.GetInt64(1);
            channelId = row.IsDBNull(2) ? null : row.GetGuid(2);
            provenance = row.GetString(3);
            deadline = kioskDeadline ?? row.GetDateTime(4);
        }

        if (deadline <= DateTime.UtcNow) return CheckoutClosed();
        if (provenance == "PLATFORM_LEGACY") return LegacyCheckout();
        if (provenance != (channelId.HasValue ? "SELLER_DIRECT" : "DIRECT_REQUIRED")) return ChannelUnavailable();

        await using var sellers = new NpgsqlCommand("SELECT ARRAY(SELECT seller_id FROM payment.v2_link_get_checkout_sellers(@checkout))", connection, transaction);
        sellers.Parameters.AddWithValue("checkout", checkoutId);
        var sellerIds = (Guid[])(await sellers.ExecuteScalarAsync(ct))!;
        if (sellerIds.Length == 0) return Results.NotFound();
        if (sellerIds.Length > 1)
            return Results.Problem(statusCode: 400, title: "Giỏ hàng có nhiều người bán khác nhau.", detail: "mixed_seller_checkout");
        var sellerId = sellerIds[0];

        Guid versionId;
        int version;
        string secretRef, bankBin, account, state;
        string? accountName;
        bool allowVirtualAccounts;
        string? boundFingerprint, verifiedFingerprint;
        await using (var find = new NpgsqlCommand("""
            SELECT id, version, secret_ref, state, expected_bank_bin, expected_bank_account, expected_bank_name,
                   allow_virtual_accounts, bound_credential_fingerprint, verified_credential_fingerprint
            FROM payment.payment_channel_versions
            WHERE seller_id=@seller AND (@channel IS NULL OR id=@channel)
            ORDER BY version DESC LIMIT 1
            """, connection, transaction))
        {
            find.Parameters.AddWithValue("seller", sellerId);
            find.Parameters.Add("channel", NpgsqlDbType.Uuid).Value = (object?)channelId ?? DBNull.Value;
            await using var row = await find.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct))
                return channelId.HasValue ? ChannelUnavailable() :
                    Results.Problem(statusCode: 503, title: "Người bán chưa cấu hình kênh thanh toán trực tiếp.", detail: "missing_seller_channel");
            versionId = row.GetGuid(0);
            version = row.GetInt32(1);
            secretRef = row.GetString(2);
            state = row.GetString(3);
            bankBin = row.GetString(4);
            account = row.GetString(5);
            accountName = row.IsDBNull(6) ? null : row.GetString(6);
            allowVirtualAccounts = row.GetBoolean(7);
            boundFingerprint = row.IsDBNull(8) ? null : row.GetString(8);
            verifiedFingerprint = row.IsDBNull(9) ? null : row.GetString(9);
        }

        if (!channelId.HasValue && state != "ACTIVE")
            return Results.Problem(statusCode: 409, title: "Kênh thanh toán của người bán đang tạm dừng hoặc chưa sẵn sàng.", detail: "channel_not_active");
        if (!resolver.TryResolveChannelConfig(secretRef, out var config) || config is null ||
            !config.MatchesReceiver(sellerId, bankBin, account)) return ChannelUnavailable();
        if (boundFingerprint is not null && !PayOsReceiverEvidence.MatchesFingerprint(config.Options, boundFingerprint))
            return ChannelUnavailable();
        var virtualReady = allowVirtualAccounts && config.Options.AllowVirtualAccounts &&
            PayOsReceiverEvidence.MatchesFingerprint(config.Options, boundFingerprint) &&
            PayOsReceiverEvidence.MatchesFingerprint(config.Options, verifiedFingerprint);
        if (allowVirtualAccounts && !virtualReady) return ChannelUnavailable();
        var gateway = resolver.ResolveClient(config.Options);
        if (!gateway.IsConfigured) return ChannelUnavailable();

        if (!channelId.HasValue)
        {
            await using var pin = new NpgsqlCommand("""
                UPDATE payment.payments
                SET seller_id=@seller, channel_id=@channel, channel_version=@version, receiver_snapshot=@receiver,
                    funding_provenance='SELLER_DIRECT', is_direct_receipt=true
                WHERE id=@id AND funding_provenance='DIRECT_REQUIRED' AND channel_id IS NULL AND status='PENDING'
                """, connection, transaction);
            pin.Parameters.AddWithValue("id", paymentId);
            pin.Parameters.AddWithValue("seller", sellerId);
            pin.Parameters.AddWithValue("channel", versionId);
            pin.Parameters.AddWithValue("version", version);
            pin.Parameters.Add("receiver", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(new
            {
                bin = bankBin,
                accountNumber = account,
                accountName,
                provider = "PAYOS"
            });
            if (await pin.ExecuteNonQueryAsync(ct) != 1) return CheckoutClosed();
        }

        var maskedAccount = SellerPaymentChannels.MaskAccountNumber(account);
        PaymentLinkResponse? existing = null;
        await using (var read = new NpgsqlCommand("SELECT order_code, checkout_url, qr_code, verified_seller_channel_id, receiver_account, receiver_account_name FROM payment.gateway_orders WHERE payment_id=@id", connection, transaction))
        {
            read.Parameters.AddWithValue("id", paymentId);
            await using var row = await read.ExecuteReaderAsync(ct);
            if (await row.ReadAsync(ct) && !row.IsDBNull(1))
            {
                if (row.IsDBNull(3) || row.GetGuid(3) != versionId) return LegacyCheckout();
                existing = new(row.GetInt64(0), amount, row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2),
                    row.IsDBNull(5) ? accountName : row.GetString(5), row.IsDBNull(4) ? maskedAccount : SellerPaymentChannels.MaskAccountNumber(row.GetString(4)));
            }
        }

        if (existing is not null)
        {
            await transaction.CommitAsync(ct);
            return Results.Ok(existing);
        }

        await using var reserve = new NpgsqlCommand("SELECT flow.reserve_gateway_order(@id)", connection, transaction);
        reserve.Parameters.AddWithValue("id", paymentId);
        var orderCode = (long)(await reserve.ExecuteScalarAsync(ct))!;
        await transaction.CommitAsync(ct);

        try
        {
            var link = await gateway.CreateForVerifiedSellerAsync(orderCode, amount, ct, new DateTimeOffset(deadline).ToUnixTimeSeconds(), virtualReady);
            // Payment or cancellation may have won while the gateway was creating the link.
            await using var save = new NpgsqlCommand("""
                UPDATE payment.gateway_orders g SET checkout_url=@url, qr_code=@qr, verified_seller_channel_id=@channel,
                    receiver_bin=@receiverBin, receiver_account=@receiverAccount, receiver_account_name=@receiverName,
                    receiver_mode=@receiverMode, receiver_verified_at=@receiverAt
                FROM (SELECT id FROM payment.payments WHERE id=@id AND channel_id=@channel AND status='PENDING' FOR UPDATE) pending
                WHERE g.payment_id=pending.id AND g.order_code=@code
                  AND (g.checkout_url IS NULL OR
                       (g.checkout_url=@url AND g.receiver_account IS NOT DISTINCT FROM @receiverAccount
                        AND g.receiver_bin IS NOT DISTINCT FROM @receiverBin AND g.receiver_mode IS NOT DISTINCT FROM @receiverMode))
                RETURNING g.qr_code
                """, connection);
            save.Parameters.AddWithValue("id", paymentId);
            save.Parameters.AddWithValue("code", orderCode);
            save.Parameters.AddWithValue("channel", versionId);
            save.Parameters.AddWithValue("url", link.CheckoutUrl);
            save.Parameters.Add("qr", NpgsqlDbType.Text).Value = (object?)link.QrCode ?? DBNull.Value;
            save.Parameters.Add("receiverBin", NpgsqlDbType.Text).Value = (object?)link.ReceiverEvidence?.Bin ?? DBNull.Value;
            save.Parameters.Add("receiverAccount", NpgsqlDbType.Text).Value = (object?)link.ReceiverEvidence?.AccountNumber ?? DBNull.Value;
            save.Parameters.Add("receiverName", NpgsqlDbType.Text).Value = (object?)link.ReceiverEvidence?.AccountName ?? DBNull.Value;
            save.Parameters.Add("receiverMode", NpgsqlDbType.Text).Value = (object?)link.ReceiverEvidence?.Mode ?? DBNull.Value;
            save.Parameters.Add("receiverAt", NpgsqlDbType.TimestampTz).Value = link.ReceiverEvidence is null ? DBNull.Value : DateTime.UtcNow;
            var stored = await save.ExecuteScalarAsync(ct);
            if (stored is null || deadline <= DateTime.UtcNow) return CheckoutClosed();
            return Results.Ok(link with
            {
                QrCode = stored as string,
                RecipientName = link.ReceiverEvidence?.AccountName ?? accountName,
                RecipientAccountMasked = link.ReceiverEvidence is null ? maskedAccount : SellerPaymentChannels.MaskAccountNumber(link.ReceiverEvidence.AccountNumber)
            });
        }
        catch (Exception ex) when (ex is PaymentGatewayException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Seller payOS link creation failed for checkout {CheckoutId}: {FailureCode}", checkoutId,
                ex is PaymentGatewayException gatewayError ? gatewayError.FailureCode : ex.GetType().Name);
            return Results.Problem(statusCode: 502, title: "Chưa lấy được liên kết thanh toán. Vui lòng kiểm tra trạng thái trước khi thử lại.");
        }
    }

    private static IResult CheckoutClosed() => Results.Problem(statusCode: 409,
        title: "Lượt mua đã hết hạn hoặc không còn chờ thanh toán. Vui lòng kiểm tra trạng thái đơn.", detail: "checkout_not_payable");

    private static IResult LegacyCheckout() => Results.Problem(statusCode: 409,
        title: "Lượt mua cũ không thể cấp lại mã thanh toán. Vui lòng kiểm tra đơn và chọn lại hoa nếu chưa thanh toán.", detail: "legacy_checkout");

    private static IResult ChannelUnavailable() => Results.Problem(statusCode: 503,
        title: "Kênh nhận tiền của người bán chưa khả dụng. Vui lòng liên hệ cửa hàng.", detail: "seller_channel_unavailable");
}
