using System.Security.Claims;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record CreateKioskTerminationRequest(string Reason);

public sealed record ReviewKioskTerminationRequest(string Action, string Reason);

public sealed record CompleteKioskTerminationRequest(string? Notes, string? EvidenceReference);

public sealed record KioskTerminationResponse(
    Guid Id,
    Guid KioskId,
    string? KioskCode,
    string? KioskName,
    Guid SellerId,
    string? ShopName,
    string Status,
    string Reason,
    DateTime RequestedAt,
    Guid RequestedBy,
    string? ReviewAction,
    string? ReviewReason,
    DateTime? ReviewedAt,
    Guid? ReviewedBy,
    Guid? CorrelatedTaskId,
    DateTime? CompletedAt,
    Guid? CompletedBy,
    string? CompletionNotes,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public sealed record KioskTerminationPage(
    List<KioskTerminationResponse> Items,
    int Page,
    int Limit,
    bool HasMore
);

public static class KioskTerminations
{
    public static IEndpointRouteBuilder MapKioskTerminations(this IEndpointRouteBuilder app)
    {
        // 1. Seller: Submit termination request for owned kiosk (records intent only)
        app.MapPost("/api/sellers/{sellerId:guid}/kiosks/{kioskId:guid}/termination-requests", async (
            Guid sellerId,
            Guid kioskId,
            CreateKioskTerminationRequest input,
            HttpContext http,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            if (actor == Guid.Empty) return Results.Unauthorized();

            await using var connection = await data.OpenConnectionAsync(ct);

            // Active actor DB check
            var (validActor, actorSeller, actorRole) = await ValidateActiveUserAsync(connection, null, actor, ct);
            if (!validActor || actorRole != "SELLER" || actorSeller != sellerId)
                return Results.Problem(statusCode: 403, detail: "Chỉ tài khoản người bán (SELLER) đang hoạt động của shop mới được yêu cầu chấm dứt.");

            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length is < 10 or > 2000 || input.Reason.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
                errors["reason"] = ["Lý do chấm dứt từ 10 đến 2000 ký tự, không chứa ký tự điều khiển không hợp lệ."];

            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            await using var tx = await connection.BeginTransactionAsync(ct);

            // Row-level lock in consistent order: Kiosk -> Seller
            await using var verifyCmd = new NpgsqlCommand(
                "SELECT seller_id FROM kiosk_ops.kiosks WHERE id = @kiosk FOR UPDATE",
                connection, tx);
            verifyCmd.Parameters.AddWithValue("kiosk", kioskId);
            var kioskOwner = await verifyCmd.ExecuteScalarAsync(ct);
            if (kioskOwner is null or DBNull)
                return Results.NotFound();

            if ((Guid)kioskOwner != sellerId)
                return Results.Problem(statusCode: 403, detail: "Kiosk không thuộc quyền sở hữu của Seller này.");

            await using var lockSellerCmd = new NpgsqlCommand(
                "SELECT kiosk_ops.v2_term_lock_seller(@seller)",
                connection, tx);
            lockSellerCmd.Parameters.AddWithValue("seller", sellerId);
            await lockSellerCmd.ExecuteNonQueryAsync(ct);

            await using var cmd = new NpgsqlCommand(
                "SELECT flow.request_kiosk_termination(@kiosk, @seller, @actor, @reason)",
                connection, tx);
            cmd.Parameters.AddWithValue("kiosk", kioskId);
            cmd.Parameters.AddWithValue("seller", sellerId);
            cmd.Parameters.AddWithValue("actor", actor);
            cmd.Parameters.AddWithValue("reason", input.Reason.Trim());

            Guid createdId;
            try
            {
                createdId = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            }
            catch (PostgresException ex) when (ex.SqlState == "40001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                return Results.Problem(statusCode: 403, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "P0002")
            {
                return Results.NotFound();
            }
            catch (PostgresException ex) when (ex.SqlState == "22023")
            {
                return Results.Problem(statusCode: 400, detail: ex.MessageText);
            }

            var record = await LoadTerminationAsync(connection, tx, createdId, ct);
            await tx.CommitAsync(ct);

            return record is not null
                ? Results.Created($"/api/sellers/{sellerId}/kiosks/{kioskId}/termination-requests/current", record)
                : Results.StatusCode(201);
        }).RequireAuthorization("Seller", "SameSeller").WithTags("KioskTermination").Produces<KioskTerminationResponse>(201);

        // 2. Seller: Get current termination request for own kiosk (readable even after kiosk unbinds upon completion)
        app.MapGet("/api/sellers/{sellerId:guid}/kiosks/{kioskId:guid}/termination-requests/current", async (
            Guid sellerId,
            Guid kioskId,
            HttpContext http,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            if (actor == Guid.Empty) return Results.Unauthorized();

            await using var connection = await data.OpenConnectionAsync(ct);

            // Active actor DB check
            var (validActor, actorSeller, actorRole) = await ValidateActiveUserAsync(connection, null, actor, ct);
            if (!validActor || actorRole != "SELLER" || actorSeller != sellerId)
                return Results.Problem(statusCode: 403, detail: "Chỉ tài khoản người bán (SELLER) đang hoạt động mới được xem thông tin.");

            // Find current or historical request for this seller on this kiosk
            await using var cmd = new NpgsqlCommand("""
                SELECT t.id FROM kiosk_ops.kiosk_terminations t
                WHERE t.kiosk_id = @kiosk AND t.seller_id = @seller
                ORDER BY
                  CASE WHEN t.status IN ('REQUESTED', 'ACCEPTED') THEN 0 ELSE 1 END,
                  t.created_at DESC
                LIMIT 1
                """, connection);
            cmd.Parameters.AddWithValue("kiosk", kioskId);
            cmd.Parameters.AddWithValue("seller", sellerId);

            var termId = await cmd.ExecuteScalarAsync(ct);
            if (termId is not null and not DBNull)
            {
                var record = await LoadTerminationAsync(connection, null, (Guid)termId, ct);
                return record is not null ? Results.Ok(record) : Results.NotFound();
            }

            // If no termination request exists, verify kiosk exists and belongs to this seller
            await using var verifyCmd = new NpgsqlCommand("SELECT seller_id FROM kiosk_ops.kiosks WHERE id = @kiosk", connection);
            verifyCmd.Parameters.AddWithValue("kiosk", kioskId);
            var owner = await verifyCmd.ExecuteScalarAsync(ct);
            if (owner is null or DBNull)
                return Results.NotFound();

            if ((Guid)owner != sellerId)
                return Results.Problem(statusCode: 403, detail: "Kiosk không thuộc quyền sở hữu của Seller này.");

            return Results.NotFound();
        }).RequireAuthorization("Seller", "SameSeller").WithTags("KioskTermination").Produces<KioskTerminationResponse>();

        // 3. Operations Manager: List termination requests (paginated, with active actor DB check)
        app.MapGet("/api/operations/kiosk-termination-requests", async (
            string? status,
            int? page,
            int? limit,
            HttpContext http,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            if (actor == Guid.Empty) return Results.Unauthorized();

            await using var connection = await data.OpenConnectionAsync(ct);

            // Active actor DB check
            var (validActor, _, actorRole) = await ValidateActiveUserAsync(connection, null, actor, ct);
            if (!validActor || actorRole != "OPERATIONS_MANAGER")
                return Results.Problem(statusCode: 403, detail: "Chỉ Operations Manager đang hoạt động mới được xem danh sách.");

            var p = page.GetValueOrDefault(1);
            if (p < 1) p = 1;
            var l = Math.Clamp(limit.GetValueOrDefault(25), 1, 100);

            await using var cmd = new NpgsqlCommand("""
                SELECT
                  id, kiosk_id, kiosk_code, kiosk_name, seller_id, shop_name,
                  status, reason, requested_at, requested_by,
                  review_action, review_reason, reviewed_at, reviewed_by,
                  correlated_task_id, completed_at, completed_by, completion_notes,
                  version, created_at, updated_at
                FROM kiosk_ops.v2_term_list_requests(@status, @take, @skip)
                """, connection);

            cmd.Parameters.AddWithValue("status", NpgsqlDbType.Text, (object?)status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("take", l + 1);
            cmd.Parameters.AddWithValue("skip", (p - 1) * l);

            var list = new List<KioskTerminationResponse>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(ReadRow(reader));
            }

            var hasMore = list.Count > l;
            var items = list.Take(l).ToList();

            return Results.Ok(new KioskTerminationPage(items, p, l, hasMore));
        }).RequireAuthorization("OperationsManager").WithTags("KioskTermination").Produces<KioskTerminationPage>();

        // 4. Operations Manager: Upload private evidence ticket bound to termination ID
        app.MapPost("/api/operations/kiosk-termination-requests/{id:guid}/evidence/upload", async (
            Guid id,
            HttpContext http,
            NpgsqlDataSource data,
            CloudinaryMedia media,
            PrivateEvidence tickets,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            if (actor == Guid.Empty) return Results.Unauthorized();

            await using var connection = await data.OpenConnectionAsync(ct);

            // Active actor DB check
            var (validActor, _, actorRole) = await ValidateActiveUserAsync(connection, null, actor, ct);
            if (!validActor || actorRole != "OPERATIONS_MANAGER")
                return Results.Problem(statusCode: 403, detail: "Chỉ Operations Manager đang hoạt động mới được tải ảnh minh chứng.");

            // Verify termination request exists and is ACCEPTED
            await using var checkCmd = new NpgsqlCommand(
                "SELECT status FROM kiosk_ops.kiosk_terminations WHERE id = @id",
                connection);
            checkCmd.Parameters.AddWithValue("id", id);
            var statusObj = await checkCmd.ExecuteScalarAsync(ct);
            if (statusObj is null or DBNull)
                return Results.NotFound();

            var status = (string)statusObj;
            if (status != "ACCEPTED")
                return Results.Problem(statusCode: 409, detail: "Chỉ tải ảnh minh chứng cho hồ sơ đang trong quá trình chấm dứt (ACCEPTED).");

            return await PrivateEvidence.UploadAsync(
                http, media, tickets, "kiosk_termination", $"manager:{actor}:{id}", ct);
        }).RequireAuthorization("OperationsManager").WithMetadata(new RequestSizeLimitAttribute(CloudinaryMedia.MaximumBytes)).Produces<EvidenceUploadResponse>();

        // 5. Operations Manager: Review termination request (Accept / Reject)
        app.MapPost("/api/operations/kiosk-termination-requests/{id:guid}/review", async (
            Guid id,
            ReviewKioskTerminationRequest input,
            HttpContext http,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            if (actor == Guid.Empty) return Results.Unauthorized();

            await using var connection = await data.OpenConnectionAsync(ct);

            // Active actor DB check
            var (validActor, _, actorRole) = await ValidateActiveUserAsync(connection, null, actor, ct);
            if (!validActor || actorRole != "OPERATIONS_MANAGER")
                return Results.Problem(statusCode: 403, detail: "Chỉ Operations Manager đang hoạt động mới được thực hiện duyệt.");

            var errors = new Dictionary<string, string[]>();
            var action = input.Action?.Trim().ToUpperInvariant();
            if (action is not ("ACCEPT" or "REJECT"))
                errors["action"] = ["Hành động chỉ chấp nhận ACCEPT hoặc REJECT."];

            if (string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length is < 10 or > 2000 || input.Reason.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
                errors["reason"] = ["Lý do duyệt từ 10 đến 2000 ký tự, không chứa ký tự điều khiển không hợp lệ."];

            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            await using var tx = await connection.BeginTransactionAsync(ct);

            await using var cmd = new NpgsqlCommand(
                "SELECT flow.review_kiosk_termination(@id, @manager, @action, @reason)",
                connection, tx);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("manager", actor);
            cmd.Parameters.AddWithValue("action", action!);
            cmd.Parameters.AddWithValue("reason", input.Reason.Trim());

            try
            {
                await cmd.ExecuteScalarAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "40001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                return Results.Problem(statusCode: 403, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "P0002")
            {
                return Results.NotFound();
            }
            catch (PostgresException ex) when (ex.SqlState == "22023")
            {
                return Results.Problem(statusCode: 400, detail: ex.MessageText);
            }

            var record = await LoadTerminationAsync(connection, tx, id, ct);
            await tx.CommitAsync(ct);

            return record is not null ? Results.Ok(record) : Results.NotFound();
        }).RequireAuthorization("OperationsManager").WithTags("KioskTermination").Produces<KioskTerminationResponse>();

        // 6. Operations Manager: Complete termination request (fail closed, same-transaction attachment save, consistent lock order)
        app.MapPost("/api/operations/kiosk-termination-requests/{id:guid}/complete", async (
            Guid id,
            CompleteKioskTerminationRequest input,
            HttpContext http,
            NpgsqlDataSource data,
            PrivateEvidence? tickets,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            if (actor == Guid.Empty) return Results.Unauthorized();

            await using var connection = await data.OpenConnectionAsync(ct);

            // Active actor DB check
            var (validActor, _, actorRole) = await ValidateActiveUserAsync(connection, null, actor, ct);
            if (!validActor || actorRole != "OPERATIONS_MANAGER")
                return Results.Problem(statusCode: 403, detail: "Chỉ Operations Manager đang hoạt động mới được hoàn tất chấm dứt.");

            await using var tx = await connection.BeginTransactionAsync(ct);

            // 1. Initial nonlocking lookup to discover kiosk_id and seller_id
            await using var lookupCmd = new NpgsqlCommand(
                "SELECT kiosk_id, seller_id, status FROM kiosk_ops.kiosk_terminations WHERE id = @id",
                connection, tx);
            lookupCmd.Parameters.AddWithValue("id", id);
            await using var lookupReader = await lookupCmd.ExecuteReaderAsync(ct);
            if (!await lookupReader.ReadAsync(ct))
                return Results.NotFound();

            var kioskId = lookupReader.GetGuid(0);
            var sellerId = lookupReader.GetGuid(1);
            var currentStatus = lookupReader.GetString(2);
            await lookupReader.DisposeAsync();

            // Duplicate complete idempotent: recheck actor and return without consuming new evidence
            if (currentStatus == "COMPLETED")
            {
                var existing = await LoadTerminationAsync(connection, tx, id, ct);
                await tx.CommitAsync(ct);
                return existing is not null ? Results.Ok(existing) : Results.NotFound();
            }

            if (currentStatus != "ACCEPTED")
            {
                return Results.Problem(statusCode: 409, detail: $"Chỉ hoàn tất hồ sơ đã được chấp thuận (ACCEPTED), trạng thái hiện tại: {currentStatus}");
            }

            // 2. Consistent lock order: Kiosk -> Slots -> Seller -> Request
            await using var lockKioskCmd = new NpgsqlCommand(
                "SELECT 1 FROM kiosk_ops.kiosks WHERE id = @kiosk FOR UPDATE",
                connection, tx);
            lockKioskCmd.Parameters.AddWithValue("kiosk", kioskId);
            await lockKioskCmd.ExecuteNonQueryAsync(ct);

            await using var lockSlotsCmd = new NpgsqlCommand(
                "SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id = @kiosk ORDER BY id FOR UPDATE",
                connection, tx);
            lockSlotsCmd.Parameters.AddWithValue("kiosk", kioskId);
            await lockSlotsCmd.ExecuteNonQueryAsync(ct);

            await using var lockSellerCmd = new NpgsqlCommand(
                "SELECT kiosk_ops.v2_term_lock_seller(@seller)",
                connection, tx);
            lockSellerCmd.Parameters.AddWithValue("seller", sellerId);
            await lockSellerCmd.ExecuteNonQueryAsync(ct);

            await using var lockReqCmd = new NpgsqlCommand(
                "SELECT status FROM kiosk_ops.kiosk_terminations WHERE id = @id FOR UPDATE",
                connection, tx);
            lockReqCmd.Parameters.AddWithValue("id", id);
            var recheckStatus = (string?)(await lockReqCmd.ExecuteScalarAsync(ct));
            if (recheckStatus == "COMPLETED")
            {
                var existing = await LoadTerminationAsync(connection, tx, id, ct);
                await tx.CommitAsync(ct);
                return existing is not null ? Results.Ok(existing) : Results.NotFound();
            }

            Guid? evidenceId = null;

            // 3. If an evidence reference ticket is supplied, resolve and persist in the SAME transaction
            if (!string.IsNullOrWhiteSpace(input.EvidenceReference))
            {
                if (tickets is null)
                    return Results.Problem(statusCode: 503, detail: "Dịch vụ xác thực minh chứng chưa sẵn sàng.");

                VerifiedMedia media;
                try
                {
                    media = tickets.Resolve(input.EvidenceReference.Trim(), "kiosk_termination", $"manager:{actor}:{id}");
                }
                catch (ArgumentException ex)
                {
                    return Results.Problem(statusCode: 400, detail: ex.Message);
                }

                evidenceId = Guid.CreateVersion7();

                if (!await PrivateEvidence.LockUnusedAsync(connection, tx, media.Url, ct))
                    return Results.Problem(statusCode: 409, detail: "Ảnh minh chứng đã được gắn vào hồ sơ khác.");

                await using var attachCmd = new NpgsqlCommand(
                    "SELECT kiosk_ops.v2_term_attach_evidence(@attId, @termId, @url, @mime, @size, @hash, @actor)",
                    connection, tx);
                attachCmd.Parameters.AddWithValue("attId", evidenceId.Value);
                attachCmd.Parameters.AddWithValue("termId", id);
                attachCmd.Parameters.AddWithValue("url", media.Url);
                attachCmd.Parameters.AddWithValue("mime", media.MimeType);
                attachCmd.Parameters.AddWithValue("size", media.SizeBytes);
                attachCmd.Parameters.AddWithValue("hash", media.Sha256);
                attachCmd.Parameters.AddWithValue("actor", actor);
                await attachCmd.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = new NpgsqlCommand(
                "SELECT flow.complete_kiosk_termination(@id, @manager, @notes, @evidenceId)",
                connection, tx);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("manager", actor);
            cmd.Parameters.AddWithValue("notes", NpgsqlDbType.Text, (object?)input.Notes?.Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("evidenceId", NpgsqlDbType.Uuid, (object?)evidenceId ?? DBNull.Value);

            try
            {
                await cmd.ExecuteScalarAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState is "40001" or "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                return Results.Problem(statusCode: 403, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "P0002")
            {
                return Results.NotFound();
            }
            catch (PostgresException ex) when (ex.SqlState == "22023")
            {
                return Results.Problem(statusCode: 400, detail: ex.MessageText);
            }

            var record = await LoadTerminationAsync(connection, tx, id, ct);
            await tx.CommitAsync(ct);

            return record is not null ? Results.Ok(record) : Results.NotFound();
        }).RequireAuthorization("OperationsManager").WithTags("KioskTermination").Produces<KioskTerminationResponse>();

        return app;
    }

    private static Guid Actor(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue("sub"), out var sub) ? sub : Guid.Empty;

    private static async Task<(bool Valid, Guid? SellerId, string? Role)> ValidateActiveUserAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        Guid userId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT status, role, seller_id FROM kiosk_ops.v2_term_get_user(@id)",
            conn, tx);
        cmd.Parameters.AddWithValue("id", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var status = reader.GetString(0);
            var role = reader.GetString(1);
            var sellerId = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2);
            return (status == "ACTIVE", sellerId, role);
        }
        return (false, null, null);
    }

    private static async Task<KioskTerminationResponse?> LoadTerminationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        Guid id,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT
              id, kiosk_id, kiosk_code, kiosk_name, seller_id, shop_name,
              status, reason, requested_at, requested_by,
              review_action, review_reason, reviewed_at, reviewed_by,
              correlated_task_id, completed_at, completed_by, completion_notes,
              version, created_at, updated_at
            FROM kiosk_ops.v2_term_get_request(@id)
            """, connection, tx);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadRow(reader);
        }
        return null;
    }

    private static KioskTerminationResponse ReadRow(NpgsqlDataReader reader) =>
        new(
            Id: reader.GetGuid(0),
            KioskId: reader.GetGuid(1),
            KioskCode: reader.IsDBNull(2) ? null : reader.GetString(2),
            KioskName: reader.IsDBNull(3) ? null : reader.GetString(3),
            SellerId: reader.GetGuid(4),
            ShopName: reader.IsDBNull(5) ? null : reader.GetString(5),
            Status: reader.GetString(6),
            Reason: reader.GetString(7),
            RequestedAt: reader.GetDateTime(8),
            RequestedBy: reader.GetGuid(9),
            ReviewAction: reader.IsDBNull(10) ? null : reader.GetString(10),
            ReviewReason: reader.IsDBNull(11) ? null : reader.GetString(11),
            ReviewedAt: reader.IsDBNull(12) ? null : reader.GetDateTime(12),
            ReviewedBy: reader.IsDBNull(13) ? null : reader.GetGuid(13),
            CorrelatedTaskId: reader.IsDBNull(14) ? null : reader.GetGuid(14),
            CompletedAt: reader.IsDBNull(15) ? null : reader.GetDateTime(15),
            CompletedBy: reader.IsDBNull(16) ? null : reader.GetGuid(16),
            CompletionNotes: reader.IsDBNull(17) ? null : reader.GetString(17),
            Version: reader.GetInt32(18),
            CreatedAt: reader.GetDateTime(19),
            UpdatedAt: reader.GetDateTime(20)
        );
}
