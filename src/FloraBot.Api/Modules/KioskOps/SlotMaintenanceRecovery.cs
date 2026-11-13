using System.Security.Claims;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

internal sealed record RecoverMaintenanceRequest(
    bool ConfirmedDoorClosed, string? ObservedContents, Guid? ExpectedBouquetId, string? Reason);

internal sealed record MaintenanceRecoveryCandidate(Guid SessionId, string Action, string Stage,
    Guid? BouquetId, DateTimeOffset ExpiresAt, Guid? CommandId);

internal sealed record MaintenanceRecoveryResult(Guid SessionId, string Stage, string SlotStatus);
internal sealed record MaintenanceRecoveryRow(Guid KioskId, string KioskCode, string KioskName,
    Guid SlotId, string SlotCode, Guid SessionId, string Action, Guid? BouquetId, DateTimeOffset ExpiresAt);
internal sealed record MaintenanceRecoveryPage(IReadOnlyList<MaintenanceRecoveryRow> Items, bool HasMore);

internal static class SlotMaintenanceRecoveryEndpoints
{
    private const string Path = "/api/operations/kiosks/{kioskId:guid}/slots/{slotId:guid}/maintenance-recovery";

    internal static void MapSlotMaintenanceRecovery(this WebApplication app)
    {
        app.MapGet("/api/operations/maintenance-recoveries", async (int? page, HttpContext http,
            SlotMaintenanceService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var managerId)) return Results.Forbid();
            var number = page ?? 1;
            if (number is < 1 or > 100000) return Results.BadRequest();
            return Results.Ok(await service.ListRecoveryCandidatesAsync(managerId, number, ct));
        }).RequireAuthorization("OperationsManager").WithTags("KioskOps")
            .WithSummary("Danh sách phiên bảo trì hết hạn thuộc phạm vi quản lý")
            .Produces<MaintenanceRecoveryPage>().Produces(400);

        app.MapGet(Path, async (Guid kioskId, Guid slotId, HttpContext http,
            SlotMaintenanceService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var managerId)) return Results.Forbid();
            return await service.GetRecoveryCandidateAsync(kioskId, slotId, managerId, ct);
        }).RequireAuthorization("OperationsManager").WithTags("KioskOps")
            .WithSummary("Xem phiên bảo trì hết hạn của ô thuộc phạm vi quản lý")
            .Produces<MaintenanceRecoveryCandidate>().Produces(404);

        app.MapPost(Path + "/{sessionId:guid}", async (Guid kioskId, Guid slotId, Guid sessionId,
            RecoverMaintenanceRequest input, HttpContext http, SlotMaintenanceService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var managerId)) return Results.Forbid();
            return await service.RecoverExpiredSessionAsync(kioskId, slotId, sessionId, managerId, input, ct);
        }).RequireAuthorization("OperationsManager").WithTags("KioskOps")
            .WithSummary("Kết thúc phiên bảo trì hết hạn sau khi kiểm tra ô tại tủ")
            .Produces<MaintenanceRecoveryResult>().Produces(404).ProducesProblem(400).ProducesProblem(409);
    }
}

public sealed partial class SlotMaintenanceService
{
    private const string RecoveryCandidates = """
        FROM kiosk_ops.slot_maintenance_sessions ms
        JOIN kiosk_ops.slots s ON s.id=ms.slot_id AND s.kiosk_id=ms.kiosk_id
        JOIN kiosk_ops.kiosks k ON k.id=ms.kiosk_id AND k.seller_id=ms.seller_id
        JOIN kiosk_ops.manager_kiosks m ON m.kiosk_id=ms.kiosk_id AND m.manager_id=@manager
        JOIN identity.users u ON u.id=m.manager_id AND u.role='OPERATIONS_MANAGER' AND u.status='ACTIVE'
        WHERE s.current_seller_id=ms.seller_id AND s.status='LOCKED' AND s.zone='WALK_IN'
          AND ms.stage NOT IN ('Completed','Recovered') AND ms.expires_at<=clock_timestamp()
        """;

