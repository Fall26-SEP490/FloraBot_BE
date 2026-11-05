using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.Ordering;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace FloraBot.Api.Modules.Payment;

public sealed record SellerRefundSummary(
    Guid Id,
    Guid OrderId,
    string OrderCode,
    long Amount,
    string Status,
    string? SubmissionStatus,
    string? Reason,
    DateTime CreatedAt,
    bool IsApproved,
    DateTime? TransferredAt);

public sealed record SellerRefundPageResponse(
    List<SellerRefundSummary> Items,
    int Page,
    int PageSize,
    bool HasMore);

public sealed record SellerRefundSubmissionDetail(
    Guid Id,
    string ProviderReference,
    DateTime TransferredAt,
    string Status,
    string? Note,
    DateTime CreatedAt,
    Guid? ReviewedBy,
    DateTime? ReviewedAt);

public sealed record SellerRefundDetailResponse(
    Guid Id,
    Guid OrderId,
    string OrderCode,
    long Amount,
    string Status,
    string? Reason,
    DateTime CreatedAt,
    bool IsApproved,
    bool CanAccessBank,
    bool CanSubmitTransfer,
    SellerRefundSubmissionDetail? Submission);

public sealed record SellerBankAccessRequest(string Reason);

public sealed record SellerSubmitTransferRequest(
    long Amount,
    string Currency,
    string BankName,
    string BankHolder,
    string AccountNumber,
    string ProviderReference,
    DateTimeOffset TransferredAt,
    string EvidenceTicket,
    string? Note = null);

public sealed record SellerRefundTransferSubmissionResponse(
    Guid Id,
    Guid RefundId,
    string ProviderReference,
    string Status,
    DateTime TransferredAt,
    DateTime CreatedAt);

public static class SellerRefunds
{
    private const string TicketPurpose = "seller_direct_refund";
    private static string TicketBinding(string actor, Guid seller, Guid refund) => $"seller_refund:{actor}:{seller}:{refund}";

    public static void MapSellerRefunds(this IEndpointRouteBuilder app)
    {
        // 1. GET /api/sellers/{sellerId}/refunds (page/filter)
        app.MapGet("/api/sellers/{sellerId:guid}/refunds", async (
            Guid sellerId,
            string? status,
            int? page,
            int? pageSize,
            NpgsqlDataSource data,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId))
                return Results.Unauthorized();

            var pageNumber = page ?? 1;
            var size = pageSize ?? 20;
            if (pageNumber < 1 || pageNumber > 100000 || size < 1 || size > 100)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Tham số phân trang không hợp lệ.");

            await using var conn = await data.OpenConnectionAsync(ct);
            if (!await VerifyActiveSellerOwnerAsync(conn, actorId, sellerId, ct))
                return Results.Forbid();

            var offset = (pageNumber - 1) * size;
            var fetchCount = size + 1;

            var items = new List<SellerRefundSummary>();
            await using (var cmd = new NpgsqlCommand("""
                SELECT id,
                       order_id,
                       order_code,
                       amount,
                       status,
                       submission_status,
                       reason,
                       created_at,
                       is_approved,
                       transferred_at
                FROM payment.v2_rf_list_seller_refunds(@sellerId, @statusFilter, @offset, @fetchCount)
                """, conn))
            {
                cmd.Parameters.AddWithValue("sellerId", sellerId);
                cmd.Parameters.Add(new NpgsqlParameter("statusFilter", NpgsqlTypes.NpgsqlDbType.Text)
                {
                    Value = string.IsNullOrWhiteSpace(status) ? DBNull.Value : status.Trim().ToUpperInvariant()
                });
                cmd.Parameters.AddWithValue("offset", offset);
                cmd.Parameters.AddWithValue("fetchCount", fetchCount);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new SellerRefundSummary(
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.GetString(2),
                        reader.GetInt64(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.GetDateTime(7),
                        reader.GetBoolean(8),
                        reader.IsDBNull(9) ? null : reader.GetDateTime(9)));
                }
            }

            var hasMore = items.Count > size;
            if (hasMore) items.RemoveAt(items.Count - 1);

