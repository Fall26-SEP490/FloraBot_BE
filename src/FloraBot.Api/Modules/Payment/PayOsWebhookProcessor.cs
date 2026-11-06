using System.Security.Cryptography;
using System.Text.Json;
using DotNetCore.CAP;
using Npgsql;
using StackExchange.Redis;

namespace FloraBot.Api.Modules.Payment;

public static class PayOsWebhookProcessor
{
    public static async Task<IResult> ProcessPaymentWebhookAsync(
        PayOsWebhook body,
        Guid? expectedChannelVersionId,
        NpgsqlDataSource source,
        IPayOsClientResolver resolver,
        IConfiguration config,
        ICapPublisher publisher,
        IConnectionMultiplexer redis,
        ILogger logger,
        CancellationToken ct)
    {
        // 1. Try to extract orderCode to resolve channel checksum key if direct payment
        string checksumKey = config["PAYOS_CHECKSUM_KEY"] ?? "";
        bool isDirect = false;
        string? expectedAccount = null;
        Guid? pinnedChannelId = null;

        if (body.Data.ValueKind == JsonValueKind.Object &&
            body.Data.TryGetProperty("orderCode", out var codeProp) &&
            codeProp.ValueKind == JsonValueKind.Number &&
            codeProp.TryGetInt64(out var orderCodeForLookup))
        {
            await using var chConn = await source.OpenConnectionAsync(ct);
            await using var chFind = chConn.CreateCommand();
            chFind.CommandText = """
                SELECT (p.is_direct_receipt OR p.funding_provenance='SELLER_DIRECT'), p.channel_id,
                       cv.secret_ref, cv.expected_bank_account, cv.expected_bank_bin, p.seller_id
                FROM payment.payments p
                JOIN payment.gateway_orders g ON g.payment_id=p.id
                LEFT JOIN payment.payment_channel_versions cv ON cv.id=p.channel_id
                WHERE p.kind='CHARGE' AND p.gateway='PAYOS' AND g.order_code=@code
                """;
            chFind.Parameters.AddWithValue("code", orderCodeForLookup);
            await using var chReader = await chFind.ExecuteReaderAsync(ct);
            if (await chReader.ReadAsync(ct))
            {
                isDirect = chReader.GetBoolean(0);
                pinnedChannelId = chReader.IsDBNull(1) ? null : chReader.GetGuid(1);
                var sRef = chReader.IsDBNull(2) ? null : chReader.GetString(2);
                expectedAccount = chReader.IsDBNull(3) ? null : chReader.GetString(3);

                if (isDirect)
                {
                    if (pinnedChannelId is null || string.IsNullOrWhiteSpace(sRef) || expectedAccount is null ||
                        chReader.IsDBNull(4) || chReader.IsDBNull(5) ||
                        !resolver.TryResolveChannelConfig(sRef, out var chConfig) || chConfig is null ||
                        !chConfig.MatchesReceiver(chReader.GetGuid(5), chReader.GetString(4), expectedAccount))
                    {
                        logger.LogWarning("Cannot verify seller payOS callback for order {OrderCode}: pinned channel configuration unavailable", orderCodeForLookup);
                        return Results.Problem(statusCode: 503, title: "Kênh thanh toán của người bán tạm thời không khả dụng.");
                    }
                    checksumKey = chConfig.Options.ChecksumKey;
                }
            }
        }

        // 2. Seller receipts use their pinned key. Platform and historical charges use the platform key.
        if (!PayOsChecksum.Verify(body.Data, body.Signature, checksumKey))
        {
            logger.LogWarning("Rejected payOS webhook: checksum verification failed");
            await using var auditConn = await source.OpenConnectionAsync(ct);
            await using var auditCmd = auditConn.CreateCommand();
            auditCmd.CommandText = "SELECT flow.audit(NULL, 'WEBHOOK_INVALID_CHECKSUM', 'payments', NULL)";
            await auditCmd.ExecuteNonQueryAsync(ct);
            return Results.BadRequest(new { message = "Checksum không hợp lệ." });
        }

        // 3. Field validation
        if (body.Data.ValueKind != JsonValueKind.Object ||
            !body.Data.TryGetProperty("orderCode", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var orderCode) ||
            !body.Data.TryGetProperty("amount", out var amountValue) || amountValue.ValueKind != JsonValueKind.Number || !amountValue.TryGetInt64(out var amount) ||
            !body.Data.TryGetProperty("reference", out var referenceValue) || referenceValue.ValueKind != JsonValueKind.String ||
            !body.Data.TryGetProperty("code", out var statusValue) || statusValue.ValueKind != JsonValueKind.String)
        {
            logger.LogWarning("Rejected payOS webhook: required data fields are missing or have invalid types");
            return Results.BadRequest();
        }

        var reference = referenceValue.GetString();
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 200)
        {
            logger.LogWarning("Rejected payOS webhook: transaction reference is empty or too long");
            return Results.BadRequest();
        }

        // 4. Channel version matching: if callback arrived at a version-bound endpoint, it MUST match the pinned channel!
        if (expectedChannelVersionId.HasValue && pinnedChannelId != expectedChannelVersionId.Value)
        {
            logger.LogWarning("Rejected version-bound webhook: payment channel {Pinned} does not match endpoint channel {Expected}",
                pinnedChannelId, expectedChannelVersionId.Value);
            return Results.BadRequest(new { message = "Kênh thanh toán không khớp với giao dịch." });
        }

