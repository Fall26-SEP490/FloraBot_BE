using System.Security.Claims;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.Ordering;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record ManualUnlockRetryRequest(Guid RequestId, string? TrackingToken = null);

public sealed record ManualUnlockRetryResponse(
    Guid RequestId,
    Guid CommandId,
    string State,
    long RequestedAt,
    long ExpiresAt,
    long RemainingSeconds);

public sealed record RetryCandidate(
    Guid CmdId,
    string HardwareId,
    Guid KioskId,
    Guid SlotId,
    int RelayChannel,
    Guid? OrderId,
    Guid? SessionId,
    long ParentExpiresAtEpoch,
    bool IsParentAcked,
    bool IsDoorUntouched,
    bool IsKioskOnlineFresh,
    string? SnapHardware,
    int? SnapRelay,
    Guid? SnapSlot,
    bool IsSnapshotsMatching,
    bool IsSharedCapValid,
    bool IsSellerCapFull,
    bool IsSellerStateValid,
    bool IsCustomerStateValid,
    DateTimeOffset? AckOccurredAt,
    bool HasPendingOrDoorEvent);

public sealed class ManualUnlockRetryService(NpgsqlDataSource data, ICapPublisher publisher, DeviceKeyRing? keys = null)
{
    internal async Task<RetryCandidate?> LoadCandidateAsync(
        string actorKind,
        Guid actorId,
        Guid kioskId,
        Guid slotId,
        Guid? cmdId,
        Guid? orderId,
        Guid? sessionId,
        string? guestTrackingToken,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct,
        bool skipLocked = false)
    {
        await using var command = new NpgsqlCommand("""
            SELECT
                cmd_id,
                hardware_id,
                relay_channel,
                order_id,
                session_id,
                parent_expires_epoch,
                is_parent_acked,
                is_door_untouched,
                is_kiosk_online_fresh,
                hardware_id_snapshot,
                relay_channel_snapshot,
                slot_id_snapshot,
                is_snapshots_matching,
                is_shared_cap_valid,
                is_seller_cap_full,
                is_seller_state_valid,
                is_customer_state_valid,
                ack_occurred_at,
                has_pending_or_door_event
            FROM flow.load_manual_unlock_retry_candidate(
                @actorKind,
                @actorId,
                @kioskId,
                @slotId,
                @cmdId,
                @orderId,
                @sessionId,
                @guestTrackingToken,
                @skipLocked
            )
            """, connection, transaction);

        command.Parameters.AddWithValue("actorKind", actorKind);
        command.Parameters.AddWithValue("actorId", actorId);
        command.Parameters.AddWithValue("kioskId", kioskId);
        command.Parameters.AddWithValue("slotId", slotId);
        command.Parameters.AddWithValue("cmdId", NpgsqlDbType.Uuid, (object?)cmdId ?? DBNull.Value);
        command.Parameters.AddWithValue("orderId", NpgsqlDbType.Uuid, (object?)orderId ?? DBNull.Value);
        command.Parameters.AddWithValue("sessionId", NpgsqlDbType.Uuid, (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("guestTrackingToken", NpgsqlDbType.Text, (object?)guestTrackingToken ?? DBNull.Value);
        command.Parameters.AddWithValue("skipLocked", skipLocked);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var cmd = reader.GetGuid(0);
        var hardware = reader.IsDBNull(1) ? "" : reader.GetString(1);
        var relay = reader.GetInt16(2);
        var ord = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3);
        var sess = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
        var parentExpiry = reader.GetInt64(5);
        var parentAcked = reader.GetBoolean(6);
        var doorUntouched = reader.GetBoolean(7);
        var kioskOnlineFresh = reader.GetBoolean(8);
        var snapHardware = reader.IsDBNull(9) ? null : reader.GetString(9);
        var snapRelay = reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10);
        var snapSlot = reader.IsDBNull(11) ? (Guid?)null : reader.GetGuid(11);
        var snapshotsMatch = !reader.IsDBNull(12) && reader.GetBoolean(12);
        var sharedCapValid = !reader.IsDBNull(13) && reader.GetBoolean(13);
        var sellerCapFull = !reader.IsDBNull(14) && reader.GetBoolean(14);
        var sellerStateValid = !reader.IsDBNull(15) && reader.GetBoolean(15);
        var customerStateValid = !reader.IsDBNull(16) && reader.GetBoolean(16);
        var ackOccurredAt = reader.IsDBNull(17) ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(17), DateTimeKind.Utc));
        var hasPendingOrDoor = reader.GetBoolean(18);

        return new RetryCandidate(
            cmd, hardware, kioskId, slotId, relay, ord, sess, parentExpiry,
            parentAcked, doorUntouched, kioskOnlineFresh,
            snapHardware, snapRelay, snapSlot, snapshotsMatch,
            sharedCapValid, sellerCapFull, sellerStateValid, customerStateValid,
            ackOccurredAt, hasPendingOrDoor);
    }

    internal async Task<IResult> RequestSellerRetryAsync(
        Guid kioskId, Guid slotId, Guid sessionId, ManualUnlockRetryRequest input,
        Guid actorId, HttpContext http, CancellationToken ct,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<MaintenanceAccessVerification>>? confirmAccess = null)
    {
        http.Response.Headers.CacheControl = "no-store";

        if (keys is null)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (input.RequestId == Guid.Empty)
            return Results.Problem(statusCode: 400, detail: "RequestId is required.");

        try
        {
            await using var connection = await data.OpenConnectionAsync(ct);
            using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
            var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
            var verified = confirmAccess is null ? MaintenanceAccessVerification.Valid
                : await confirmAccess(connection, transaction, ct);
            if (verified != MaintenanceAccessVerification.Valid)
            {
                if (verified == MaintenanceAccessVerification.Invalid)
                {
                    await outbox.CommitAsync(ct);
                    return Results.Problem(statusCode: 400, detail: "Mã xác nhận không hợp lệ.");
                }
                return Results.NotFound();
            }
            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);

            await using var setLockTimeout = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", connection, transaction);
            await setLockTimeout.ExecuteNonQueryAsync(ct);

            var candidate = await LoadCandidateAsync(
                "SELLER", actorId, kioskId, slotId, null, null, sessionId, null,
                connection, transaction, ct);

            if (candidate is null)
                return Results.NotFound();

            var execution = await ExecutePersistAndAuditAsync(
                candidate, "SELLER", actorId, actorId, input.RequestId,
                connection, transaction, outbox, publisher, watermark, http, ct);

            return execution;
        }
        catch (PostgresException ex) when (ex.SqlState is "55P03" or "40P01" or "23505")
        {
            return Results.Problem(statusCode: 409, detail: "Conflict or lock timeout processing retry request.");
        }
    }

    public async Task<IResult> RequestCustomerRetryAsync(
        Guid kioskId, Guid orderId, Guid cmdId, ManualUnlockRetryRequest input,
        HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";

        if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString())
            return Results.Forbid();

        var role = http.User.FindFirstValue("role") ?? http.User.FindFirstValue(ClaimTypes.Role);
        if (role is not ("CUSTOMER" or "KIOSK"))
            return Results.Forbid();

        if (keys is null)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (input.RequestId == Guid.Empty)
            return Results.Problem(statusCode: 400, detail: "RequestId is required.");

        var isGuest = role == "KIOSK";
        Guid actorId;
        string? guestToken = null;

        if (isGuest)
        {
            guestToken = ReceiptAccess.NormalizeToken(input.TrackingToken);
            if (guestToken is null)
                return Results.NotFound();

            actorId = kioskId;
        }
        else
        {
            var subClaim = http.User.FindFirstValue("sub");
            if (string.IsNullOrEmpty(subClaim) || !Guid.TryParse(subClaim, out actorId) || actorId == Guid.Empty)
                return Results.Unauthorized();
        }

        var actorKind = isGuest ? "GUEST" : "CUSTOMER";

        try
        {
            await using var connection = await data.OpenConnectionAsync(ct);
            using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
            var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);

            await using var setLockTimeout = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", connection, transaction);
            await setLockTimeout.ExecuteNonQueryAsync(ct);

            // Find slot_id from parent unlock_tokens first for scoping
            await using var slotFind = new NpgsqlCommand("SELECT slot_id FROM kiosk_ops.unlock_tokens WHERE cmd_id = @cmdId AND kiosk_id = @kioskId", connection, transaction);
            slotFind.Parameters.AddWithValue("cmdId", cmdId);
            slotFind.Parameters.AddWithValue("kioskId", kioskId);
            var slotObj = await slotFind.ExecuteScalarAsync(ct);
            if (slotObj is not Guid slotId)
                return Results.NotFound();

            var candidate = await LoadCandidateAsync(
                actorKind, actorId, kioskId, slotId, cmdId, orderId, null, guestToken,
                connection, transaction, ct);

            if (candidate is null)
                return Results.NotFound();

            Guid? auditActor = isGuest ? null : actorId;
            var execution = await ExecutePersistAndAuditAsync(
                candidate, actorKind, actorId, auditActor, input.RequestId,
                connection, transaction, outbox, publisher, watermark, http, ct);

            return execution;
        }
        catch (PostgresException ex) when (ex.SqlState is "55P03" or "40P01" or "23505")
        {
            return Results.Problem(statusCode: 409, detail: "Conflict or lock timeout processing retry request.");
        }
    }

    private IResult? EvaluateCandidate(RetryCandidate candidate, string actorKind, HttpContext http)
    {
        if (!keys!.HardwareIds.Contains(candidate.HardwareId))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var denial = GetDenial(candidate, actorKind, DateTimeOffset.UtcNow, out var cooldown);
        if (denial is null) return null;
        if (cooldown > 0)
            http.Response.Headers.RetryAfter = cooldown.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(statusCode: 409, detail: denial);
    }

    internal static string? GetDenial(RetryCandidate candidate, string actorKind, DateTimeOffset nowUtc, out long cooldown)
    {
        cooldown = 0;
        if (candidate.SnapHardware is null || candidate.SnapRelay is null || candidate.SnapSlot is null)
            return "Legacy dispatch snapshot cannot be retried.";

        if (!candidate.IsSnapshotsMatching)
            return "Hardware or relay snapshot mismatch.";

        if (!candidate.IsParentAcked)
            return "Parent command must be ACKED to retry.";

        if (!candidate.IsDoorUntouched)
            return "Door has already been opened or closed.";

        var nowSec = nowUtc.ToUnixTimeSeconds();
        if (candidate.ParentExpiresAtEpoch <= nowSec)
            return "Parent command has expired.";

        if (!candidate.IsKioskOnlineFresh)
            return "Kiosk is offline or heartbeat is stale.";

        if (!candidate.IsSharedCapValid)
            return "Slot capability does not support manual retry.";

        if (actorKind == "SELLER")
        {
            if (!candidate.IsSellerCapFull || !candidate.IsSellerStateValid)
                return "Seller maintenance session or capability invalid.";
        }
        else
        {
            if (!candidate.IsCustomerStateValid)
                return "Customer order or held slot state invalid.";
        }

        if (candidate.HasPendingOrDoorEvent)
            return "Pending inbox event or door opened/closed exists.";

        if (candidate.AckOccurredAt is null)
            return "Genuine signed ACK receipt not found.";

        var elapsed = nowUtc - candidate.AckOccurredAt.Value;
        if (elapsed < TimeSpan.FromSeconds(31))
        {
            cooldown = (long)Math.Ceiling((TimeSpan.FromSeconds(31) - elapsed).TotalSeconds);
            return $"Retry cooldown active. Remaining seconds: {cooldown}.";
        }

        return null;
    }

    private async Task<IResult> ExecutePersistAndAuditAsync(
        RetryCandidate candidate,
        string actorKind,
        Guid actorId,
        Guid? auditActor,
        Guid requestId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICapTransaction outbox,
        ICapPublisher publisher,
        long watermark,
        HttpContext http,
        CancellationToken ct)
    {
        // Idempotency is checked only after the candidate has passed ownership scope.
        await using var existingCmd = new NpgsqlCommand("""
            SELECT request_id, actor_kind, actor_id, state, requested_at, expires_at
            FROM kiosk_ops.manual_unlock_retries
            WHERE parent_cmd_id = @cmdId
            FOR UPDATE
            """, connection, transaction);
        existingCmd.Parameters.AddWithValue("cmdId", candidate.CmdId);
        await using (var reader = await existingCmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                var existingRequestId = reader.GetGuid(0);
                var existingActorKind = reader.GetString(1);
                var existingActorId = reader.GetGuid(2);
                var existingState = reader.GetString(3);
                var existingRequestedAt = reader.GetInt64(4);
                var existingExpiresAt = reader.GetInt64(5);

                if (existingRequestId == requestId && existingActorKind == actorKind && existingActorId == actorId)
                {
                    var rem = Math.Max(0, existingExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    return Results.Ok(new ManualUnlockRetryResponse(
                        existingRequestId, candidate.CmdId, existingState,
                        existingRequestedAt, existingExpiresAt, rem));
                }

                return Results.Problem(statusCode: 409, detail: "A manual retry request already exists for this command.");
            }
        }

        // Idempotency: same requestId with different command
        await using var checkReqCmd = new NpgsqlCommand("SELECT 1 FROM kiosk_ops.manual_unlock_retries WHERE request_id = @requestId", connection, transaction);
        checkReqCmd.Parameters.AddWithValue("requestId", requestId);
        if (await checkReqCmd.ExecuteScalarAsync(ct) is not null)
            return Results.Problem(statusCode: 409, detail: "RequestId already exists with a different command.");

        var evaluation = EvaluateCandidate(candidate, actorKind, http);
        if (evaluation is not null) return evaluation;

        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiresAtSec = Math.Min(nowSec + 30, candidate.ParentExpiresAtEpoch);
        if (expiresAtSec <= nowSec)
            return Results.Problem(statusCode: 409, detail: "Parent command has expired.");

        // Insert new retry row
        await using var insertCmd = new NpgsqlCommand("""
            INSERT INTO kiosk_ops.manual_unlock_retries(
                request_id, parent_cmd_id, kiosk_id, slot_id, order_id, session_id,
                actor_kind, actor_id, hardware_id_snapshot, relay_channel_snapshot, slot_id_snapshot,
                requested_at, expires_at, state, next_attempt_at
            ) VALUES (
                @requestId, @cmdId, @kioskId, @slotId, @orderId, @sessionId,
                @actorKind, @actorId, @snapHardware, @snapRelay, @snapSlot,
                @requestedAt, @expiresAt, 'PENDING', CURRENT_TIMESTAMP
            ) ON CONFLICT DO NOTHING
            """, connection, transaction);
        insertCmd.Parameters.AddWithValue("requestId", requestId);
        insertCmd.Parameters.AddWithValue("cmdId", candidate.CmdId);
        insertCmd.Parameters.AddWithValue("kioskId", candidate.KioskId);
        insertCmd.Parameters.AddWithValue("slotId", candidate.SlotId);
        insertCmd.Parameters.AddWithValue("orderId", NpgsqlDbType.Uuid, (object?)candidate.OrderId ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("sessionId", NpgsqlDbType.Uuid, (object?)candidate.SessionId ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("actorKind", actorKind);
        insertCmd.Parameters.AddWithValue("actorId", actorId);
        insertCmd.Parameters.AddWithValue("snapHardware", candidate.SnapHardware!);
        insertCmd.Parameters.AddWithValue("snapRelay", candidate.SnapRelay!.Value);
        insertCmd.Parameters.AddWithValue("snapSlot", candidate.SnapSlot!.Value);
        insertCmd.Parameters.AddWithValue("requestedAt", nowSec);
        insertCmd.Parameters.AddWithValue("expiresAt", expiresAtSec);

        var rows = await insertCmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
        {
            // Race winner resolved by ON CONFLICT DO NOTHING
            await using var raceCmd = new NpgsqlCommand("SELECT request_id, actor_kind, actor_id, state, requested_at, expires_at FROM kiosk_ops.manual_unlock_retries WHERE parent_cmd_id = @cmdId", connection, transaction);
            raceCmd.Parameters.AddWithValue("cmdId", candidate.CmdId);
            await using var raceReader = await raceCmd.ExecuteReaderAsync(ct);
            if (await raceReader.ReadAsync(ct))
            {
                if (raceReader.GetGuid(0) == requestId && raceReader.GetString(1) == actorKind && raceReader.GetGuid(2) == actorId)
                {
                    var rem = Math.Max(0, raceReader.GetInt64(5) - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    return Results.Ok(new ManualUnlockRetryResponse(requestId, candidate.CmdId, raceReader.GetString(3), raceReader.GetInt64(4), raceReader.GetInt64(5), rem));
                }
            }
            return Results.Problem(statusCode: 409, detail: "A manual retry request already exists for this command.");
        }

        // Audit without tracking token
        await using var auditCmd = new NpgsqlCommand("""
            SELECT flow.audit(
                @actor,
                'MANUAL_UNLOCK_RETRY_REQUESTED',
                'manual_unlock_retries',
                @requestId,
                jsonb_build_object(
                    'parent_cmd_id', @cmdId,
                    'kiosk_id', @kioskId,
                    'slot_id', @slotId,
                    'order_id', @orderId,
                    'session_id', @sessionId,
                    'actor_kind', @actorKind
                )
            )
            """, connection, transaction);
        auditCmd.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, (object?)auditActor ?? DBNull.Value);
        auditCmd.Parameters.AddWithValue("requestId", requestId);
        auditCmd.Parameters.AddWithValue("cmdId", candidate.CmdId);
        auditCmd.Parameters.AddWithValue("kioskId", candidate.KioskId);
        auditCmd.Parameters.AddWithValue("slotId", candidate.SlotId);
        auditCmd.Parameters.AddWithValue("orderId", NpgsqlDbType.Uuid, (object?)candidate.OrderId ?? DBNull.Value);
        auditCmd.Parameters.AddWithValue("sessionId", NpgsqlDbType.Uuid, (object?)candidate.SessionId ?? DBNull.Value);
        auditCmd.Parameters.AddWithValue("actorKind", actorKind);
        await auditCmd.ExecuteNonQueryAsync(ct);

        await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
        await outbox.CommitAsync(ct);

        return Results.Ok(new ManualUnlockRetryResponse(
            requestId, candidate.CmdId, "PENDING", nowSec, expiresAtSec, expiresAtSec - nowSec));
    }
}

public static class ManualUnlockRetries
{
    public static IEndpointRouteBuilder MapManualUnlockRetries(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/me/kiosks/{kioskId:guid}/slots/{slotId:guid}/maintenance-sessions/{sessionId:guid}/retry-unlock",
            (Guid kioskId, Guid slotId, Guid sessionId) => Results.Problem(statusCode: 409, detail: "Hãy xác nhận mã mở ô tại kiosk."))
            .RequireAuthorization("Seller").RequireRateLimiting("auth").WithTags("KioskOps").ProducesProblem(409);

        app.MapPost("/api/kiosks/{kioskId:guid}/pickup/orders/{orderId:guid}/commands/{cmdId:guid}/retry-unlock",
            async (Guid kioskId, Guid orderId, Guid cmdId, [FromBody] ManualUnlockRetryRequest input,
                HttpContext http, ManualUnlockRetryService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return await service.RequestCustomerRetryAsync(kioskId, orderId, cmdId, input, http, ct);
            })
            .RequireAuthorization("Shopping")
            .RequireRateLimiting("auth")
            .Produces<ManualUnlockRetryResponse>()
            .WithTags("KioskOps");

        return app;
    }
}