    internal async Task<MaintenanceRecoveryPage> ListRecoveryCandidatesAsync(Guid managerId, int page, CancellationToken ct)
    {
        const int pageSize = 20;
        await using var command = data.CreateCommand($"""
            SELECT k.id, k.code, k.name, s.id, s.slot_code, ms.id, ms.action, s.bouquet_id, ms.expires_at
            {RecoveryCandidates}
            ORDER BY ms.expires_at DESC, ms.id DESC LIMIT @limit OFFSET @offset
            """);
        command.Parameters.AddWithValue("manager", managerId);
        command.Parameters.AddWithValue("limit", pageSize + 1);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<MaintenanceRecoveryRow>();
        while (items.Count < pageSize && await reader.ReadAsync(ct))
            items.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetGuid(3), reader.GetString(4), reader.GetGuid(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7), new DateTimeOffset(reader.GetDateTime(8))));
        return new(items, await reader.ReadAsync(ct));
    }

    internal async Task<IResult> GetRecoveryCandidateAsync(Guid kioskId, Guid slotId, Guid managerId,
        CancellationToken ct)
    {
        await using var command = data.CreateCommand($"""
            SELECT ms.id, ms.action, ms.stage, s.bouquet_id, ms.expires_at, ms.current_cmd_id
            {RecoveryCandidates}
              AND ms.kiosk_id=@kiosk AND ms.slot_id=@slot
            """);
        command.Parameters.AddWithValue("kiosk", kioskId);
        command.Parameters.AddWithValue("slot", slotId);
        command.Parameters.AddWithValue("manager", managerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return Results.NotFound();
        return Results.Ok(new MaintenanceRecoveryCandidate(reader.GetGuid(0), reader.GetString(1),
            reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
            new DateTimeOffset(reader.GetDateTime(4)), reader.IsDBNull(5) ? null : reader.GetGuid(5)));
    }

    internal async Task<IResult> RecoverExpiredSessionAsync(Guid kioskId, Guid slotId, Guid sessionId,
        Guid managerId, RecoverMaintenanceRequest input, CancellationToken ct)
    {
        var reason = input.Reason?.Trim();
        if (!input.ConfirmedDoorClosed || input.ObservedContents is not ("EMPTY" or "ORIGINAL_BOUQUET")
            || reason?.Length is not (>= 5 and <= 500))
            return Results.Problem(statusCode: 400, detail: "Xác nhận cửa đã đóng tại tủ, chọn tình trạng hoa và nhập lý do từ 5 đến 500 ký tự.");

        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        await using var command = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", connection, transaction);
        command.Parameters.AddWithValue("kiosk", kioskId);
        command.Parameters.AddWithValue("slot", slotId);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("manager", managerId);

        try
        {
            await command.ExecuteNonQueryAsync(ct);
            command.CommandText = """
                SELECT k.seller_id, k.status='ONLINE'
                    AND k.last_heartbeat_at >= clock_timestamp()-make_interval(secs=>@freshness)
                FROM kiosk_ops.kiosks k
                JOIN kiosk_ops.manager_kiosks m ON m.kiosk_id=k.id AND m.manager_id=@manager
                JOIN identity.users u ON u.id=m.manager_id AND u.role='OPERATIONS_MANAGER' AND u.status='ACTIVE'
                WHERE k.id=@kiosk AND k.seller_id IS NOT NULL
                FOR SHARE OF k,m,u
                """;
            command.Parameters.AddWithValue("freshness", SlotMaintenancePolicy.TelemetryFreshnessSeconds);
            Guid sellerId;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return Results.NotFound();
                sellerId = reader.GetGuid(0);
                if (reader.IsDBNull(1) || !reader.GetBoolean(1))
                    return Results.Problem(statusCode: 409, detail: "Kiosk chưa trực tuyến hoặc tín hiệu kết nối đã cũ.");
            }

            var auth = await LoadAndLockContextAsync(connection, transaction, kioskId, slotId, sellerId,
                actorId: null, sessionId, ct);
            if (auth?.Session is not { } session) return Results.NotFound();
            if (session.Stage is "Completed" or "Recovered" || session.ExpiresAt > DateTime.UtcNow
                || auth.Slot.Status != "LOCKED" || auth.Slot.Zone != "WALK_IN"
                || auth.Slot.HoldOrderId.HasValue || auth.Slot.HoldUntil.HasValue)
                return Results.Problem(statusCode: 409, detail: "Chỉ khôi phục ô Walk-in đang khóa bởi phiên hết hạn và không giữ đơn.");

            var bouquetId = auth.Slot.BouquetId;
            var hasFlower = input.ObservedContents == "ORIGINAL_BOUQUET";
            if (input.ExpectedBouquetId != bouquetId || (bouquetId.HasValue && bouquetId != session.OldBouquetId)
                || (!bouquetId.HasValue && session.IntendedStage == "AwaitRemoval")
                || (hasFlower && !bouquetId.HasValue))
                return Results.Problem(statusCode: 409, detail: "Thông tin bó hoa đã thay đổi hoặc không khớp phiên. Tải lại và kiểm tra tại tủ.");

            if (!auth.Capability.IsFullyVerifiedFor(kioskId, auth.Slot.RelayChannel))
                return Results.Problem(statusCode: 409, detail: "Ô chưa có đủ cảm biến đã xác minh.");

            command.Parameters.AddWithValue("seller", sellerId);
            command.Parameters.Add("bouquet", NpgsqlDbType.Uuid).Value = (object?)bouquetId ?? DBNull.Value;
            command.CommandText = """
                SELECT 1 FROM kiosk_ops.slot_assignments
                WHERE slot_id=@slot AND seller_id=@seller AND status='ACTIVE' FOR SHARE
                """;
            if (await command.ExecuteScalarAsync(ct) is null)
                return Results.Problem(statusCode: 409, detail: "Ô không còn được giao cho shop.");

            string? bouquetStatus = null;
            bool sellable = false;
            if (bouquetId.HasValue)
            {
                command.CommandText = """
                    SELECT b.status, b.sellable_until>clock_timestamp() AND flow.slot_sellable(@slot,@seller)
                        AND flow.check_seller_active_product(b.product_id,@seller) IS NOT NULL
                        AND @sellerActive,
                        flow.check_bouquet_active_order(b.id) IS NOT NULL
                    FROM kiosk_ops.bouquets b WHERE b.id=@bouquet AND b.seller_id=@seller FOR UPDATE OF b
                    """;
                command.Parameters.AddWithValue("sellerActive", auth.Seller.Status == "ACTIVE");
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct) || reader.GetBoolean(2)
                    || reader.GetString(0) is "HELD" or "PICKED_UP" or "RETURNED")
                    return Results.Problem(statusCode: 409, detail: "Bó hoa đang gắn với đơn hoặc có trạng thái chưa thể khôi phục.");
                bouquetStatus = reader.GetString(0);
                sellable = reader.GetBoolean(1);
            }

            // Serialize with MQTT dispatch and event processing before reading their current state.
            command.CommandText = "SELECT 1 FROM kiosk_ops.unlock_tokens WHERE slot_id=@slot ORDER BY id FOR UPDATE";
            await command.ExecuteNonQueryAsync(ct);
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM kiosk_ops.unlock_tokens t WHERE t.slot_id=@slot AND (
                        t.status='OPENED' OR (t.door_opened_at IS NOT NULL AND t.door_closed_at IS NULL)
                        OR (t.status IN ('ISSUED','SENT','ACKED') AND t.expires_at>clock_timestamp())
                        OR EXISTS (SELECT 1 FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id=t.cmd_id AND i.disposition='PENDING')
                    )
                )
                """;
            if (await command.ExecuteScalarAsync(ct) is true)
                return Results.Problem(statusCode: 409, detail: "Cửa đang mở, còn lệnh mở hiệu lực hoặc còn sự kiện thiết bị chưa xử lý.");

            command.CommandText = """
                SELECT observed_at, received_at, states FROM kiosk_ops.flower_presence WHERE kiosk_id=@kiosk FOR SHARE
                """;
            SensorObservation? observation = null;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                    observation = new(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                        new DateTimeOffset(reader.GetDateTime(1)), reader.GetFieldValue<int[]>(2), true, true);
            }
            if (observation is null || !SlotMaintenancePolicy.VerifyTelemetryFreshness(observation, auth.Slot.RelayChannel,
                    DateTimeOffset.UtcNow, out _) || observation.States![auth.Slot.RelayChannel] != (hasFlower ? 1 : 0))
                return Results.Problem(statusCode: 409, detail: "Cảm biến hoa chưa có dữ liệu mới hoặc không khớp với kiểm tra tại tủ.");

            var slotStatus = !hasFlower ? "RENTED_EMPTY"
                : bouquetStatus == "STOCKED" && sellable && session.PreviousSlotStatus == "STOCKED"
                    ? "STOCKED" : "PENDING_REMOVAL";
            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            command.Parameters.AddWithValue("status", slotStatus);
            command.Parameters.AddWithValue("hasFlower", hasFlower);
            command.Parameters.AddWithValue("reason", reason!);
            command.Parameters.AddWithValue("observed", input.ObservedContents!);
            command.CommandText = """
                UPDATE kiosk_ops.unlock_tokens SET status='REVOKED'
                WHERE slot_id=@slot AND purpose='SELLER_ACCESS' AND status IN ('ISSUED','SENT','ACKED');
                UPDATE kiosk_ops.manual_unlock_retries SET state='CANCELLED', updated_at=clock_timestamp()
                WHERE session_id=@session AND slot_id=@slot AND state='PENDING';
                UPDATE kiosk_ops.bouquets SET status=CASE WHEN status='DAMAGED' THEN 'DAMAGED' ELSE 'RETURNED' END,
                    updated_at=clock_timestamp()
                WHERE id=@bouquet AND NOT @hasFlower;
                INSERT INTO kiosk_ops.inventory_logs(bouquet_id,slot_id,movement_type,performed_by,reason)
                SELECT @bouquet,@slot,'RETURN_SELLER',@manager,@reason WHERE @bouquet IS NOT NULL AND NOT @hasFlower;
                UPDATE kiosk_ops.slots SET status=@status, bouquet_id=CASE WHEN @hasFlower THEN bouquet_id ELSE NULL END,
                    row_version=row_version+1, updated_at=clock_timestamp() WHERE id=@slot;
                UPDATE kiosk_ops.slot_maintenance_sessions SET stage='Recovered', substate=NULL,
                    row_version=row_version+1, updated_at=clock_timestamp() WHERE id=@session;
                SELECT flow.audit(@manager,'SLOT_MAINTENANCE_RECOVERED','slots',@slot,
                    jsonb_build_object('sessionId',@session,'kioskId',@kiosk,'observedContents',@observed,
                        'bouquetId',@bouquet,'from','LOCKED','to',@status,'reason',@reason));
                """;
            await command.ExecuteNonQueryAsync(ct);
            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);

            logger.LogInformation(
                "Maintenance session {SessionId} recovered at kiosk {KioskId} slot {SlotId}; command {CommandId}, slot status {SlotStatus}",
                sessionId, kioskId, slotId, session.CurrentCmdId, slotStatus);
            return Results.Ok(new MaintenanceRecoveryResult(sessionId, "Recovered", slotStatus));
        }
        catch (PostgresException ex) when (ex.SqlState is "55P03" or "40P01" or "40001")
        {
            return Results.Problem(statusCode: 409, detail: "Ô đang được xử lý đồng thời. Tải lại trạng thái trước khi thử lại.");
        }
    }
}
