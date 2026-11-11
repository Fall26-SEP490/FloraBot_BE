using System.Data;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNetCore.CAP;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace FloraBot.Api.Modules.Payment;

public sealed record BindPaymentChannelRequest(string SecretRef);
public sealed record UpdateChannelStatusRequest(string Status);

public sealed record SellerPaymentChannelResponse(
    Guid SellerId,
    int Version,
    string State,
    string ExpectedBankBin,
    string ExpectedBankAccountMasked,
    string? ExpectedBankName,
    DateTime? VerifiedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public sealed record AdminPaymentChannelItemResponse(
    Guid SellerId,
    string ShopName,
    int CurrentVersion,
    string State,
    string ExpectedBankBin,
    string ExpectedBankAccountMasked,
    string? ExpectedBankName,
    DateTime? VerifiedAt,
    DateTime UpdatedAt
);

public sealed record ChannelVerificationResponse(
    bool Verified,
    string State,
    string Message
);

public static class SellerPaymentChannels
{
    private static readonly Regex SecretRefPattern = new("^[A-Za-z0-9_-]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex BinPattern = new("^[0-9]{4,10}$", RegexOptions.Compiled);
    private static readonly Regex AccountPattern = new("^[0-9A-Za-z]{4,30}$", RegexOptions.Compiled);

    public static string MaskAccountNumber(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return "";
        var trimmed = account.Trim();
        if (trimmed.Length <= 4) return new string('*', trimmed.Length);
        return new string('*', trimmed.Length - 4) + trimmed[^4..];
    }

    private static async Task<(bool Ok, IResult? Error, Guid ActorId)> CheckActiveActorAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, HttpContext http, string[] allowedRoles, Guid? requiredSellerId, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId))
        {
            return (false, Results.Unauthorized(), Guid.Empty);
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, role, status, seller_id FROM payment.v2_cp_get_actor(@id)";
        cmd.Parameters.AddWithValue("id", actorId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return (false, Results.Unauthorized(), Guid.Empty);
        }

        var role = reader.GetString(1);
        var status = reader.GetString(2);
        var sellerId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3);

        if (status != "ACTIVE")
        {
            return (false, Results.Problem(statusCode: 403, title: "Tài khoản bị khóa", detail: "Tài khoản người dùng không ở trạng thái hoạt động."), actorId);
        }

        if (!allowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            return (false, Results.Problem(statusCode: 403, title: "Quyền truy cập bị từ chối", detail: "Vai trò không có quyền thực hiện thao tác này."), actorId);
        }

        if (role == "SELLER")
        {
            if (requiredSellerId.HasValue && sellerId != requiredSellerId.Value)
            {
                return (false, Results.Problem(statusCode: 403, title: "Quyền truy cập bị từ chối", detail: "Chỉ chủ cửa hàng mới có quyền thao tác trên kênh thanh toán của mình."), actorId);
            }
        }

        return (true, null, actorId);
    }

    public static void MapSellerPaymentChannels(this IEndpointRouteBuilder app)
    {
        // Version-bound webhook endpoint for payOS probe and callbacks
        app.MapPost("/api/payments/channels/{versionId:guid}/webhook",
            async (Guid versionId, [FromQuery] string? attempt, PayOsWebhook body, HttpContext http, NpgsqlDataSource source,
                   IPayOsClientResolver resolver, IConfiguration config, ICapPublisher publisher, IConnectionMultiplexer redis,
                   ILogger<PayOsClient> logger, CancellationToken ct) =>
            {
                // Check if this is the documented probe sample
                if (body.Code == "00" && body.Desc == "success" && body.Data.ValueKind == JsonValueKind.Object &&
                    body.Data.TryGetProperty("orderCode", out var oc) && oc.GetInt64() == 123 &&
                    body.Data.TryGetProperty("amount", out var am) && am.GetInt64() == 3000 &&
                    body.Data.TryGetProperty("description", out var ds) && ds.GetString() == "VQRIO123")
                {
                    var attemptNonce = attempt ?? http.Request.Query["attempt"].ToString();
                    if (string.IsNullOrWhiteSpace(attemptNonce))
                    {
                        logger.LogWarning("Rejected probe sample on channel {VersionId}: attempt nonce missing", versionId);
                        return Results.BadRequest(new { message = "Mã xác thực lần thử (attempt nonce) là bắt buộc." });
                    }

                    await using var connection = await source.OpenConnectionAsync(ct);
                    await using var tx = await connection.BeginTransactionAsync(ct);

                    await using var chCmd = connection.CreateCommand();
                    chCmd.Transaction = tx;
                    chCmd.CommandText = "SELECT id, seller_id, version, secret_ref, state, bound_credential_fingerprint FROM payment.payment_channel_versions WHERE id = @versionId FOR UPDATE";
                    chCmd.Parameters.AddWithValue("versionId", versionId);

                    Guid sellerId; int version; string secretRef; string state; string? boundFingerprint;
                    await using (var r = await chCmd.ExecuteReaderAsync(ct))
                    {
                        if (!await r.ReadAsync(ct)) return Results.NotFound(new { message = "Kênh thanh toán không tồn tại." });
                        sellerId = r.GetGuid(1);
                        version = r.GetInt32(2);
                        secretRef = r.GetString(3);
                        state = r.GetString(4);
                        boundFingerprint = r.IsDBNull(5) ? null : r.GetString(5);
                    }

                    // A paused channel cannot be activated by a probe
                    if (state == "PAUSED")
                    {
                        await tx.RollbackAsync(ct);
                        return Results.Conflict(new { message = "Kênh thanh toán đang tạm dừng, không thể kích hoạt bằng probe." });
                    }

                    if (!resolver.TryResolveChannelConfig(secretRef, out var chConfig) || chConfig is null)
                    {
                        await tx.RollbackAsync(ct);
                        return Results.BadRequest(new { message = "Cấu hình kênh không khả dụng." });
                    }

                    if (chConfig.SellerId != sellerId || boundFingerprint is not null &&
                        !PayOsReceiverEvidence.MatchesFingerprint(chConfig.Options, boundFingerprint))
                        return Results.BadRequest(new { message = "Cấu hình kênh đã thay đổi. Cần gắn lại phiên bản mới." });

                    // Verify HMAC using channel-pinned checksum key
                    if (!PayOsChecksum.Verify(body.Data, body.Signature, chConfig.Options.ChecksumKey))
                    {
                        logger.LogWarning("Rejected probe sample on channel {VersionId}: invalid checksum", versionId);
                        await tx.RollbackAsync(ct);
                        return Results.BadRequest(new { message = "Checksum không hợp lệ." });
                    }

                    // Look up exact pending attempt
                    await using var attemptCmd = connection.CreateCommand();
                    attemptCmd.Transaction = tx;
                    attemptCmd.CommandText = """
                        SELECT id, confirm_reply_received, credential_fingerprint
                        FROM payment.channel_verification_attempts
                        WHERE channel_version_id = @versionId AND attempt_token = @token AND status = 'PENDING' AND expires_at > @now
                        FOR UPDATE;
                        """;
                    attemptCmd.Parameters.AddWithValue("versionId", versionId);
                    attemptCmd.Parameters.AddWithValue("token", attemptNonce);
                    attemptCmd.Parameters.AddWithValue("now", DateTime.UtcNow);

                    Guid attemptId; bool confirmReplyReceived;
                    await using (var ar = await attemptCmd.ExecuteReaderAsync(ct))
                    {
                        if (!await ar.ReadAsync(ct))
                        {
                            logger.LogWarning("Rejected probe sample on channel {VersionId}: attempt nonce invalid or expired", versionId);
                            await tx.RollbackAsync(ct);
                            return Results.BadRequest(new { message = "Lần thử xác thực không hợp lệ hoặc đã hết hạn." });
                        }
                        attemptId = ar.GetGuid(0);
                        confirmReplyReceived = ar.GetBoolean(1);
                        if (ar.IsDBNull(2) || !PayOsReceiverEvidence.MatchesFingerprint(chConfig.Options, ar.GetString(2)))
                            return Results.BadRequest(new { message = "Thông tin xác thực đã thay đổi trong lần kiểm tra." });
                    }

                    var now = DateTime.UtcNow;
                    await using var updateAttempt = connection.CreateCommand();
                    updateAttempt.Transaction = tx;
                    updateAttempt.CommandText = "UPDATE payment.channel_verification_attempts SET sample_received = true, updated_at = @now WHERE id = @id";
                    updateAttempt.Parameters.AddWithValue("now", now);
                    updateAttempt.Parameters.AddWithValue("id", attemptId);
                    await updateAttempt.ExecuteNonQueryAsync(ct);

                    if (confirmReplyReceived)
                    {
                        // Both confirm reply and signed sample verified: activate channel atomically!
                        await using var actCmd = connection.CreateCommand();
                        actCmd.Transaction = tx;
                        actCmd.CommandText = """
                            UPDATE payment.payment_channel_versions
                            SET state = 'ACTIVE', verified_at = @now, updated_at = @now,
                                verified_credential_fingerprint = CASE WHEN bound_credential_fingerprint IS NULL THEN NULL ELSE @fingerprint END
                            WHERE id = @versionId AND state <> 'PAUSED';
                            UPDATE payment.channel_verification_attempts
                            SET status = 'SUCCEEDED', updated_at = @now
                            WHERE id = @attemptId;
                            """;
                        actCmd.Parameters.AddWithValue("now", now);
                        actCmd.Parameters.AddWithValue("versionId", versionId);
                        actCmd.Parameters.AddWithValue("attemptId", attemptId);
                        actCmd.Parameters.AddWithValue("fingerprint", PayOsReceiverEvidence.Fingerprint(chConfig.Options));
                        await actCmd.ExecuteNonQueryAsync(ct);

                        await using var auditCmd = connection.CreateCommand();
                        auditCmd.Transaction = tx;
                        auditCmd.CommandText = "SELECT flow.audit(NULL, 'SELLER_PAYMENT_CHANNEL_VERIFIED', 'payment_channel_versions', @id, jsonb_build_object('seller_id', @sellerId, 'version', @version))";
                        auditCmd.Parameters.AddWithValue("id", versionId);
                        auditCmd.Parameters.AddWithValue("sellerId", sellerId);
                        auditCmd.Parameters.AddWithValue("version", version);
                        await auditCmd.ExecuteNonQueryAsync(ct);
                    }

                    await tx.CommitAsync(ct);
                    return Results.Ok(new { success = true });
                }

                // If NOT a probe sample, process real payment callback through the complete transactional pipeline!
                return await PayOsWebhookProcessor.ProcessPaymentWebhookAsync(
                    body, versionId, source, resolver, config, publisher, redis, logger, ct);
            }).AllowAnonymous().WithTags("Payment");

        // 1. GET safe channel metadata for seller (Owner only or Admin; deny staff/customer)
        app.MapGet("/api/sellers/{sellerId:guid}/payment-channel",
            async (Guid sellerId, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                await using var connection = await source.OpenConnectionAsync(ct);

                var (actorOk, actorError, _) = await CheckActiveActorAsync(
                    connection, null, http, ["ADMIN", "SELLER"], sellerId, ct);
                if (!actorOk) return actorError!;

                await using var checkSeller = connection.CreateCommand();
                checkSeller.CommandText = "SELECT id FROM payment.v2_cp_get_seller(@sellerId)";
                checkSeller.Parameters.AddWithValue("sellerId", sellerId);
                if (await checkSeller.ExecuteScalarAsync(ct) is null)
                {
                    return Results.NotFound(new { message = "Không tìm thấy thông tin cửa hàng." });
                }

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT seller_id, version, state, expected_bank_bin, expected_bank_account, expected_bank_name, verified_at, created_at, updated_at
                    FROM payment.payment_channel_versions
                    WHERE seller_id = @sellerId
                    ORDER BY version DESC
                    LIMIT 1
                    """;
                cmd.Parameters.AddWithValue("sellerId", sellerId);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    return Results.NotFound(new { message = "Cửa hàng chưa có kênh thanh toán nào được cấu hình." });
                }

                var response = new SellerPaymentChannelResponse(
                    reader.GetGuid(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    MaskAccountNumber(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                    reader.GetDateTime(7),
                    reader.GetDateTime(8)
                );

                return Results.Ok(response);
            }).RequireAuthorization("Merchant", "SameSeller").WithTags("Payment").Produces<SellerPaymentChannelResponse>();

        // 2. PUT config binding (Admin-only, serializing seller row for update)
        app.MapPut("/api/sellers/{sellerId:guid}/payment-channel",
            async (Guid sellerId, BindPaymentChannelRequest body, HttpContext http, NpgsqlDataSource source, IPayOsClientResolver resolver, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";

                if (string.IsNullOrWhiteSpace(body.SecretRef) || !SecretRefPattern.IsMatch(body.SecretRef))
                {
                    return Results.BadRequest(new { message = "Mã tham chiếu cấu hình không đúng định dạng." });
                }

                if (!resolver.TryResolveChannelConfig(body.SecretRef, out var channelConfig) || channelConfig is null)
                {
                    return Results.BadRequest(new { message = "Cấu hình kênh không tồn tại hoặc không hợp lệ trên máy chủ." });
                }

                if (channelConfig.SellerId != sellerId)
                {
                    return Results.Problem(statusCode: 403, title: "Quyền truy cập bị từ chối", detail: "Cấu hình kênh tham chiếu không thuộc về cửa hàng này.");
                }

                var opt = channelConfig.Options;
                if (string.IsNullOrWhiteSpace(opt.ExpectedBin) || !BinPattern.IsMatch(opt.ExpectedBin) ||
                    string.IsNullOrWhiteSpace(opt.ExpectedAccount) || !AccountPattern.IsMatch(opt.ExpectedAccount) ||
                    string.IsNullOrWhiteSpace(opt.ExpectedName) || opt.ExpectedName.Length < 2 || opt.ExpectedName.Length > 120)
                {
                    return Results.BadRequest(new { message = "Thông tin ngân hàng cấu hình cho kênh không hợp lệ." });
                }

                await using var connection = await source.OpenConnectionAsync(ct);
                await using var tx = await connection.BeginTransactionAsync(ct);

                var (actorOk, actorError, actorId) = await CheckActiveActorAsync(
                    connection, tx, http, ["ADMIN"], null, ct);
                if (!actorOk)
                {
                    await tx.RollbackAsync(ct);
                    return actorError!;
                }

                // Serialize concurrent binds by locking the seller row FOR UPDATE
                await using var checkSeller = connection.CreateCommand();
                checkSeller.Transaction = tx;
                checkSeller.CommandText = "SELECT id, status FROM payment.v2_cp_lock_seller(@sellerId)";
                checkSeller.Parameters.AddWithValue("sellerId", sellerId);
                var sellerStatus = await checkSeller.ExecuteScalarAsync(ct);
                if (sellerStatus is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { message = "Không tìm thấy thông tin cửa hàng." });
                }

                await using var versionCmd = connection.CreateCommand();
                versionCmd.Transaction = tx;
                versionCmd.CommandText = "SELECT COALESCE(MAX(version), 0) + 1 FROM payment.payment_channel_versions WHERE seller_id = @sellerId";
                versionCmd.Parameters.AddWithValue("sellerId", sellerId);
                var rawVersion = await versionCmd.ExecuteScalarAsync(ct);
                var nextVersion = Convert.ToInt32(rawVersion);

                var now = DateTime.UtcNow;
                await using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = """
                    INSERT INTO payment.payment_channel_versions (
                        id, seller_id, version, secret_ref, expected_bank_bin, expected_bank_account, expected_bank_name, state, created_at, updated_at,
                        allow_virtual_accounts, bound_credential_fingerprint
                    ) VALUES (
                        public.uuid_v7(), @sellerId, @version, @secretRef, @bin, @account, @name, 'PENDING_VERIFICATION', @now, @now,
                        @virtualAccounts, @fingerprint
                    )
                    RETURNING id, seller_id, version, state, expected_bank_bin, expected_bank_account, expected_bank_name, verified_at, created_at, updated_at
                    """;
                insertCmd.Parameters.AddWithValue("sellerId", sellerId);
                insertCmd.Parameters.AddWithValue("version", nextVersion);
                insertCmd.Parameters.AddWithValue("secretRef", body.SecretRef);
                insertCmd.Parameters.AddWithValue("bin", opt.ExpectedBin);
                insertCmd.Parameters.AddWithValue("account", opt.ExpectedAccount);
                insertCmd.Parameters.Add("name", NpgsqlDbType.Text).Value = (object?)opt.ExpectedName ?? DBNull.Value;
                insertCmd.Parameters.AddWithValue("now", now);
                insertCmd.Parameters.AddWithValue("virtualAccounts", opt.AllowVirtualAccounts);
                insertCmd.Parameters.AddWithValue("fingerprint", PayOsReceiverEvidence.Fingerprint(opt));

                Guid versionId;
                SellerPaymentChannelResponse response;
                await using (var reader = await insertCmd.ExecuteReaderAsync(ct))
                {
                    await reader.ReadAsync(ct);
                    versionId = reader.GetGuid(0);
                    response = new SellerPaymentChannelResponse(
                        reader.GetGuid(1),
                        reader.GetInt32(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        MaskAccountNumber(reader.GetString(5)),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                        reader.GetDateTime(8),
                        reader.GetDateTime(9)
                    );
                }

                await using var auditCmd = connection.CreateCommand();
                auditCmd.Transaction = tx;
                auditCmd.CommandText = "SELECT flow.audit(@actor, 'SELLER_PAYMENT_CHANNEL_BOUND', 'payment_channel_versions', @id, jsonb_build_object('seller_id', @sellerId, 'version', @version))";
                auditCmd.Parameters.AddWithValue("actor", actorId);
                auditCmd.Parameters.AddWithValue("id", versionId);
                auditCmd.Parameters.AddWithValue("sellerId", sellerId);
                auditCmd.Parameters.AddWithValue("version", nextVersion);
                await auditCmd.ExecuteNonQueryAsync(ct);

                await tx.CommitAsync(ct);
                return Results.Ok(response);
            }).RequireAuthorization("Admin").WithTags("Payment").Produces<SellerPaymentChannelResponse>();

        // 3. POST verify channel via confirm-webhook
        app.MapPost("/api/sellers/{sellerId:guid}/payment-channel/verify",
            async (Guid sellerId, HttpContext http, NpgsqlDataSource source, IPayOsClientResolver resolver, IConfiguration config, ILogger<PayOsClient> logger, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                await using var connection = await source.OpenConnectionAsync(ct);
                await using var tx = await connection.BeginTransactionAsync(ct);

                var (actorOk, actorError, actorId) = await CheckActiveActorAsync(
                    connection, tx, http, ["ADMIN", "SELLER"], sellerId, ct);
                if (!actorOk)
                {
                    await tx.RollbackAsync(ct);
                    return actorError!;
                }

                await using var readCmd = connection.CreateCommand();
                readCmd.Transaction = tx;
                readCmd.CommandText = """
                    SELECT id, version, secret_ref, state, expected_bank_bin, expected_bank_account, expected_bank_name, bound_credential_fingerprint
                    FROM payment.payment_channel_versions
                    WHERE seller_id = @sellerId
                    ORDER BY version DESC
                    LIMIT 1
                    FOR UPDATE
                    """;
                readCmd.Parameters.AddWithValue("sellerId", sellerId);

                Guid versionId;
                int version;
                string secretRef;
                string currentState;
                string expectedBin;
                string expectedAccount;
                string? expectedName;
                string? boundFingerprint;
                await using (var reader = await readCmd.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct))
                    {
                        await tx.RollbackAsync(ct);
                        return Results.NotFound(new { message = "Cửa hàng chưa có kênh thanh toán nào được cấu hình." });
                    }
                    versionId = reader.GetGuid(0);
                    version = reader.GetInt32(1);
                    secretRef = reader.GetString(2);
                    currentState = reader.GetString(3);
                    expectedBin = reader.GetString(4);
                    expectedAccount = reader.GetString(5);
                    expectedName = reader.IsDBNull(6) ? null : reader.GetString(6);
                    boundFingerprint = reader.IsDBNull(7) ? null : reader.GetString(7);
                }

                if (currentState == "PAUSED")
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { message = "Kênh thanh toán đang tạm dừng, không thể thực hiện xác thực." });
                }

                if (!resolver.TryResolveChannelConfig(secretRef, out var channelConfig) || channelConfig is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: 503, title: "Cấu hình kênh không khả dụng trên máy chủ.");
                }

                var publicBase = config["PAYOS_PUBLIC_BASE_URL"] ?? config["MEMBER_WEB_URL"] ?? "https://api.florabot.test";
                if (!channelConfig.MatchesReceiver(sellerId, expectedBin, expectedAccount) || boundFingerprint is not null &&
                    !PayOsReceiverEvidence.MatchesFingerprint(channelConfig.Options, boundFingerprint))
                    return Results.Problem(statusCode: 503, title: "Cấu hình kênh đã thay đổi. Cần gắn lại phiên bản mới.");
                var credentialFingerprint = PayOsReceiverEvidence.Fingerprint(channelConfig.Options);
                var attemptToken = Guid.NewGuid().ToString("N");
                var webhookUrl = $"{publicBase.TrimEnd('/')}/api/payments/channels/{versionId}/webhook?attempt={attemptToken}";

                // Expire any prior pending attempts for this channel version
                await using var expireOld = connection.CreateCommand();
                expireOld.Transaction = tx;
                expireOld.CommandText = "UPDATE payment.channel_verification_attempts SET status = 'EXPIRED', updated_at = @now WHERE channel_version_id = @versionId AND status = 'PENDING'";
                expireOld.Parameters.AddWithValue("now", DateTime.UtcNow);
                expireOld.Parameters.AddWithValue("versionId", versionId);
                await expireOld.ExecuteNonQueryAsync(ct);

                // Insert exact single pending attempt with 10-minute expiry
                await using var insertAttempt = connection.CreateCommand();
                insertAttempt.Transaction = tx;
                insertAttempt.CommandText = """
                    INSERT INTO payment.channel_verification_attempts (
                        id, channel_version_id, seller_id, attempt_token, status, expires_at, created_at, updated_at, credential_fingerprint
                    ) VALUES (
                        public.uuid_v7(), @versionId, @sellerId, @token, 'PENDING', @expires, @now, @now, @fingerprint
                    ) RETURNING id;
                    """;
                insertAttempt.Parameters.AddWithValue("versionId", versionId);
                insertAttempt.Parameters.AddWithValue("sellerId", sellerId);
                insertAttempt.Parameters.AddWithValue("token", attemptToken);
                insertAttempt.Parameters.AddWithValue("expires", DateTime.UtcNow.AddMinutes(10));
                insertAttempt.Parameters.AddWithValue("now", DateTime.UtcNow);
                insertAttempt.Parameters.AddWithValue("fingerprint", credentialFingerprint);
                var attemptId = (Guid)(await insertAttempt.ExecuteScalarAsync(ct))!;

                await tx.CommitAsync(ct);

                // Call payOS confirm-webhook
                var client = resolver.ResolveClient(channelConfig.Options);
                PayOsConfirmWebhookResponse confirmReply;
                try
                {
                    confirmReply = await client.ConfirmWebhookAsync(webhookUrl, ct);
                }
                catch (PaymentGatewayException ex)
                {
                    logger.LogWarning("payOS confirm-webhook connection failed for seller {SellerId}: {Code}", sellerId, ex.FailureCode);
                    await using var failCmd = connection.CreateCommand();
                    failCmd.CommandText = "UPDATE payment.channel_verification_attempts SET status = 'FAILED', updated_at = @now WHERE id = @id";
                    failCmd.Parameters.AddWithValue("now", DateTime.UtcNow);
                    failCmd.Parameters.AddWithValue("id", attemptId);
                    await failCmd.ExecuteNonQueryAsync(ct);

                    return Results.Problem(statusCode: 503, title: "Không thể kết nối tới cổng thanh toán để đăng ký webhook.", detail: ex.FailureCode);
                }

                // Verify returned account matches expected recipient without logging raw bank account numbers
                if (!string.Equals(confirmReply.AccountNumber.Trim(), expectedAccount.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("payOS confirm-webhook returned account mismatch for seller {SellerId}", sellerId);

                    await using var failCmd = connection.CreateCommand();
                    failCmd.CommandText = "UPDATE payment.channel_verification_attempts SET status = 'FAILED', updated_at = @now WHERE id = @id";
                    failCmd.Parameters.AddWithValue("now", DateTime.UtcNow);
                    failCmd.Parameters.AddWithValue("id", attemptId);
                    await failCmd.ExecuteNonQueryAsync(ct);

                    return Results.Problem(statusCode: 422, title: "Số tài khoản nhận tiền từ cổng thanh toán không khớp với cấu hình.", detail: "account_mismatch");
                }

                // Update attempt with confirm reply info
                await using (var updateAttempt = connection.CreateCommand())
                {
                    updateAttempt.CommandText = """
                        UPDATE payment.channel_verification_attempts
                        SET confirm_reply_received = true, confirmed_account = @acc, confirmed_name = @name, updated_at = @now
                        WHERE id = @id
                        """;
                    updateAttempt.Parameters.AddWithValue("acc", confirmReply.AccountNumber);
                    updateAttempt.Parameters.Add("name", NpgsqlDbType.Text).Value = (object?)confirmReply.AccountName ?? DBNull.Value;
                    updateAttempt.Parameters.AddWithValue("now", DateTime.UtcNow);
                    updateAttempt.Parameters.AddWithValue("id", attemptId);
                    await updateAttempt.ExecuteNonQueryAsync(ct);
                }

                // Check if signed sample was already recorded
                bool sampleReceived;
                await using (var checkSample = connection.CreateCommand())
                {
                    checkSample.CommandText = "SELECT sample_received FROM payment.channel_verification_attempts WHERE id = @id";
                    checkSample.Parameters.AddWithValue("id", attemptId);
                    sampleReceived = (bool)(await checkSample.ExecuteScalarAsync(ct))!;
                }

                if (sampleReceived)
                {
                    var now = DateTime.UtcNow;
                    await using var actTx = await connection.BeginTransactionAsync(ct);
                    await using var actCmd = connection.CreateCommand();
                    actCmd.Transaction = actTx;
                    actCmd.CommandText = """
                        UPDATE payment.payment_channel_versions
                        SET state = 'ACTIVE', verified_at = @now, updated_at = @now,
                            verified_credential_fingerprint = CASE WHEN bound_credential_fingerprint IS NULL THEN NULL ELSE @fingerprint END
                        WHERE id = @versionId AND state <> 'PAUSED';
                        UPDATE payment.channel_verification_attempts
                        SET status = 'SUCCEEDED', updated_at = @now
                        WHERE id = @attemptId;
                        """;
                    actCmd.Parameters.AddWithValue("now", now);
                    actCmd.Parameters.AddWithValue("versionId", versionId);
                    actCmd.Parameters.AddWithValue("attemptId", attemptId);
                    actCmd.Parameters.AddWithValue("fingerprint", credentialFingerprint);
                    await actCmd.ExecuteNonQueryAsync(ct);

                    await using var auditCmd = connection.CreateCommand();
                    auditCmd.Transaction = actTx;
                    auditCmd.CommandText = "SELECT flow.audit(@actor, 'SELLER_PAYMENT_CHANNEL_VERIFIED', 'payment_channel_versions', @id, jsonb_build_object('seller_id', @sellerId, 'version', @version))";
                    auditCmd.Parameters.AddWithValue("actor", actorId);
                    auditCmd.Parameters.AddWithValue("id", versionId);
                    auditCmd.Parameters.AddWithValue("sellerId", sellerId);
                    auditCmd.Parameters.AddWithValue("version", version);
                    await auditCmd.ExecuteNonQueryAsync(ct);

                    await actTx.CommitAsync(ct);
                    return Results.Ok(new ChannelVerificationResponse(true, "ACTIVE", "Kênh thanh toán đã được xác thực thành công."));
                }

                return Results.Ok(new ChannelVerificationResponse(false, "PENDING_VERIFICATION", "Đã xác nhận tài khoản nhận tiền. Đang chờ chữ ký probe từ cổng thanh toán."));
            }).RequireAuthorization("Merchant", "SameSeller").WithTags("Payment").Produces<ChannelVerificationResponse>();

        // 4. PATCH update status (PAUSE blocks new use; ACTIVE requires verified_at)
        app.MapPatch("/api/sellers/{sellerId:guid}/payment-channel/status",
            async (Guid sellerId, UpdateChannelStatusRequest body, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                var status = body.Status?.Trim().ToUpperInvariant();
                if (status is not ("ACTIVE" or "PAUSED"))
                {
                    return Results.BadRequest(new { message = "Trạng thái chỉ có thể là ACTIVE hoặc PAUSED." });
                }

                await using var connection = await source.OpenConnectionAsync(ct);
                await using var tx = await connection.BeginTransactionAsync(ct);

                var (actorOk, actorError, actorId) = await CheckActiveActorAsync(
                    connection, tx, http, ["ADMIN", "SELLER"], sellerId, ct);
                if (!actorOk)
                {
                    await tx.RollbackAsync(ct);
                    return actorError!;
                }

                await using var readCmd = connection.CreateCommand();
                readCmd.Transaction = tx;
                readCmd.CommandText = """
                    SELECT id, version, state, verified_at, expected_bank_bin, expected_bank_account, expected_bank_name, created_at, updated_at
                    FROM payment.payment_channel_versions
                    WHERE seller_id = @sellerId
                    ORDER BY version DESC
                    LIMIT 1
                    FOR UPDATE
                    """;
                readCmd.Parameters.AddWithValue("sellerId", sellerId);

                Guid versionId;
                int version;
                DateTime? verifiedAt;
                string bin;
                string account;
                string? name;
                DateTime createdAt;
                await using (var reader = await readCmd.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct))
                    {
                        await tx.RollbackAsync(ct);
                        return Results.NotFound(new { message = "Cửa hàng chưa có kênh thanh toán nào được cấu hình." });
                    }
                    versionId = reader.GetGuid(0);
                    version = reader.GetInt32(1);
                    verifiedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
                    bin = reader.GetString(4);
                    account = reader.GetString(5);
                    name = reader.IsDBNull(6) ? null : reader.GetString(6);
                    createdAt = reader.GetDateTime(7);
                }

                if (status == "ACTIVE" && verifiedAt is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { message = "Kênh thanh toán chưa được xác thực thành công (verified_at là bắt buộc). Không thể chuyển sang trạng thái ACTIVE." });
                }

                var now = DateTime.UtcNow;
                await using var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = tx;
                updateCmd.CommandText = """
                    UPDATE payment.payment_channel_versions
                    SET state = @state, updated_at = @now
                    WHERE id = @id
                    """;
                updateCmd.Parameters.AddWithValue("state", status);
                updateCmd.Parameters.AddWithValue("now", now);
                updateCmd.Parameters.AddWithValue("id", versionId);
                await updateCmd.ExecuteNonQueryAsync(ct);

                await using var auditCmd = connection.CreateCommand();
                auditCmd.Transaction = tx;
                auditCmd.CommandText = "SELECT flow.audit(@actor, 'SELLER_PAYMENT_CHANNEL_STATUS_CHANGED', 'payment_channel_versions', @id, jsonb_build_object('seller_id', @sellerId, 'state', @state))";
                auditCmd.Parameters.AddWithValue("actor", actorId);
                auditCmd.Parameters.AddWithValue("id", versionId);
                auditCmd.Parameters.AddWithValue("sellerId", sellerId);
                auditCmd.Parameters.AddWithValue("state", status);
                await auditCmd.ExecuteNonQueryAsync(ct);

                await tx.CommitAsync(ct);

                var response = new SellerPaymentChannelResponse(
                    sellerId,
                    version,
                    status,
                    bin,
                    MaskAccountNumber(account),
                    name,
                    verifiedAt,
                    createdAt,
                    now
                );

                return Results.Ok(response);
            }).RequireAuthorization("Merchant", "SameSeller").WithTags("Payment").Produces<SellerPaymentChannelResponse>();

        // 5. Admin GET all channels (Active admin check)
        app.MapGet("/api/admin/payment-channels",
            async (HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                await using var connection = await source.OpenConnectionAsync(ct);

                var (actorOk, actorError, _) = await CheckActiveActorAsync(
                    connection, null, http, ["ADMIN"], null, ct);
                if (!actorOk) return actorError!;

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT seller_id, shop_name, version, state, expected_bank_bin,
                           expected_bank_account, expected_bank_name, verified_at, updated_at
                    FROM payment.v2_cp_list_admin_channels()
                    """;

                var results = new List<AdminPaymentChannelItemResponse>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    results.Add(new AdminPaymentChannelItemResponse(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetInt32(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        MaskAccountNumber(reader.GetString(5)),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                        reader.GetDateTime(8)
                    ));
                }

                return Results.Ok(results);
            }).RequireAuthorization("Admin").WithTags("Payment").Produces<List<AdminPaymentChannelItemResponse>>();
    }
}