            return Results.Ok(new SellerRefundPageResponse(items, pageNumber, size, hasMore));
        })
        .RequireAuthorization("Seller", "SameSeller")
        .WithTags("Payment")
        .WithSummary("Danh sách các khoản hoàn tiền trực tiếp của shop")
        .Produces<SellerRefundPageResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        // 2. GET /api/sellers/{sellerId}/refunds/{refundId}
        app.MapGet("/api/sellers/{sellerId:guid}/refunds/{refundId:guid}", async (
            Guid sellerId,
            Guid refundId,
            NpgsqlDataSource data,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId))
                return Results.Unauthorized();

            await using var conn = await data.OpenConnectionAsync(ct);
            if (!await VerifyActiveSellerOwnerAsync(conn, actorId, sellerId, ct))
                return Results.Forbid();

            SellerRefundDetailResponse? detail = null;
            await using (var cmd = new NpgsqlCommand("""
                SELECT id,
                       order_id,
                       order_code,
                       amount,
                       status,
                       reason,
                       created_at,
                       is_approved,
                       has_bank,
                       sub_id,
                       provider_reference,
                       transferred_at,
                       sub_status,
                       sub_note,
                       sub_created,
                       reviewed_by,
                       reviewed_at
                FROM payment.v2_rf_get_seller_refund_detail(@sellerId, @refundId)
                """, conn))
            {
                cmd.Parameters.AddWithValue("refundId", refundId);
                cmd.Parameters.AddWithValue("sellerId", sellerId);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var isApproved = reader.GetBoolean(7);
                    var hasBank = reader.GetBoolean(8);
                    var pStatus = reader.GetString(4);
                    var hasSub = !reader.IsDBNull(9);

                    SellerRefundSubmissionDetail? sub = null;
                    if (hasSub)
                    {
                        sub = new SellerRefundSubmissionDetail(
                            reader.GetGuid(9),
                            reader.GetString(10),
                            reader.GetDateTime(11),
                            reader.GetString(12),
                            reader.IsDBNull(13) ? null : reader.GetString(13),
                            reader.GetDateTime(14),
                            reader.IsDBNull(15) ? null : reader.GetGuid(15),
                            reader.IsDBNull(16) ? null : reader.GetDateTime(16));
                    }

                    var canAccessBank = isApproved && pStatus == "PENDING" && hasBank;
                    // Only permit submission when no previous submission exists (resubmission not supported)
                    var canSubmit = isApproved && pStatus == "PENDING" && hasBank && sub is null;

                    detail = new SellerRefundDetailResponse(
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.GetString(2),
                        reader.GetInt64(3),
                        pStatus,
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetDateTime(6),
                        isApproved,
                        canAccessBank,
                        canSubmit,
                        sub);
                }
            }

            if (detail is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Khoản hoàn tiền không tồn tại hoặc không thuộc shop này.");

            return Results.Ok(detail);
        })
        .RequireAuthorization("Seller", "SameSeller")
        .WithTags("Payment")
        .WithSummary("Chi tiết khoản hoàn tiền trực tiếp của shop")
        .Produces<SellerRefundDetailResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // 3. POST /api/sellers/{sellerId}/refunds/{refundId}/bank-details
        app.MapPost("/api/sellers/{sellerId:guid}/refunds/{refundId:guid}/bank-details", async (
            Guid sellerId,
            Guid refundId,
            SellerBankAccessRequest request,
            NpgsqlDataSource data,
            IDataProtectionProvider protection,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request?.Reason) || request.Reason.Trim().Length < 5 || request.Reason.Length > 500 || request.Reason.Any(char.IsControl))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Cần nêu rõ lý do xem thông tin tài khoản hoàn tiền (5–500 ký tự).");

            await using var conn = await data.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            if (!await VerifyActiveSellerOwnerAsync(conn, actorId, sellerId, ct, tx))
            {
                await tx.RollbackAsync(ct);
                return Results.Forbid();
            }

            // Lock payment and verify it is an approved direct refund assigned to this seller
            Guid orderId;
            string? refundBankName = null;
            string? refundBankHolder = null;
            string? refundBankAccountEnc = null;

            await using (var cmd = new NpgsqlCommand("""
                SELECT id,
                       order_id,
                       status,
                       is_approved,
                       refund_bank_name,
                       refund_bank_holder,
                       refund_bank_account_enc
                FROM payment.v2_rf_lock_refund_for_bank_details(@sellerId, @refundId)
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("refundId", refundId);
                cmd.Parameters.AddWithValue("sellerId", sellerId);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    await reader.CloseAsync();
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Khoản hoàn tiền không tồn tại hoặc không thuộc shop này.");
                }

                var status = reader.GetString(2);
                var isApproved = reader.GetBoolean(3);
                orderId = reader.GetGuid(1);

                if (status != "PENDING")
                {
                    await reader.CloseAsync();
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Khoản hoàn tiền không ở trạng thái chờ chi.");
                }

                if (!isApproved)
                {
                    await reader.CloseAsync();
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Khoản hoàn tiền cần được quản trị viên duyệt trước khi xem thông tin tài khoản.");
                }

                if (reader.IsDBNull(4) || reader.IsDBNull(5) || reader.IsDBNull(6))
                {
                    await reader.CloseAsync();
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Khách hàng chưa cung cấp thông tin tài khoản nhận hoàn tiền.");
                }

                refundBankName = reader.GetString(4);
                refundBankHolder = reader.GetString(5);
                refundBankAccountEnc = reader.GetString(6);
            }

            // Atomic audit log before revealing full bank details
            await using (var auditCmd = new NpgsqlCommand("""
                SELECT flow.audit(@actor, 'SELLER_REFUND_BANK_VIEWED', 'payments', @refundId,
                    jsonb_build_object('reason', @reason, 'order_id', @orderId, 'seller_id', @sellerId))
                """, conn, tx))
            {
                auditCmd.Parameters.AddWithValue("actor", actorId);
                auditCmd.Parameters.AddWithValue("refundId", refundId);
                auditCmd.Parameters.AddWithValue("reason", request.Reason.Trim());
                auditCmd.Parameters.AddWithValue("orderId", orderId);
                auditCmd.Parameters.AddWithValue("sellerId", sellerId);
                await auditCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            // Decrypt customer bank account
            try
            {
                var account = protection.CreateProtector("bank-account-v1").Unprotect(refundBankAccountEnc);
                return Results.Ok(new RefundBankResponse(refundBankName, refundBankHolder, account));
            }
            catch (CryptographicException)
            {
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, detail: "Không thể giải mã thông tin tài khoản ngân hàng.");
            }
        })
        .RequireAuthorization("Seller", "SameSeller")
        .WithTags("Payment")
        .WithSummary("Xem thông tin tài khoản ngân hàng của khách để chuyển tiền hoàn (bắt buộc nêu lý do)")
        .Produces<RefundBankResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // 4. POST /api/sellers/{sellerId}/refunds/{refundId}/evidence/upload
        PrivateEvidence.DescribeUpload(app.MapPost("/api/sellers/{sellerId:guid}/refunds/{refundId:guid}/evidence/upload", async (
            Guid sellerId,
            Guid refundId,
            HttpContext http,
            NpgsqlDataSource data,
            CloudinaryMedia media,
            PrivateEvidence tickets,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId))
                return Results.Unauthorized();

            await using var conn = await data.OpenConnectionAsync(ct);
            if (!await VerifyActiveSellerOwnerAsync(conn, actorId, sellerId, ct))
                return Results.Forbid();

            // Verify refund exists, is approved direct refund in pending status
            await using (var checkCmd = new NpgsqlCommand("""
                SELECT status, is_approved
                FROM payment.v2_rf_check_evidence_upload_eligibility(@sellerId, @refundId)
                """, conn))
            {
                checkCmd.Parameters.AddWithValue("refundId", refundId);
                checkCmd.Parameters.AddWithValue("sellerId", sellerId);

                await using var reader = await checkCmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return Results.NotFound();

                var status = reader.GetString(0);
                var isApproved = reader.GetBoolean(1);
                if (status != "PENDING" || !isApproved)
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Chỉ được tải ảnh chứng từ cho khoản hoàn đã được duyệt và đang chờ chi.");
            }

            var binding = TicketBinding(actorId.ToString(), sellerId, refundId);
            return await PrivateEvidence.UploadAsync(http, media, tickets, TicketPurpose, binding, ct);
        })
        .RequireAuthorization("Seller", "SameSeller")
        .WithMetadata(new RequestSizeLimitAttribute(CloudinaryMedia.MaximumBytes)));

        // 5. POST /api/sellers/{sellerId}/refunds/{refundId}/submit-transfer
        app.MapPost("/api/sellers/{sellerId:guid}/refunds/{refundId:guid}/submit-transfer", async (
            Guid sellerId,
            Guid refundId,
            SellerSubmitTransferRequest request,
            NpgsqlDataSource data,
            PrivateEvidence tickets,
            IDataProtectionProvider protection,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId))
                return Results.Unauthorized();

            // Validation
            if (request.Amount <= 0)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Số tiền hoàn phải lớn hơn 0.");

            if (!string.Equals(request.Currency?.Trim(), "VND", StringComparison.OrdinalIgnoreCase))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Đơn vị tiền tệ chỉ chấp nhận VND.");

            if (string.IsNullOrWhiteSpace(request.ProviderReference) || request.ProviderReference.Trim().Length < 3 || request.ProviderReference.Length > 100 || request.ProviderReference.Any(char.IsControl))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Mã giao dịch ngân hàng / tham chiếu không hợp lệ (3–100 ký tự).");

            if (string.IsNullOrWhiteSpace(request.EvidenceTicket))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Chứng từ chuyển khoản là bắt buộc.");

            var now = DateTimeOffset.UtcNow;
            if (request.TransferredAt > now.AddMinutes(5) || request.TransferredAt < now.AddDays(-30))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Thời gian chuyển khoản không hợp lệ.");

            // Resolve and verify evidence ticket with exact actor, seller, refund binding
            VerifiedMedia verifiedMedia;
            try
            {
                var binding = TicketBinding(actorId.ToString(), sellerId, refundId);
                verifiedMedia = tickets.Resolve(request.EvidenceTicket, TicketPurpose, binding);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: ex.Message);
            }

            await using var conn = await data.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            if (!await VerifyActiveSellerOwnerAsync(conn, actorId, sellerId, ct, tx))
            {
                await tx.RollbackAsync(ct);
                return Results.Forbid();
            }

            // Lock payment row and verify conditions
            Guid orderId;
            long expectedAmount;
            string? refundBankName;
            string? refundBankHolder;
            string? refundBankAccountEnc;

            await using (var cmd = new NpgsqlCommand("""
                SELECT id,
                       order_id,
                       amount,
                       status,
                       is_approved,
                       refund_bank_name,
                       refund_bank_holder,
                       refund_bank_account_enc
                FROM payment.v2_rf_lock_refund_for_submit_transfer(@sellerId, @refundId)
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("refundId", refundId);
                cmd.Parameters.AddWithValue("sellerId", sellerId);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    await reader.CloseAsync();
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Khoản hoàn tiền không tồn tại hoặc không thuộc shop này.");
                }

                orderId = reader.GetGuid(1);
                expectedAmount = reader.GetInt64(2);
                var pStatus = reader.GetString(3);
                var isApproved = reader.GetBoolean(4);

                if (pStatus != "PENDING")
                {
                    await reader.CloseAsync();
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Khoản hoàn tiền không ở trạng thái chờ chi.");
                }

                if (!isApproved)
                {
                    await reader.CloseAsync();
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Khoản hoàn tiền cần được quản trị viên duyệt trước khi gửi xác nhận chuyển khoản.");
                }

                if (reader.IsDBNull(5) || reader.IsDBNull(6) || reader.IsDBNull(7))
                {
                    await reader.CloseAsync();
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Chưa có thông tin tài khoản nhận hoàn tiền của khách hàng.");
                }

                refundBankName = reader.GetString(5);
                refundBankHolder = reader.GetString(6);
                refundBankAccountEnc = reader.GetString(7);
            }

            // Verify exact amount
            if (request.Amount != expectedAmount)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: $"Số tiền chuyển khoản ({request.Amount:N0} đ) không khớp với số tiền cần hoàn ({expectedAmount:N0} đ).");
            }

            // Verify receiver snapshot consistency against order's customer bank
            string decryptedCustomerAccount;
            try
            {
                decryptedCustomerAccount = protection.CreateProtector("bank-account-v1").Unprotect(refundBankAccountEnc);
            }
            catch (CryptographicException)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, detail: "Lỗi bảo mật khi kiểm tra tài khoản nhận hoàn.");
            }

            if (!string.Equals(request.BankName?.Trim(), refundBankName?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.BankHolder?.Trim(), refundBankHolder?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.AccountNumber?.Trim(), decryptedCustomerAccount.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Thông tin người nhận chuyển khoản không khớp với tài khoản yêu cầu hoàn của khách.");
            }

            var providerRef = request.ProviderReference.Trim();

            // Check if submission already exists for this refund (idempotency or conflict)
            await using (var existCmd = new NpgsqlCommand("""
                SELECT id, provider_reference, amount, status, transferred_at, created_at
                FROM payment.seller_refund_transfers
                WHERE refund_id = @refundId
                FOR UPDATE
                """, conn, tx))
            {
                existCmd.Parameters.AddWithValue("refundId", refundId);
                await using var reader = await existCmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var existingId = reader.GetGuid(0);
                    var existingRef = reader.GetString(1);
                    var existingAmt = reader.GetInt64(2);
                    var existingStatus = reader.GetString(3);
                    var existingTransferred = reader.GetDateTime(4);
                    var existingCreated = reader.GetDateTime(5);
                    await reader.CloseAsync();

                    if (string.Equals(existingRef, providerRef, StringComparison.OrdinalIgnoreCase) && existingAmt == request.Amount)
                    {
                        await tx.CommitAsync(ct);
                        return Results.Ok(new SellerRefundTransferSubmissionResponse(
                            existingId, refundId, existingRef, existingStatus, existingTransferred, existingCreated));
                    }

                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Khoản hoàn này đã có chuyển khoản được gửi với mã tham chiếu khác.");
                }
            }

            // Check provider reference uniqueness across all refunds
            await using (var uniqueRefCmd = new NpgsqlCommand("""
                SELECT 1 FROM payment.seller_refund_transfers
                WHERE lower(provider_reference) = lower(@ref)
                """, conn, tx))
            {
                uniqueRefCmd.Parameters.AddWithValue("ref", providerRef);
                var refUsed = await uniqueRefCmd.ExecuteScalarAsync(ct);
                if (refUsed is not null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Mã giao dịch ngân hàng này đã được sử dụng cho khoản hoàn khác.");
                }
            }

            // Lock and verify evidence URL is unused
            if (!await PrivateEvidence.LockUnusedAsync(conn, tx, verifiedMedia.Url, ct))
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Ảnh chứng từ này đã được dùng cho giao dịch khác.");
            }

            // Record attachment via flow.attach and set metadata
            await using (var attachCmd = new NpgsqlCommand("""
                SELECT flow.attach('payment', 'refund', @refundId, @url, 'PROOF', @actor)
                """, conn, tx))
            {
                attachCmd.Parameters.AddWithValue("refundId", refundId);
                attachCmd.Parameters.AddWithValue("url", verifiedMedia.Url);
                attachCmd.Parameters.AddWithValue("actor", actorId);
                await attachCmd.ExecuteNonQueryAsync(ct);
            }

            await PrivateEvidence.SetFlowMetadataAsync(conn, tx, verifiedMedia, ct);

            Guid? attachmentId = null;
            await using (var getAttachCmd = new NpgsqlCommand("""
                SELECT id FROM payment.v2_rf_get_attachment_id(@url)
                """, conn, tx))
            {
                getAttachCmd.Parameters.AddWithValue("url", verifiedMedia.Url);
                attachmentId = (Guid?)await getAttachCmd.ExecuteScalarAsync(ct);
            }

            // Insert seller refund transfer with PENDING_REVIEW
            var submissionId = Guid.CreateVersion7();
            var transferredAtUtc = request.TransferredAt.UtcDateTime;
            var createdAtUtc = DateTime.UtcNow;

            await using (var insertCmd = new NpgsqlCommand("""
                INSERT INTO payment.seller_refund_transfers (
                    id, refund_id, seller_id, order_id, submitted_by, amount, currency,
                    receiver_bank_name, receiver_bank_holder, receiver_account_no_enc,
                    provider_reference, transferred_at, evidence_attachment_id, evidence_url,
                    status, note, created_at, updated_at
                ) VALUES (
                    @id, @refundId, @sellerId, @orderId, @actor, @amount, 'VND',
                    @bankName, @bankHolder, @accountEnc,
                    @ref, @transferredAt, @attachId, @url,
                    'PENDING_REVIEW', @note, @createdAt, @createdAt
                )
                """, conn, tx))
            {
                insertCmd.Parameters.AddWithValue("id", submissionId);
                insertCmd.Parameters.AddWithValue("refundId", refundId);
                insertCmd.Parameters.AddWithValue("sellerId", sellerId);
                insertCmd.Parameters.AddWithValue("orderId", orderId);
                insertCmd.Parameters.AddWithValue("actor", actorId);
                insertCmd.Parameters.AddWithValue("amount", request.Amount);
                insertCmd.Parameters.AddWithValue("bankName", refundBankName!);
                insertCmd.Parameters.AddWithValue("bankHolder", refundBankHolder!);
                insertCmd.Parameters.AddWithValue("accountEnc", refundBankAccountEnc!);
                insertCmd.Parameters.AddWithValue("ref", providerRef);
                insertCmd.Parameters.AddWithValue("transferredAt", transferredAtUtc);
                insertCmd.Parameters.AddWithValue("attachId", (object?)attachmentId ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("url", verifiedMedia.Url);
                insertCmd.Parameters.Add(new NpgsqlParameter("note", NpgsqlTypes.NpgsqlDbType.Text)
                {
                    Value = string.IsNullOrWhiteSpace(request.Note) ? DBNull.Value : request.Note.Trim()
                });
                insertCmd.Parameters.AddWithValue("createdAt", createdAtUtc);

                await insertCmd.ExecuteNonQueryAsync(ct);
            }

            // Atomic audit
            await using (var auditCmd = new NpgsqlCommand("""
                SELECT flow.audit(@actor, 'SELLER_REFUND_TRANSFER_SUBMITTED', 'payments', @refundId,
                    jsonb_build_object('reference', @ref, 'amount', @amount, 'seller_id', @sellerId, 'transfer_id', @id))
                """, conn, tx))
            {
                auditCmd.Parameters.AddWithValue("actor", actorId);
                auditCmd.Parameters.AddWithValue("refundId", refundId);
                auditCmd.Parameters.AddWithValue("ref", providerRef);
                auditCmd.Parameters.AddWithValue("amount", request.Amount);
                auditCmd.Parameters.AddWithValue("sellerId", sellerId);
                auditCmd.Parameters.AddWithValue("id", submissionId);
                await auditCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            return Results.Ok(new SellerRefundTransferSubmissionResponse(
                submissionId,
                refundId,
                providerRef,
                "PENDING_REVIEW",
                transferredAtUtc,
                createdAtUtc));
        })
        .RequireAuthorization("Seller", "SameSeller")
        .WithTags("Payment")
        .WithSummary("Gửi thông tin xác nhận chuyển khoản hoàn tiền trực tiếp của người bán (chờ Admin duyệt)")
        .Produces<SellerRefundTransferSubmissionResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // 6. Admin inspection endpoint: GET /api/admin/refunds/{refundId}/seller-submission
        app.MapGet("/api/admin/refunds/{refundId:guid}/seller-submission", async (
            Guid refundId,
            NpgsqlDataSource data,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            await using var conn = await data.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT id,
                       refund_id,
                       seller_id,
                       shop_name,
                       submitted_by,
                       submitter_name,
                       amount,
                       currency,
                       receiver_bank_name,
                       receiver_bank_holder,
                       provider_reference,
                       transferred_at,
                       evidence_attachment_id,
                       evidence_url,
                       status,
                       note,
                       created_at,
                       reviewed_by,
                       reviewed_at
                FROM payment.v2_rf_get_admin_seller_submission(@refundId)
                """, conn);
            cmd.Parameters.AddWithValue("refundId", refundId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return Results.NotFound();

            return Results.Ok(new
            {
                Id = reader.GetGuid(0),
                RefundId = reader.GetGuid(1),
                SellerId = reader.GetGuid(2),
                ShopName = reader.GetString(3),
                SubmittedBy = reader.GetGuid(4),
                SubmitterName = reader.GetString(5),
                Amount = reader.GetInt64(6),
                Currency = reader.GetString(7),
                ReceiverBankName = reader.GetString(8),
                ReceiverBankHolder = reader.GetString(9),
                ProviderReference = reader.GetString(10),
                TransferredAt = reader.GetDateTime(11),
                EvidenceAttachmentId = reader.IsDBNull(12) ? null : (Guid?)reader.GetGuid(12),
                EvidenceUrl = reader.GetString(13),
                Status = reader.GetString(14),
                Note = reader.IsDBNull(15) ? null : reader.GetString(15),
                CreatedAt = reader.GetDateTime(16),
                ReviewedBy = reader.IsDBNull(17) ? null : (Guid?)reader.GetGuid(17),
                ReviewedAt = reader.IsDBNull(18) ? null : (DateTime?)reader.GetDateTime(18)
            });
        })
        .RequireAuthorization("Admin")
        .WithTags("Payment")
        .WithSummary("Admin xem chi tiết chuyển khoản hoàn tiền trực tiếp của người bán gửi duyệt");
    }

    private static async Task<bool> VerifyActiveSellerOwnerAsync(
        NpgsqlConnection conn,
        Guid actorId,
        Guid sellerId,
        CancellationToken ct,
        NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id
            FROM payment.v2_rf_verify_active_seller_owner(@actorId, @sellerId)
            """, conn, tx);
        cmd.Parameters.AddWithValue("actorId", actorId);
        cmd.Parameters.AddWithValue("sellerId", sellerId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