        // 5. Mandatory receiver account check on direct receipts!
        if (isDirect)
        {
            if (string.IsNullOrWhiteSpace(expectedAccount))
            {
                logger.LogWarning("Rejected direct payment webhook: expected account missing on channel");
                return Results.BadRequest(new { message = "Tài khoản nhận tiền không hợp lệ." });
            }

            if (!body.Data.TryGetProperty("accountNumber", out var accProp) ||
                accProp.ValueKind != JsonValueKind.String ||
                !string.Equals(accProp.GetString()?.Trim(), expectedAccount.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Rejected direct payment webhook for order {OrderCode}: receiver account mismatch or missing", orderCode);
                return Results.BadRequest(new { message = "Tài khoản nhận tiền không khớp." });
            }
        }

        await using var connection = await source.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;

        // 6. Advisory lock on order code
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('payos:' || @code::text,0))", connection, transaction);
        gate.Parameters.AddWithValue("code", orderCode);
        await gate.ExecuteNonQueryAsync(ct);

        // 7. Find payment, purpose, amounts, and provenance
        await using var find = new NpgsqlCommand("""
            SELECT p.id, p.purpose, p.checkout_id, p.subscription_id, p.amount, p.status,
                   p.funding_provenance::text
            FROM payment.payments p
            JOIN payment.gateway_orders g ON g.payment_id=p.id
            WHERE p.kind='CHARGE' AND p.gateway='PAYOS' AND g.order_code=@code
            """, connection, transaction);
        find.Parameters.AddWithValue("code", orderCode);

        Guid paymentId; string purpose; Guid target; long expected; bool alreadySucceeded;
        string? fundingProvenance;

        await using (var reader = await find.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                logger.LogWarning("Rejected payOS webhook: no payment maps to order code {OrderCode}", orderCode);
                return Results.BadRequest(new { message = "Không tìm thấy khoản thanh toán." });
            }
            paymentId = reader.GetGuid(0);
            purpose = reader.GetString(1);
            var targetOrdinal = purpose == "SUBSCRIPTION" ? 3 : 2;
            target = !reader.IsDBNull(targetOrdinal) ? reader.GetGuid(targetOrdinal) : Guid.Empty;
            expected = reader.GetInt64(4);
            alreadySucceeded = reader.GetString(5) == "SUCCEEDED";
            fundingProvenance = reader.IsDBNull(6) ? null : reader.GetString(6);
            if (await reader.ReadAsync(ct)) throw new InvalidOperationException("Duplicate gateway order code.");
        }

        // Cutover guard: new flower orders in DIRECT_REQUIRED cannot pay before direct binding
        if (fundingProvenance == "DIRECT_REQUIRED")
        {
            logger.LogWarning("Rejected payOS webhook: order {OrderCode} is DIRECT_REQUIRED and cannot pay before channel binding", orderCode);
            return Results.BadRequest(new { message = "Giao dịch mua hoa chưa được liên kết kênh thanh toán trực tiếp." });
        }

        // 8. Verify Amount
        if (amount != expected)
        {
            logger.LogWarning("Rejected payOS webhook for order code {OrderCode}: amount {Amount} != expected {Expected}", orderCode, amount, expected);
            return Results.BadRequest(new { message = "Số tiền thanh toán không khớp." });
        }

        var success = statusValue.GetString() == "00";
        if ((purpose == "SUBSCRIPTION" || purpose == "KIOSK_APPLICATION") && !success)
        {
            logger.LogWarning("Rejected unsuccessful payOS webhook for payment purpose {Purpose}", purpose);
            return Results.BadRequest(new { message = "Giao dịch không thành công." });
        }

        // 9. Execute SQL flow
        await using var command = new NpgsqlCommand(
            purpose == "SUBSCRIPTION" ? "SELECT flow.subscription_paid(@id,@txn)" :
            purpose == "KIOSK_APPLICATION" ? "SELECT flow.kiosk_application_paid(@id,@txn)" :
            "SELECT flow.checkout_paid(@id,@txn,@success,@amount)", connection, transaction);
        command.Parameters.AddWithValue("id", purpose == "KIOSK_APPLICATION" ? paymentId : target);
        command.Parameters.AddWithValue("txn", reference);
        if (purpose != "SUBSCRIPTION" && purpose != "KIOSK_APPLICATION")
        {
            command.Parameters.AddWithValue("success", success);
            command.Parameters.AddWithValue("amount", amount);
        }
        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex)
        {
            logger.LogWarning(ex, "SQL error in payOS webhook execution: SqlState {SqlState}, Table {Table}, Constraint {Constraint}, Message {Message}",
                ex.SqlState, ex.TableName, ex.ConstraintName, ex.MessageText);
            throw;
        }

        if (!alreadySucceeded) await PaymentEvents.PublishSucceededAsync(connection, transaction, publisher, paymentId, ct);
        await outbox.CommitAsync(ct);
        await connection.CloseAsync();

        try
        {
            await redis.GetDatabase().StringSetAsync($"webhook:{paymentId}:{reference}", "committed", TimeSpan.FromDays(1));
        }
        catch (RedisException)
        {
            logger.LogWarning("Payment {PaymentId} committed; optional webhook cache marker unavailable", paymentId);
        }

        return Results.Ok(new { success = true });
    }
}
