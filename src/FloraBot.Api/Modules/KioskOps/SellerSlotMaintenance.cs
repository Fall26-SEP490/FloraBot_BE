using System.Security.Claims;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record StartSlotMaintenanceRequest(
    Guid Id,
    string Action,
    Guid? ExpectedBouquetId,
    Guid? ProductId,
    string Reason);

public sealed record SlotMaintenanceSessionResponse(
    Guid Id,
    Guid KioskId,
    Guid SlotId,
    string Action,
    string Stage,
    string? Substate,
    Guid? CurrentCmdId,
    DateTimeOffset PhaseStartedAt,
    DateTimeOffset ExpiresAt,
    string? InspectionReason,
    DateTimeOffset? InspectedAt,
    Guid? OldBouquetId,
    Guid? NewProductId,
    Guid? NewBouquetId,
    IReadOnlyList<string> AvailableActions,
    int? RetryAfterSeconds = null);

public sealed record ReportIssueRequest(string Reason);
public sealed record OpenForCheckRequest(string Reason);

public sealed record UpdateSensorCapabilityRequest(
    bool Enabled,
    bool DoorVerified,
    bool PresenceVerified);

public sealed record SlotSensorCapabilityResponse(
    Guid SlotId,
    Guid KioskId,
    int RelayChannel,
    bool Enabled,
    bool DoorVerified,
    bool PresenceVerified,
    bool SupportsManualRetry,
    Guid? EnrolledBy,
    DateTime UpdatedAt);

internal sealed record MaintenanceKioskInfo(Guid Id, string Status, DateTime? LastHeartbeatAt, Guid SellerId);
internal sealed record MaintenanceSlotInfo(
    Guid Id, string Status, string? Zone, int RelayChannel, Guid CurrentSellerId,
    Guid? BouquetId, Guid? HoldOrderId, DateTime? HoldUntil, long RowVersion);
internal sealed record MaintenanceActorInfo(Guid Id, string Status, string Role, Guid SellerId);
internal sealed record MaintenanceSellerInfo(Guid Id, string Status, bool IsPackageValid)
{
    public bool CanMaintain(bool isRemoval) => Status == "ACTIVE" || (isRemoval && Status == "PAST_DUE");
}
internal sealed record MaintenanceCapabilityInfo(
    bool Exists, bool Enabled, bool DoorVerified, bool PresenceVerified, bool SupportsManualRetry,
    Guid? KioskId = null, int? RelayChannel = null)
{
    public bool IsFullyVerifiedFor(Guid kioskId, int relay) =>
        Exists && Enabled && DoorVerified && PresenceVerified && KioskId == kioskId && RelayChannel == relay;
}

internal sealed record MaintenanceSessionRow(
    Guid Id, Guid KioskId, Guid SlotId, Guid SellerId, Guid ActorId,
    string Action, string Stage, string IntendedStage, string? Substate,
    string PreviousSlotStatus, Guid? OldBouquetId, Guid? NewProductId, Guid? NewBouquetId,
    string Reason, DateTime PhaseStartedAt, DateTime ExpiresAt, Guid? CurrentCmdId,
    DateTime? LatestInspectionAt, long? LatestInspectionObservedAt, string? LatestInspectionReason,
    int InspectionOpenCount, DateTime? LastInspectionOpenAt,
    long RowVersion);

internal sealed record MaintenanceLockedContext(
    MaintenanceKioskInfo Kiosk,
    MaintenanceSlotInfo Slot,
    MaintenanceActorInfo Actor,
    MaintenanceSellerInfo Seller,
    bool AssignmentActive,
    MaintenanceCapabilityInfo Capability,
    MaintenanceSessionRow? Session);

public sealed class SlotMaintenanceService(NpgsqlDataSource data, ICapPublisher publisher)
{
    private static IReadOnlyList<string> ComputeAvailableActions(
        string stage, string action, string? substate, bool canOpenForCheck = false) =>
        stage switch
        {
            "Completed" => [],
            "NeedsInspection" => canOpenForCheck
                ? ["open-for-check", "check", "report-issue"]
                : ["check", "report-issue"],
            "AwaitRemoval" => ["check", "report-issue"],
            "AwaitLoading" => action == "REPLACE" && substate == "RemovalClosedEmptyConfirmed"
                ? ["open-next", "check", "report-issue"]
                : ["check", "report-issue"],
            _ => ["check", "report-issue"]
        };

    private static SlotMaintenanceSessionResponse ToResponse(MaintenanceSessionRow s, bool canOpenForCheck = false) =>
        new(
            s.Id, s.KioskId, s.SlotId, s.Action, s.Stage, s.Substate, s.CurrentCmdId,
            new DateTimeOffset(s.PhaseStartedAt, TimeSpan.Zero),
            new DateTimeOffset(s.ExpiresAt, TimeSpan.Zero),
            s.LatestInspectionReason,
            s.LatestInspectionAt.HasValue ? new DateTimeOffset(s.LatestInspectionAt.Value, TimeSpan.Zero) : null,
            s.OldBouquetId, s.NewProductId, s.NewBouquetId,
            ComputeAvailableActions(s.Stage, s.Action, s.Substate, canOpenForCheck));

    // Uniform lock order: Kiosk SHARE -> Slot UPDATE -> Session FOR UPDATE -> Actor/Seller/Assignment SHARE
    private static async Task<MaintenanceLockedContext?> LoadAndLockContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid kioskId,
        Guid slotId,
        Guid sellerId,
        Guid? actorId,
        Guid? sessionId,
        CancellationToken ct)
    {
        await using var timeoutCmd = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", connection, transaction);
        await timeoutCmd.ExecuteNonQueryAsync(ct);

        // 1. Kiosk SHARE
        MaintenanceKioskInfo kiosk;
        await using (var kioskCmd = new NpgsqlCommand("""
            SELECT id, status, seller_id, last_heartbeat_at FROM kiosk_ops.kiosks WHERE id = @kiosk FOR SHARE
            """, connection, transaction))
        {
            kioskCmd.Parameters.AddWithValue("kiosk", kioskId);
            await using var reader = await kioskCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var owner = reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2);
            if (owner != sellerId) return null;
            kiosk = new(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                owner);
        }

        // 2. Slot UPDATE
        MaintenanceSlotInfo slot;
        await using (var slotCmd = new NpgsqlCommand("""
            SELECT id, status, zone, relay_channel, current_seller_id, bouquet_id, hold_order_id, hold_until, row_version
            FROM kiosk_ops.slots WHERE id = @slot AND kiosk_id = @kiosk FOR UPDATE
            """, connection, transaction))
        {
            slotCmd.Parameters.AddWithValue("slot", slotId);
            slotCmd.Parameters.AddWithValue("kiosk", kioskId);
            await using var reader = await slotCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var slotOwner = reader.IsDBNull(4) ? Guid.Empty : reader.GetGuid(4);
            if (slotOwner != sellerId) return null;
            slot = new(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                Convert.ToInt32(reader.GetValue(3)),
                slotOwner,
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                Convert.ToInt64(reader.GetValue(8)));
        }

        // 3. Session FOR UPDATE (if sessionId specified)
        MaintenanceSessionRow? session = null;
        if (sessionId.HasValue)
        {
            await using var sessionCmd = new NpgsqlCommand("""
                SELECT id, kiosk_id, slot_id, seller_id, actor_id, action, stage, intended_stage, substate,
                       previous_slot_status, old_bouquet_id, new_product_id, new_bouquet_id, reason,
                       phase_started_at, expires_at, current_cmd_id, latest_inspection_at,
                       latest_inspection_observed_at, latest_inspection_reason, inspection_open_count,
                       last_inspection_open_at, row_version
                FROM kiosk_ops.slot_maintenance_sessions
                WHERE id = @id AND kiosk_id = @kiosk AND slot_id = @slot
                FOR UPDATE
                """, connection, transaction);
            sessionCmd.Parameters.AddWithValue("id", sessionId.Value);
            sessionCmd.Parameters.AddWithValue("kiosk", kioskId);
            sessionCmd.Parameters.AddWithValue("slot", slotId);
            await using var reader = await sessionCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var sessSeller = reader.GetGuid(3);
            if (sessSeller != sellerId) return null;

            session = new(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), sessSeller, reader.GetGuid(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10),
                reader.IsDBNull(11) ? null : reader.GetGuid(11),
                reader.IsDBNull(12) ? null : reader.GetGuid(12),
                reader.GetString(13), reader.GetDateTime(14), reader.GetDateTime(15),
                reader.IsDBNull(16) ? null : reader.GetGuid(16),
                reader.IsDBNull(17) ? null : reader.GetDateTime(17),
                reader.IsDBNull(18) ? null : Convert.ToInt64(reader.GetValue(18)),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                Convert.ToInt32(reader.GetValue(20)),
                reader.IsDBNull(21) ? null : reader.GetDateTime(21),
                Convert.ToInt64(reader.GetValue(22)));
        }

        var effectiveActor = actorId ?? session?.ActorId;
        if (!effectiveActor.HasValue) return null;

        // 4. Actor, Seller & Assignment SHARE
        MaintenanceActorInfo actor;
        MaintenanceSellerInfo seller;
        bool assignmentActive;
        await using (var authCmd = new NpgsqlCommand("""
            SELECT actor_id, actor_status, actor_role, actor_seller_id, seller_status,
                   package_active, assignment_active
            FROM flow.seller_slot_maintenance_auth(@actor, @seller, @slot)
            """, connection, transaction))
        {
            authCmd.Parameters.AddWithValue("actor", effectiveActor.Value);
            authCmd.Parameters.AddWithValue("seller", sellerId);
            authCmd.Parameters.AddWithValue("slot", slotId);
            await using var reader = await authCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            actor = new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3));
            seller = new(sellerId, reader.GetString(4), reader.GetBoolean(5));
            assignmentActive = reader.GetBoolean(6);
        }

        // 5. Capability registration with mapping attestation
        MaintenanceCapabilityInfo cap = new(false, false, false, false, false);
        await using (var capCmd = new NpgsqlCommand("""
            SELECT enabled, door_verified, presence_verified, supports_manual_retry, kiosk_id, relay_channel
            FROM kiosk_ops.slot_sensor_capabilities WHERE slot_id = @slot FOR SHARE
            """, connection, transaction))
        {
            capCmd.Parameters.AddWithValue("slot", slotId);
            await using var reader = await capCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                cap = new(
                    true,
                    reader.GetBoolean(0),
                    reader.GetBoolean(1),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3),
                    reader.GetGuid(4),
                    Convert.ToInt32(reader.GetValue(5)));
            }
        }

        return new(kiosk, slot, actor, seller, assignmentActive, cap, session);
    }

    public async Task<IResult> StartSessionAsync(
        Guid kioskId, Guid slotId, Guid actorId, Guid sellerId,
        StartSlotMaintenanceRequest input, CancellationToken ct)
    {
        if (input.Id == Guid.Empty)
            return Results.Problem(statusCode: 400, detail: "Cần Id hợp lệ cho phiên bảo trì.");

        if (string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length is < 5 or > 500)
            return Results.Problem(statusCode: 400, detail: "Lý do bảo trì phải từ 5 đến 500 ký tự.");

        if (!Enum.TryParse<MaintenanceAction>(input.Action, true, out var action) || !Enum.IsDefined(action))
            return Results.Problem(statusCode: 400, detail: "Loại hành động bảo trì không hợp lệ. Chỉ chấp nhận LOAD, REMOVE hoặc REPLACE.");

        var actionUpper = action.ToString().ToUpperInvariant();
        var reasonTrim = input.Reason.Trim();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;

        try
        {
            // Lock kiosk -> slot -> actor/seller/assignment in uniform order
            var auth = await LoadAndLockContextAsync(connection, transaction, kioskId, slotId, sellerId, actorId, sessionId: null, ct);
            if (auth is null) return Results.NotFound();

            var isShopAuthorized = auth.Seller.CanMaintain(action == MaintenanceAction.Remove);

            if (auth.Actor.Status != "ACTIVE" || auth.Actor.Role != "SELLER" || !isShopAuthorized)
                return Results.Forbid();

            if (!auth.AssignmentActive)
                return Results.Problem(statusCode: 409, detail: "Ô không còn được phân công hoạt động cho shop.");

            if (auth.Kiosk.Status != "ONLINE")
                return Results.Problem(statusCode: 409, detail: "Kiosk hiện đang không trực tuyến.");

            if (auth.Slot.Zone != "WALK_IN")
                return Results.Problem(statusCode: 409, detail: "Quy trình bảo trì tự phục vụ chỉ áp dụng cho ô vùng WALK_IN.");

            // Idempotency recheck under slot lock
            await using (var checkId = new NpgsqlCommand("""
                SELECT id, kiosk_id, slot_id, seller_id, actor_id, action, stage, intended_stage, substate,
                       previous_slot_status, old_bouquet_id, new_product_id, new_bouquet_id, reason,
                       phase_started_at, expires_at, current_cmd_id, latest_inspection_at,
                       latest_inspection_observed_at, latest_inspection_reason, inspection_open_count,
                       last_inspection_open_at, row_version
                FROM kiosk_ops.slot_maintenance_sessions
                WHERE id = @id FOR SHARE
                """, connection, transaction))
            {
                checkId.Parameters.AddWithValue("id", input.Id);
                await using var reader = await checkId.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var existingSeller = reader.GetGuid(3);
                    var existingKiosk = reader.GetGuid(1);
                    var existingSlot = reader.GetGuid(2);
                    var existingActor = reader.GetGuid(4);

                    if (existingSeller != sellerId || existingKiosk != kioskId || existingSlot != slotId || existingActor != actorId)
                        return Results.NotFound();

                    var existingAction = reader.GetString(5);
                    var existingOldBouquet = reader.IsDBNull(10) ? (Guid?)null : reader.GetGuid(10);
                    var existingNewProduct = reader.IsDBNull(11) ? (Guid?)null : reader.GetGuid(11);
                    var existingReason = reader.GetString(13);

                    if (existingAction != actionUpper || existingOldBouquet != input.ExpectedBouquetId ||
                        existingNewProduct != input.ProductId || existingReason != reasonTrim)
                    {
                        return Results.Problem(statusCode: 409, detail: "Mã phiên bảo trì (Id) đã được sử dụng với thông tin khác.");
                    }

                    var row = new MaintenanceSessionRow(
                        reader.GetGuid(0), existingKiosk, existingSlot, existingSeller, existingActor,
                        existingAction, reader.GetString(6), reader.GetString(7),
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        reader.GetString(9), existingOldBouquet, existingNewProduct,
                        reader.IsDBNull(12) ? null : reader.GetGuid(12),
                        existingReason, reader.GetDateTime(14), reader.GetDateTime(15),
                        reader.IsDBNull(16) ? null : reader.GetGuid(16),
                        reader.IsDBNull(17) ? null : reader.GetDateTime(17),
                        reader.IsDBNull(18) ? null : Convert.ToInt64(reader.GetValue(18)),
                        reader.IsDBNull(19) ? null : reader.GetString(19),
                        Convert.ToInt32(reader.GetValue(20)),
                        reader.IsDBNull(21) ? null : reader.GetDateTime(21),
                        Convert.ToInt64(reader.GetValue(22)));

                    return Results.Ok(ToResponse(row));
                }
            }

            if (!auth.Capability.IsFullyVerifiedFor(auth.Kiosk.Id, auth.Slot.RelayChannel))
                return Results.Problem(statusCode: 409, detail: "Ô chưa được đăng ký hoặc kích hoạt đầy đủ năng lực cảm biến cửa và hoa.");

            // Check no active maintenance session on slot
            await using (var activeSessionCheck = new NpgsqlCommand("""
                SELECT 1 FROM kiosk_ops.slot_maintenance_sessions WHERE slot_id = @slot AND stage <> 'Completed'
                """, connection, transaction))
            {
                activeSessionCheck.Parameters.AddWithValue("slot", slotId);
                if (await activeSessionCheck.ExecuteScalarAsync(ct) is not null)
                    return Results.Problem(statusCode: 409, detail: "Ô đang có một phiên bảo trì khác chưa hoàn tất.");
            }

            // Check active hold on slot
            if (auth.Slot.HoldOrderId is not null && (auth.Slot.HoldUntil is null || auth.Slot.HoldUntil > now.UtcDateTime))
                return Results.Problem(statusCode: 409, detail: "Ô đang được giữ cho đơn hàng khác.");

            // Check active customer orders: scope strictly to current bouquet for Remove/Replace; historical completed sales do not block Load
            if (action is MaintenanceAction.Remove or MaintenanceAction.Replace && auth.Slot.BouquetId is { } bId)
            {
                await using var orderCheck = new NpgsqlCommand("SELECT 1 WHERE flow.check_bouquet_active_order(@bouquet) IS NOT NULL", connection, transaction);
                orderCheck.Parameters.AddWithValue("bouquet", bId);
                if (await orderCheck.ExecuteScalarAsync(ct) is not null)
                    return Results.Problem(statusCode: 409, detail: "Bó hoa đang gắn với đơn hàng của khách cần xử lý.");
            }

            // Check active unlock tokens on slot
            await using (var tokenCheck = new NpgsqlCommand("""
                SELECT 1 FROM kiosk_ops.unlock_tokens
                WHERE slot_id = @slot
                  AND (status = 'OPENED' OR (door_opened_at IS NOT NULL AND door_closed_at IS NULL)
                       OR (status IN ('ISSUED','SENT','ACKED') AND expires_at > CURRENT_TIMESTAMP))
                """, connection, transaction))
            {
                tokenCheck.Parameters.AddWithValue("slot", slotId);
                if (await tokenCheck.ExecuteScalarAsync(ct) is not null)
                    return Results.Problem(statusCode: 409, detail: "Cửa ô đang mở hoặc có lệnh mở còn hiệu lực.");
            }

            // Check sellable lease
            bool isLeasePaid;
            await using (var leaseCmd = new NpgsqlCommand("SELECT flow.slot_sellable(@slot, @seller)", connection, transaction))
            {
                leaseCmd.Parameters.AddWithValue("slot", slotId);
                leaseCmd.Parameters.AddWithValue("seller", sellerId);
                isLeasePaid = (bool)(await leaseCmd.ExecuteScalarAsync(ct) ?? false);
            }

            // Check product active and owned
            bool productActive = false;
            bool productOwned = false;
            if (input.ProductId is { } prodId && prodId != Guid.Empty)
            {
                await using var prodCmd = new NpgsqlCommand("""
                    SELECT is_active, is_owned
                    FROM flow.seller_flower_product_check(@product, @seller)
                    """, connection, transaction);
                prodCmd.Parameters.AddWithValue("product", prodId);
                prodCmd.Parameters.AddWithValue("seller", sellerId);
                await using var reader = await prodCmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    productActive = reader.GetBoolean(0);
                    productOwned = reader.GetBoolean(1);
                }
            }

            // For LOAD: Fresh closed-empty baseline is required before opening door, using full pure telemetry verification
            if (action == MaintenanceAction.Load)
            {
                await using var presenceCmd = new NpgsqlCommand("""
                    SELECT observed_at, received_at, states FROM kiosk_ops.flower_presence WHERE kiosk_id = @kiosk
                    """, connection, transaction);
                presenceCmd.Parameters.AddWithValue("kiosk", kioskId);
                await using var reader = await presenceCmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return Results.Problem(statusCode: 409, detail: "Chưa có dữ liệu cảm biến hoa từ kiosk để xác nhận ô trống.");

                var obsAt = Convert.ToInt64(reader.GetValue(0));
                var recAt = reader.GetDateTime(1);
                var states = reader.GetFieldValue<int[]>(2);

                var obsDto = DateTimeOffset.FromUnixTimeMilliseconds(obsAt);
                var recDto = new DateTimeOffset(recAt, TimeSpan.Zero);
                var baselineObs = new SensorObservation(obsDto, recDto, states, true, true);

                if (!SlotMaintenancePolicy.VerifyTelemetryFreshness(baselineObs, auth.Slot.RelayChannel, now, out var telDenial))
                    return Results.Problem(statusCode: 409, detail: "Tín hiệu cảm biến chưa đạt chuẩn độ mới hoặc bị lỗi: " + telDenial);

                if (states[auth.Slot.RelayChannel] != 0)
                    return Results.Problem(statusCode: 409, detail: "Cần tín hiệu cảm biến xác nhận ô đang trống và ổn định trước khi mở nạp.");
            }

            var context = new SlotMaintenanceContext(
                KioskId: kioskId,
                SlotId: slotId,
                SellerId: sellerId,
                ActorId: actorId,
                ActorRole: auth.Actor.Role,
                KioskSellerId: auth.Kiosk.SellerId,
                SlotCurrentSellerId: auth.Slot.CurrentSellerId,
                HasActiveAssignment: auth.AssignmentActive,
                Zone: auth.Slot.Zone,
                SlotStatus: auth.Slot.Status,
                BouquetId: auth.Slot.BouquetId,
                ProductId: input.ProductId,
                IsProductActive: productActive,
                IsLeasePaid: isLeasePaid,
                HasActiveCustomerOrder: false,
                HasActiveHold: false,
                HasActiveDoorCommand: false,
                KioskOnline: auth.Kiosk.Status == "ONLINE",
                LastHeartbeatAt: auth.Kiosk.LastHeartbeatAt.HasValue ? new DateTimeOffset(auth.Kiosk.LastHeartbeatAt.Value, TimeSpan.Zero) : null,
                RelayChannel: auth.Slot.RelayChannel,
                SensorCapabilityEnabled: auth.Capability.Enabled,
                SensorInstalled: auth.Capability.DoorVerified && auth.Capability.PresenceVerified,
                SupportsManualRetry: auth.Capability.SupportsManualRetry,
                IsActorActive: auth.Actor.Status == "ACTIVE",
                IsShopActive: isShopAuthorized,
                IsProductOwnedBySeller: productOwned,
                RowVersion: auth.Slot.RowVersion);

            var valResult = SlotMaintenancePolicy.ValidateInitialSession(context, action, now);
            if (!valResult.Allowed)
                return Results.Problem(statusCode: 409, detail: valResult.DenialReason ?? "Không thỏa mãn điều kiện bắt đầu bảo trì ô.");

            // Lock slot using existing LOCKED state
            await using (var lockSlot = new NpgsqlCommand("""
                UPDATE kiosk_ops.slots
                SET status = 'LOCKED', row_version = row_version + 1, updated_at = CURRENT_TIMESTAMP
                WHERE id = @slot
                """, connection, transaction))
            {
                lockSlot.Parameters.AddWithValue("slot", slotId);
                await lockSlot.ExecuteNonQueryAsync(ct);
            }

            // Issue initial SELLER_ACCESS unlock token
            Guid cmdId;
            await using (var issueToken = new NpgsqlCommand("""
                INSERT INTO kiosk_ops.unlock_tokens (kiosk_id, slot_id, purpose, issued_to_user_id, token_hash, issued_at, expires_at)
                VALUES (@kiosk, @slot, 'SELLER_ACCESS', @actor,
                        encode(sha256(convert_to(public.uuid_v7()::text, 'UTF8')), 'hex'),
                        CURRENT_TIMESTAMP, CURRENT_TIMESTAMP + flow.cfg_min('token_minutes'))
                RETURNING cmd_id
                """, connection, transaction))
            {
                issueToken.Parameters.AddWithValue("kiosk", kioskId);
                issueToken.Parameters.AddWithValue("slot", slotId);
                issueToken.Parameters.AddWithValue("actor", actorId);
                cmdId = (Guid)(await issueToken.ExecuteScalarAsync(ct))!;
            }

            var stageStr = valResult.InitialStage!.Value.ToString();
            var expiresAt15 = now.AddMinutes(15);

            await using (var insertSession = new NpgsqlCommand("""
                INSERT INTO kiosk_ops.slot_maintenance_sessions(
                    id, kiosk_id, slot_id, seller_id, actor_id, action, stage, intended_stage, substate,
                    previous_slot_status, old_bouquet_id, new_product_id, reason, phase_started_at,
                    expires_at, current_cmd_id, inspection_open_count, row_version
                ) VALUES (
                    @id, @kiosk, @slot, @seller, @actor, @action, @stage, @intendedStage, 'Initial',
                    @previousStatus, @oldBouquet, @newProduct, @reason, CURRENT_TIMESTAMP,
                    @expiresAt, @cmdId, 0, 1
                )
                """, connection, transaction))
            {
                insertSession.Parameters.AddWithValue("id", input.Id);
                insertSession.Parameters.AddWithValue("kiosk", kioskId);
                insertSession.Parameters.AddWithValue("slot", slotId);
                insertSession.Parameters.AddWithValue("seller", sellerId);
                insertSession.Parameters.AddWithValue("actor", actorId);
                insertSession.Parameters.AddWithValue("action", actionUpper);
                insertSession.Parameters.AddWithValue("stage", stageStr);
                insertSession.Parameters.AddWithValue("intendedStage", stageStr);
                insertSession.Parameters.AddWithValue("previousStatus", auth.Slot.Status);
                insertSession.Parameters.AddWithValue("oldBouquet", NpgsqlDbType.Uuid, (object?)auth.Slot.BouquetId ?? DBNull.Value);
                insertSession.Parameters.AddWithValue("newProduct", NpgsqlDbType.Uuid, (object?)input.ProductId ?? DBNull.Value);
                insertSession.Parameters.AddWithValue("reason", reasonTrim);
                insertSession.Parameters.AddWithValue("expiresAt", expiresAt15.UtcDateTime);
                insertSession.Parameters.AddWithValue("cmdId", cmdId);
                await insertSession.ExecuteNonQueryAsync(ct);
            }

            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            await using (var audit = new NpgsqlCommand("""
                SELECT flow.audit(@actor, 'SLOT_MAINTENANCE_STARTED', 'slots', @slot,
                    jsonb_build_object('sessionId', @id, 'action', @action, 'cmdId', @cmdId, 'previousStatus', @previousStatus));
                """, connection, transaction))
            {
                audit.Parameters.AddWithValue("actor", actorId);
                audit.Parameters.AddWithValue("slot", slotId);
                audit.Parameters.AddWithValue("id", input.Id);
                audit.Parameters.AddWithValue("action", actionUpper);
                audit.Parameters.AddWithValue("cmdId", cmdId);
                audit.Parameters.AddWithValue("previousStatus", auth.Slot.Status);
                await audit.ExecuteNonQueryAsync(ct);
            }

            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);

            var createdRow = new MaintenanceSessionRow(
                input.Id, kioskId, slotId, sellerId, actorId, actionUpper, stageStr, stageStr, "Initial",
                auth.Slot.Status, auth.Slot.BouquetId, input.ProductId, null, reasonTrim,
                now.UtcDateTime, expiresAt15.UtcDateTime, cmdId, null, null, null, 0, null, 1);

            return Results.Ok(ToResponse(createdRow));
        }
        catch (PostgresException ex) when (ex.SqlState is "55P03" or "40P01")
        {
            return Results.Problem(statusCode: 409, detail: "Ô đang được xử lý đồng thời. Hãy tải lại trạng thái trước khi thử lại.");
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return await GetSessionAsync(kioskId, slotId, input.Id, sellerId, ct);
        }
    }

    public async Task<IResult> GetSessionAsync(
        Guid kioskId, Guid slotId, Guid sessionId, Guid sellerId, CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT id, kiosk_id, slot_id, seller_id, actor_id, action, stage, intended_stage, substate,
                   previous_slot_status, old_bouquet_id, new_product_id, new_bouquet_id, reason,
                   phase_started_at, expires_at, current_cmd_id, latest_inspection_at,
                   latest_inspection_observed_at, latest_inspection_reason, inspection_open_count,
                   last_inspection_open_at, row_version
            FROM kiosk_ops.slot_maintenance_sessions
            WHERE id = @id AND kiosk_id = @kiosk AND slot_id = @slot
            """, connection);
        cmd.Parameters.AddWithValue("id", sessionId);
        cmd.Parameters.AddWithValue("kiosk", kioskId);
        cmd.Parameters.AddWithValue("slot", slotId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return Results.NotFound();
        if (reader.GetGuid(3) != sellerId) return Results.NotFound();

        var row = new MaintenanceSessionRow(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10),
            reader.IsDBNull(11) ? null : reader.GetGuid(11),
            reader.IsDBNull(12) ? null : reader.GetGuid(12),
            reader.GetString(13), reader.GetDateTime(14), reader.GetDateTime(15),
            reader.IsDBNull(16) ? null : reader.GetGuid(16),
            reader.IsDBNull(17) ? null : reader.GetDateTime(17),
            reader.IsDBNull(18) ? null : Convert.ToInt64(reader.GetValue(18)),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            Convert.ToInt32(reader.GetValue(20)),
            reader.IsDBNull(21) ? null : reader.GetDateTime(21),
            Convert.ToInt64(reader.GetValue(22)));

        bool canOpenForCheck = row.Stage == "NeedsInspection" && row.InspectionOpenCount < 3 && row.ExpiresAt > DateTime.UtcNow && row.CurrentCmdId.HasValue;
        return Results.Ok(ToResponse(row, canOpenForCheck));
    }

    public async Task<IResult> CheckSessionAsync(
        Guid kioskId, Guid slotId, Guid sessionId, Guid sellerId, CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;

        try
        {
            var auth = await LoadAndLockContextAsync(connection, transaction, kioskId, slotId, sellerId, actorId: null, sessionId: sessionId, ct);
            if (auth is null || auth.Session is null) return Results.NotFound();

            var result = await SyncSessionInternalAsync(connection, transaction, auth, ct);
            await outbox.CommitAsync(ct);
            return Results.Ok(result);
        }
        catch (PostgresException ex) when (ex.SqlState is "55P03" or "40P01")
        {
            return Results.Problem(statusCode: 409, detail: "Ô đang được xử lý đồng thời. Hãy tải lại trạng thái trước khi thử lại.");
        }
    }

    public async Task<IResult> OpenNextAsync(
        Guid kioskId, Guid slotId, Guid sessionId, Guid actorId, Guid sellerId, CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;

        try
        {
            var auth = await LoadAndLockContextAsync(connection, transaction, kioskId, slotId, sellerId, actorId, sessionId, ct);
            if (auth is null || auth.Session is null) return Results.NotFound();

            var s = auth.Session;
            if (s.Action != "REPLACE" || s.Stage != "AwaitLoading" || s.Substate != "RemovalClosedEmptyConfirmed")
                return Results.Problem(statusCode: 409, detail: "Phiên không ở trạng thái sẵn sàng mở cửa để nạp hoa mới.");

            var now = DateTimeOffset.UtcNow;
            if (s.ExpiresAt <= now.UtcDateTime)
                return Results.Problem(statusCode: 409, detail: "Phiên bảo trì đã hết hạn. Vui lòng kiểm tra tủ.");

            if (auth.Actor.Status != "ACTIVE" || auth.Actor.Role != "SELLER" || auth.Seller.Status != "ACTIVE" || !auth.AssignmentActive)
                return Results.Forbid();

            // Recheck kiosk online & fresh heartbeat (fail-closed immediately)
            if (auth.Kiosk.Status != "ONLINE" || !auth.Kiosk.LastHeartbeatAt.HasValue ||
                now.UtcDateTime - auth.Kiosk.LastHeartbeatAt.Value > TimeSpan.FromSeconds(SlotMaintenancePolicy.TelemetryFreshnessSeconds))
                return Results.Problem(statusCode: 409, detail: "Kiosk đang offline hoặc mất tín hiệu.");

            // Recheck capability registration and mapping
            if (!auth.Capability.IsFullyVerifiedFor(auth.Kiosk.Id, auth.Slot.RelayChannel))
                return Results.Problem(statusCode: 409, detail: "Năng lực cảm biến ô không hợp lệ hoặc bị thay đổi cấu hình.");

            // Recheck slot status LOCKED, no holds, no active orders
            if (auth.Slot.Status != "LOCKED" || auth.Slot.HoldOrderId is not null ||
                (auth.Slot.HoldUntil is not null && auth.Slot.HoldUntil > now.UtcDateTime))
                return Results.Problem(statusCode: 409, detail: "Trạng thái ô không cho phép mở nạp hoa.");

            if (auth.Slot.BouquetId is { } currentBq)
            {
                await using var orderCheck = new NpgsqlCommand("SELECT 1 WHERE flow.check_bouquet_active_order(@bouquet) IS NOT NULL", connection, transaction);
                orderCheck.Parameters.AddWithValue("bouquet", currentBq);
                if (await orderCheck.ExecuteScalarAsync(ct) is not null)
                    return Results.Problem(statusCode: 409, detail: "Ô đang gắn với đơn hàng của khách cần xử lý.");
            }

            // Recheck sellable lease
            await using (var leaseCmd = new NpgsqlCommand("SELECT flow.slot_sellable(@slot, @seller)", connection, transaction))
            {
                leaseCmd.Parameters.AddWithValue("slot", slotId);
                leaseCmd.Parameters.AddWithValue("seller", sellerId);
                if (!((bool)(await leaseCmd.ExecuteScalarAsync(ct) ?? false)))
                    return Results.Problem(statusCode: 409, detail: "Gói thuê của seller đã hết hạn, không được mở nạp hoa.");
            }

            // Recheck valid active product owned by seller
            if (s.NewProductId is { } prodId)
            {
                await using var prodCmd = new NpgsqlCommand("SELECT 1 WHERE flow.check_seller_active_product(@product, @seller) IS NOT NULL", connection, transaction);
                prodCmd.Parameters.AddWithValue("product", prodId);
                prodCmd.Parameters.AddWithValue("seller", sellerId);
                if (await prodCmd.ExecuteScalarAsync(ct) is null)
                    return Results.Problem(statusCode: 409, detail: "Sản phẩm nạp không hợp lệ hoặc không thuộc shop.");
            }

            // Issue new SELLER_ACCESS token for loading phase
            Guid newCmdId;
            await using (var issueToken = new NpgsqlCommand("""
                INSERT INTO kiosk_ops.unlock_tokens (kiosk_id, slot_id, purpose, issued_to_user_id, token_hash, issued_at, expires_at)
                VALUES (@kiosk, @slot, 'SELLER_ACCESS', @actor,
                        encode(sha256(convert_to(public.uuid_v7()::text, 'UTF8')), 'hex'),
                        CURRENT_TIMESTAMP, CURRENT_TIMESTAMP + flow.cfg_min('token_minutes'))
                RETURNING cmd_id
                """, connection, transaction))
            {
                issueToken.Parameters.AddWithValue("kiosk", kioskId);
                issueToken.Parameters.AddWithValue("slot", slotId);
                issueToken.Parameters.AddWithValue("actor", actorId);
                newCmdId = (Guid)(await issueToken.ExecuteScalarAsync(ct))!;
            }

            await using (var updateSession = new NpgsqlCommand("""
                UPDATE kiosk_ops.slot_maintenance_sessions
                SET current_cmd_id = @cmdId, phase_started_at = CURRENT_TIMESTAMP, substate = 'Initial',
                    row_version = row_version + 1, updated_at = CURRENT_TIMESTAMP
                WHERE id = @id
                """, connection, transaction))
            {
                updateSession.Parameters.AddWithValue("cmdId", newCmdId);
                updateSession.Parameters.AddWithValue("id", sessionId);
                await updateSession.ExecuteNonQueryAsync(ct);
            }

            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            await using (var audit = new NpgsqlCommand("""
                SELECT flow.audit(@actor, 'SLOT_MAINTENANCE_OPEN_NEXT_ISSUED', 'slots', @slot,
                    jsonb_build_object('sessionId', @id, 'cmdId', @cmdId));
                """, connection, transaction))
            {
                audit.Parameters.AddWithValue("actor", actorId);
                audit.Parameters.AddWithValue("slot", slotId);
                audit.Parameters.AddWithValue("id", sessionId);
                audit.Parameters.AddWithValue("cmdId", newCmdId);
                await audit.ExecuteNonQueryAsync(ct);
            }

            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);

            var updatedRow = s with
            {
                CurrentCmdId = newCmdId,
                PhaseStartedAt = now.UtcDateTime,
                Substate = "Initial",
                RowVersion = s.RowVersion + 1
            };

            return Results.Ok(ToResponse(updatedRow));
        }
        catch (PostgresException ex) when (ex.SqlState is "55P03" or "40P01")
        {
            return Results.Problem(statusCode: 409, detail: "Ô đang được xử lý đồng thời. Hãy tải lại trạng thái trước khi thử lại.");
        }
    }

    public async Task<IResult> OpenForCheckAsync(
        Guid kioskId, Guid slotId, Guid sessionId, Guid actorId, Guid sellerId,
        OpenForCheckRequest input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length is < 5 or > 500)
            return Results.Problem(statusCode: 400, detail: "Lý do mở kiểm tra phải từ 5 đến 500 ký tự.");

        var reasonTrim = input.Reason.Trim();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;

        try
        {
            var auth = await LoadAndLockContextAsync(connection, transaction, kioskId, slotId, sellerId, actorId, sessionId, ct);
            if (auth is null || auth.Session is null) return Results.NotFound();

            var s = auth.Session;
            if (s.Stage != "NeedsInspection")
                return Results.Problem(statusCode: 409, detail: "Chỉ phiên đang ở trạng thái NeedsInspection mới được mở kiểm tra.");

            if (s.ExpiresAt <= now.UtcDateTime)
                return Results.Problem(statusCode: 409, detail: "Phiên bảo trì đã hết hạn. Vui lòng liên hệ hỗ trợ kỹ thuật.");

            var isShopAuthorized = auth.Seller.CanMaintain(s.Action == "REMOVE");

            if (auth.Actor.Status != "ACTIVE" || auth.Actor.Role != "SELLER" || !isShopAuthorized || !auth.AssignmentActive)
                return Results.Forbid();

            // Recheck kiosk online & fresh heartbeat
            if (auth.Kiosk.Status != "ONLINE" || !auth.Kiosk.LastHeartbeatAt.HasValue ||
                now.UtcDateTime - auth.Kiosk.LastHeartbeatAt.Value > TimeSpan.FromSeconds(SlotMaintenancePolicy.TelemetryFreshnessSeconds))
                return Results.Problem(statusCode: 409, detail: "Kiosk đang offline hoặc mất tín hiệu.");

            // Recheck capability registration and mapping
            if (!auth.Capability.IsFullyVerifiedFor(auth.Kiosk.Id, auth.Slot.RelayChannel))
                return Results.Problem(statusCode: 409, detail: "Năng lực cảm biến ô không hợp lệ hoặc bị thay đổi cấu hình.");

            // Slot must be LOCKED, zone WALK_IN, no holds, no active orders
            if (auth.Slot.Status != "LOCKED" || auth.Slot.HoldOrderId is not null ||
                (auth.Slot.HoldUntil is not null && auth.Slot.HoldUntil > now.UtcDateTime))
                return Results.Problem(statusCode: 409, detail: "Trạng thái ô không cho phép mở kiểm tra.");

            if (auth.Slot.BouquetId is { } currentBq)
            {
                await using var orderCheck = new NpgsqlCommand("SELECT 1 WHERE flow.check_bouquet_active_order(@bouquet) IS NOT NULL", connection, transaction);
                orderCheck.Parameters.AddWithValue("bouquet", currentBq);
                if (await orderCheck.ExecuteScalarAsync(ct) is not null)
                    return Results.Problem(statusCode: 409, detail: "Ô đang gắn với đơn hàng của khách cần xử lý.");
            }

            // Max 3 inspection opens per session
            if (s.InspectionOpenCount >= 3)
                return Results.Problem(statusCode: 409, detail: "Đã vượt quá số lần mở kiểm tra tối đa (3 lần) cho phiên này.");

            // Cooldown of >= 30 seconds between inspection opens
            if (s.LastInspectionOpenAt.HasValue && now.UtcDateTime < s.LastInspectionOpenAt.Value.AddSeconds(30))
                return Results.Problem(statusCode: 409, detail: "Cần đợi ít nhất 30 giây giữa các lần mở kiểm tra.");

            // Prior command must be genuinely CLOSED!
            if (!s.CurrentCmdId.HasValue)
                return Results.Problem(statusCode: 409, detail: "Chưa có lệnh mở cửa nào trước đó để mở kiểm tra.");

            await using (var tokenCmd = new NpgsqlCommand("""
                SELECT status, door_opened_at, door_closed_at FROM kiosk_ops.unlock_tokens
                WHERE cmd_id = @cmd AND kiosk_id = @kiosk AND slot_id = @slot
                  AND issued_to_user_id = @actor AND purpose = 'SELLER_ACCESS'
                """, connection, transaction))
            {
                tokenCmd.Parameters.AddWithValue("cmd", s.CurrentCmdId.Value);
                tokenCmd.Parameters.AddWithValue("kiosk", kioskId);
                tokenCmd.Parameters.AddWithValue("slot", slotId);
                tokenCmd.Parameters.AddWithValue("actor", actorId);
                await using var reader = await tokenCmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return Results.Problem(statusCode: 409, detail: "Không tìm thấy lệnh mở cửa trước đó.");

                var tokenStatus = reader.GetString(0);
                var doorOpenedAt = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
                var doorClosedAt = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2);
                if (tokenStatus != "CLOSED" || doorOpenedAt is null || doorClosedAt is null ||
                    doorOpenedAt < s.PhaseStartedAt || doorClosedAt < doorOpenedAt)
                    return Results.Problem(statusCode: 409, detail: "Lệnh mở cửa trước đó chưa đóng hoàn tất; không thể mở kiểm tra.");
            }

            // Stable fresh known 0/1 snapshot required
            await using (var presenceCmd = new NpgsqlCommand("""
                SELECT observed_at, received_at, states FROM kiosk_ops.flower_presence WHERE kiosk_id = @kiosk
                """, connection, transaction))
            {
                presenceCmd.Parameters.AddWithValue("kiosk", kioskId);
                await using var reader = await presenceCmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return Results.Problem(statusCode: 409, detail: "Chưa có dữ liệu cảm biến hoa từ kiosk để xác nhận trạng thái.");

                var obsAt = Convert.ToInt64(reader.GetValue(0));
                var recAt = reader.GetDateTime(1);
                var states = reader.GetFieldValue<int[]>(2);

                var obsDto = DateTimeOffset.FromUnixTimeMilliseconds(obsAt);
                var recDto = new DateTimeOffset(recAt, TimeSpan.Zero);
                var snap = new SensorObservation(obsDto, recDto, states, true, true);

                if (!SlotMaintenancePolicy.VerifyTelemetryFreshness(snap, auth.Slot.RelayChannel, now, out var telDenial))
                    return Results.Problem(statusCode: 409, detail: "Tín hiệu cảm biến chưa đạt chuẩn độ mới hoặc bị lỗi: " + telDenial);

                var stateVal = states[auth.Slot.RelayChannel];
                if (stateVal is not (0 or 1))
                    return Results.Problem(statusCode: 409, detail: "Cần tín hiệu cảm biến ổn định (đã định trạng 0 hoặc 1) trước khi mở kiểm tra.");
            }

            // Issue NEW SELLER_ACCESS unlock command
            Guid newCmdId;
            await using (var issueToken = new NpgsqlCommand("""
                INSERT INTO kiosk_ops.unlock_tokens (kiosk_id, slot_id, purpose, issued_to_user_id, token_hash, issued_at, expires_at)
                VALUES (@kiosk, @slot, 'SELLER_ACCESS', @actor,
                        encode(sha256(convert_to(public.uuid_v7()::text, 'UTF8')), 'hex'),
                        CURRENT_TIMESTAMP, CURRENT_TIMESTAMP + flow.cfg_min('token_minutes'))
                RETURNING cmd_id
                """, connection, transaction))
            {
                issueToken.Parameters.AddWithValue("kiosk", kioskId);
                issueToken.Parameters.AddWithValue("slot", slotId);
                issueToken.Parameters.AddWithValue("actor", actorId);
                newCmdId = (Guid)(await issueToken.ExecuteScalarAsync(ct))!;
            }

            await using (var updateSession = new NpgsqlCommand("""
                UPDATE kiosk_ops.slot_maintenance_sessions
                SET current_cmd_id = @cmdId, phase_started_at = CURRENT_TIMESTAMP, substate = 'Initial',
                    inspection_open_count = inspection_open_count + 1, last_inspection_open_at = CURRENT_TIMESTAMP,
                    row_version = row_version + 1, updated_at = CURRENT_TIMESTAMP
                WHERE id = @id
                """, connection, transaction))
            {
                updateSession.Parameters.AddWithValue("cmdId", newCmdId);
                updateSession.Parameters.AddWithValue("id", sessionId);
                await updateSession.ExecuteNonQueryAsync(ct);
            }

            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            await using (var audit = new NpgsqlCommand("""
                SELECT flow.audit(@actor, 'SLOT_MAINTENANCE_INSPECTION_OPENED', 'slots', @slot,
                    jsonb_build_object('sessionId', @id, 'cmdId', @cmdId, 'reason', @reason, 'count', @count));
                """, connection, transaction))
            {
                audit.Parameters.AddWithValue("actor", actorId);
                audit.Parameters.AddWithValue("slot", slotId);
                audit.Parameters.AddWithValue("id", sessionId);
                audit.Parameters.AddWithValue("cmdId", newCmdId);
                audit.Parameters.AddWithValue("reason", reasonTrim);
                audit.Parameters.AddWithValue("count", s.InspectionOpenCount + 1);
                await audit.ExecuteNonQueryAsync(ct);
            }

            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);

            var updatedRow = s with
            {
                CurrentCmdId = newCmdId,
                PhaseStartedAt = now.UtcDateTime,
                Substate = "Initial",
                InspectionOpenCount = s.InspectionOpenCount + 1,
                LastInspectionOpenAt = now.UtcDateTime,
                RowVersion = s.RowVersion + 1
            };

            return Results.Ok(ToResponse(updatedRow, canOpenForCheck: false));
        }
        catch (PostgresException ex) when (ex.SqlState is "55P03" or "40P01")
        {
            return Results.Problem(statusCode: 409, detail: "Ô đang được xử lý đồng thời. Hãy tải lại trạng thái trước khi thử lại.");
        }
    }

    public async Task<IResult> ReportIssueAsync(
        Guid kioskId, Guid slotId, Guid sessionId, Guid actorId, Guid sellerId,
        string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 5 or > 500)
            return Results.Problem(statusCode: 400, detail: "Lý do báo lỗi phải từ 5 đến 500 ký tự.");

        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;

        try
        {
            var auth = await LoadAndLockContextAsync(connection, transaction, kioskId, slotId, sellerId, actorId, sessionId, ct);
            if (auth is null || auth.Session is null) return Results.NotFound();

            var s = auth.Session;
            if (s.Stage == "Completed")
                return Results.Problem(statusCode: 409, detail: "Phiên bảo trì đã hoàn tất, không thể báo lỗi.");

            var reasonTrim = reason.Trim();
            var now = DateTimeOffset.UtcNow;
            await using (var updateCmd = new NpgsqlCommand("""
                UPDATE kiosk_ops.slot_maintenance_sessions
                SET stage = 'NeedsInspection', latest_inspection_reason = @reason, latest_inspection_at = CURRENT_TIMESTAMP,
                    row_version = row_version + 1, updated_at = CURRENT_TIMESTAMP
                WHERE id = @id
                """, connection, transaction))
            {
                updateCmd.Parameters.AddWithValue("reason", reasonTrim);
                updateCmd.Parameters.AddWithValue("id", sessionId);
                await updateCmd.ExecuteNonQueryAsync(ct);
            }

            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            await using (var audit = new NpgsqlCommand("""
                SELECT flow.audit(@actor, 'SLOT_MAINTENANCE_ISSUE_REPORTED', 'slots', @slot,
                    jsonb_build_object('sessionId', @id, 'reason', @reason));
                """, connection, transaction))
            {
                audit.Parameters.AddWithValue("actor", actorId);
                audit.Parameters.AddWithValue("slot", slotId);
                audit.Parameters.AddWithValue("id", sessionId);
                audit.Parameters.AddWithValue("reason", reasonTrim);
                await audit.ExecuteNonQueryAsync(ct);
            }

            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);

            var updatedRow = s with
            {
                Stage = "NeedsInspection",
                LatestInspectionReason = reasonTrim,
                LatestInspectionAt = now.UtcDateTime,
                RowVersion = s.RowVersion + 1
            };

            bool canOpen = updatedRow.InspectionOpenCount < 3 && updatedRow.ExpiresAt > DateTime.UtcNow && updatedRow.CurrentCmdId.HasValue;
            return Results.Ok(ToResponse(updatedRow, canOpen));
        }
        catch (PostgresException ex) when (ex.SqlState is "55P03" or "40P01")
        {
            return Results.Problem(statusCode: 409, detail: "Ô đang được xử lý đồng thời. Hãy tải lại trạng thái trước khi thử lại.");
        }
    }

    public async Task<IResult> UpdateSensorCapabilityAsync(
        Guid kioskId, Guid slotId, Guid adminId, UpdateSensorCapabilityRequest input, CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;

        try
        {
            await using var timeoutCmd = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", connection, transaction);
            await timeoutCmd.ExecuteNonQueryAsync(ct);

            // Kiosk SHARE -> Slot UPDATE
            await using (var kioskCmd = new NpgsqlCommand("SELECT 1 FROM kiosk_ops.kiosks WHERE id = @kiosk FOR SHARE", connection, transaction))
            {
                kioskCmd.Parameters.AddWithValue("kiosk", kioskId);
                if (await kioskCmd.ExecuteScalarAsync(ct) is null) return Results.NotFound();
            }

            int relay;
            await using (var slotCmd = new NpgsqlCommand("SELECT relay_channel FROM kiosk_ops.slots WHERE id = @slot AND kiosk_id = @kiosk FOR UPDATE", connection, transaction))
            {
                slotCmd.Parameters.AddWithValue("slot", slotId);
                slotCmd.Parameters.AddWithValue("kiosk", kioskId);
                var res = await slotCmd.ExecuteScalarAsync(ct);
                if (res is null) return Results.NotFound();
                relay = Convert.ToInt32(res);
            }

            if (relay is < 0 or > 3)
                return Results.Problem(statusCode: 400, detail: "Kênh relay của ô không nằm trong khoảng 0..3.");

            await using (var upsert = new NpgsqlCommand("""
                INSERT INTO kiosk_ops.slot_sensor_capabilities(
                    slot_id, kiosk_id, relay_channel, enabled, door_verified, presence_verified,
                    supports_manual_retry, enrolled_by, enrolled_at, updated_at
                ) VALUES (
                    @slot, @kiosk, @relay, @enabled, @doorVerified, @presenceVerified,
                    false, @admin, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                )
                ON CONFLICT (slot_id) DO UPDATE SET
                    kiosk_id = excluded.kiosk_id,
                    relay_channel = excluded.relay_channel,
                    enabled = excluded.enabled,
                    door_verified = excluded.door_verified,
                    presence_verified = excluded.presence_verified,
                    supports_manual_retry = false,
                    manual_retry_hardware_id = NULL,
                    updated_at = CURRENT_TIMESTAMP
                """, connection, transaction))
            {
                upsert.Parameters.AddWithValue("slot", slotId);
                upsert.Parameters.AddWithValue("kiosk", kioskId);
                upsert.Parameters.AddWithValue("relay", relay);
                upsert.Parameters.AddWithValue("enabled", input.Enabled);
                upsert.Parameters.AddWithValue("doorVerified", input.DoorVerified);
                upsert.Parameters.AddWithValue("presenceVerified", input.PresenceVerified);
                upsert.Parameters.AddWithValue("admin", adminId);
                await upsert.ExecuteNonQueryAsync(ct);
            }

            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            await using (var audit = new NpgsqlCommand("""
                SELECT flow.audit(@admin, 'SLOT_SENSOR_CAPABILITY_UPDATED', 'slots', @slot,
                    jsonb_build_object('enabled', @enabled, 'doorVerified', @doorVerified, 'presenceVerified', @presenceVerified));
                """, connection, transaction))
            {
                audit.Parameters.AddWithValue("admin", adminId);
                audit.Parameters.AddWithValue("slot", slotId);
                audit.Parameters.AddWithValue("enabled", input.Enabled);
                audit.Parameters.AddWithValue("doorVerified", input.DoorVerified);
                audit.Parameters.AddWithValue("presenceVerified", input.PresenceVerified);
                await audit.ExecuteNonQueryAsync(ct);
            }

            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);

            return Results.Ok(new SlotSensorCapabilityResponse(
                slotId, kioskId, relay, input.Enabled, input.DoorVerified, input.PresenceVerified,
                false, adminId, DateTime.UtcNow));
        }
        catch (PostgresException ex) when (ex.SqlState is "55P03" or "40P01")
        {
            return Results.Problem(statusCode: 409, detail: "Ô đang được xử lý đồng thời. Hãy tải lại trạng thái trước khi thử lại.");
        }
    }

    public async Task SyncActiveSessionsForKioskAsync(Guid kioskId, CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        var sessionList = new List<(Guid SessionId, Guid SlotId, Guid SellerId)>();
        await using (var listCmd = new NpgsqlCommand("""
            SELECT id, slot_id, seller_id FROM kiosk_ops.slot_maintenance_sessions
            WHERE kiosk_id = @kiosk AND stage <> 'Completed'
            """, connection))
        {
            listCmd.Parameters.AddWithValue("kiosk", kioskId);
            await using var reader = await listCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                sessionList.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2)));
        }

        foreach (var (sessId, sId, selId) in sessionList)
        {
            await using var txConn = await data.OpenConnectionAsync(ct);
            using var outbox = await txConn.BeginTransactionAsync(publisher, cancellationToken: ct);
            var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
            var auth = await LoadAndLockContextAsync(txConn, transaction, kioskId, sId, selId, actorId: null, sessionId: sessId, ct);
            if (auth is not null && auth.Session is not null)
            {
                await SyncSessionInternalAsync(txConn, transaction, auth, ct);
                await outbox.CommitAsync(ct);
            }
        }
    }

    private async Task<SlotMaintenanceSessionResponse> SyncSessionInternalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MaintenanceLockedContext auth,
        CancellationToken ct)
    {
        var s = auth.Session!;
        var now = DateTimeOffset.UtcNow;

        if (s.Stage == "Completed")
            return ToResponse(s);

        var isShopAuthorized = auth.Seller.CanMaintain(s.Action == "REMOVE");

        // Recheck seller/actor/assignment revocation under locks
        if (auth.Actor.Status != "ACTIVE" || auth.Actor.Role != "SELLER" || !isShopAuthorized || !auth.AssignmentActive)
        {
            if (s.Stage != "NeedsInspection")
            {
                await using var revokeCmd = new NpgsqlCommand("""
                    UPDATE kiosk_ops.slot_maintenance_sessions
                    SET stage = 'NeedsInspection', latest_inspection_reason = 'ActorOrSellerRevoked',
                        latest_inspection_at = CURRENT_TIMESTAMP, row_version = row_version + 1, updated_at = CURRENT_TIMESTAMP
                    WHERE id = @id
                    """, connection, transaction);
                revokeCmd.Parameters.AddWithValue("id", s.Id);
                await revokeCmd.ExecuteNonQueryAsync(ct);

                var watermarkRevoke = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
                await using var auditRevoke = new NpgsqlCommand("""
                    SELECT flow.audit(@actor, 'SLOT_MAINTENANCE_INSPECTION_REQUIRED', 'slots', @slot,
                        jsonb_build_object('sessionId', @id, 'reason', 'ActorOrSellerRevoked'));
                    """, connection, transaction);
                auditRevoke.Parameters.AddWithValue("actor", s.ActorId);
                auditRevoke.Parameters.AddWithValue("slot", s.SlotId);
                auditRevoke.Parameters.AddWithValue("id", s.Id);
                await auditRevoke.ExecuteNonQueryAsync(ct);
                await DomainChanges.PublishAsync(connection, transaction, publisher, watermarkRevoke, ct);

                return ToResponse(s with
                {
                    Stage = "NeedsInspection",
                    LatestInspectionReason = "ActorOrSellerRevoked",
                    LatestInspectionAt = now.UtcDateTime,
                    RowVersion = s.RowVersion + 1
                }, canOpenForCheck: false);
            }
            return ToResponse(s);
        }

        // Recheck capability registration and mapping
        if (!auth.Capability.IsFullyVerifiedFor(auth.Kiosk.Id, auth.Slot.RelayChannel))
        {
            if (s.Stage != "NeedsInspection")
            {
                await using var capRevokeCmd = new NpgsqlCommand("""
                    UPDATE kiosk_ops.slot_maintenance_sessions
                    SET stage = 'NeedsInspection', latest_inspection_reason = 'SensorCapabilityRevoked',
                        latest_inspection_at = CURRENT_TIMESTAMP, row_version = row_version + 1, updated_at = CURRENT_TIMESTAMP
                    WHERE id = @id
                    """, connection, transaction);
                capRevokeCmd.Parameters.AddWithValue("id", s.Id);
                await capRevokeCmd.ExecuteNonQueryAsync(ct);

                var watermarkCap = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
                await using var auditCap = new NpgsqlCommand("""
                    SELECT flow.audit(@actor, 'SLOT_MAINTENANCE_INSPECTION_REQUIRED', 'slots', @slot,
                        jsonb_build_object('sessionId', @id, 'reason', 'SensorCapabilityRevoked'));
                    """, connection, transaction);
                auditCap.Parameters.AddWithValue("actor", s.ActorId);
                auditCap.Parameters.AddWithValue("slot", s.SlotId);
                auditCap.Parameters.AddWithValue("id", s.Id);
                await auditCap.ExecuteNonQueryAsync(ct);
                await DomainChanges.PublishAsync(connection, transaction, publisher, watermarkCap, ct);

                return ToResponse(s with
                {
                    Stage = "NeedsInspection",
                    LatestInspectionReason = "SensorCapabilityRevoked",
                    LatestInspectionAt = now.UtcDateTime,
                    RowVersion = s.RowVersion + 1
                }, canOpenForCheck: false);
            }
            return ToResponse(s);
        }

        // Check kiosk online
        bool kioskOnline = auth.Kiosk.Status == "ONLINE" &&
            auth.Kiosk.LastHeartbeatAt.HasValue &&
            auth.Kiosk.LastHeartbeatAt.Value >= now.UtcDateTime.AddSeconds(-SlotMaintenancePolicy.TelemetryFreshnessSeconds);

        if (!kioskOnline)
        {
            if (s.Stage != "NeedsInspection")
            {
                await using var offlineCmd = new NpgsqlCommand("""
                    UPDATE kiosk_ops.slot_maintenance_sessions
                    SET stage = 'NeedsInspection', latest_inspection_reason = 'KioskOffline',
                        latest_inspection_at = CURRENT_TIMESTAMP, row_version = row_version + 1, updated_at = CURRENT_TIMESTAMP
                    WHERE id = @id
                    """, connection, transaction);
                offlineCmd.Parameters.AddWithValue("id", s.Id);
                await offlineCmd.ExecuteNonQueryAsync(ct);

                var watermarkOffline = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
                await using var auditOffline = new NpgsqlCommand("""
                    SELECT flow.audit(@actor, 'SLOT_MAINTENANCE_INSPECTION_REQUIRED', 'slots', @slot,
                        jsonb_build_object('sessionId', @id, 'reason', 'KioskOffline'));
                    """, connection, transaction);
                auditOffline.Parameters.AddWithValue("actor", s.ActorId);
                auditOffline.Parameters.AddWithValue("slot", s.SlotId);
                auditOffline.Parameters.AddWithValue("id", s.Id);
                await auditOffline.ExecuteNonQueryAsync(ct);
                await DomainChanges.PublishAsync(connection, transaction, publisher, watermarkOffline, ct);

                return ToResponse(s with
                {
                    Stage = "NeedsInspection",
                    LatestInspectionReason = "KioskOffline",
                    LatestInspectionAt = now.UtcDateTime,
                    RowVersion = s.RowVersion + 1
                }, canOpenForCheck: false);
            }
            return ToResponse(s);
        }

        // Check expiration
        if (s.ExpiresAt <= now.UtcDateTime)
        {
            if (s.Stage != "NeedsInspection")
            {
                await using var expireCmd = new NpgsqlCommand("""
                    UPDATE kiosk_ops.slot_maintenance_sessions
                    SET stage = 'NeedsInspection', latest_inspection_reason = 'SessionExpired',
                        latest_inspection_at = CURRENT_TIMESTAMP, row_version = row_version + 1, updated_at = CURRENT_TIMESTAMP
                    WHERE id = @id
                    """, connection, transaction);
                expireCmd.Parameters.AddWithValue("id", s.Id);
                await expireCmd.ExecuteNonQueryAsync(ct);

                var watermarkExp = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
                await using var auditExp = new NpgsqlCommand("""
                    SELECT flow.audit(@actor, 'SLOT_MAINTENANCE_SESSION_EXPIRED', 'slots', @slot,
                        jsonb_build_object('sessionId', @id));
                    """, connection, transaction);
                auditExp.Parameters.AddWithValue("actor", s.ActorId);
                auditExp.Parameters.AddWithValue("slot", s.SlotId);
                auditExp.Parameters.AddWithValue("id", s.Id);
                await auditExp.ExecuteNonQueryAsync(ct);
                await DomainChanges.PublishAsync(connection, transaction, publisher, watermarkExp, ct);

                return ToResponse(s with
                {
                    Stage = "NeedsInspection",
                    LatestInspectionReason = "SessionExpired",
                    LatestInspectionAt = now.UtcDateTime,
                    RowVersion = s.RowVersion + 1
                }, canOpenForCheck: false);
            }
            return ToResponse(s);
        }

        // If no active command (e.g. Replace after empty confirmed, awaiting open-next)
        if (s.CurrentCmdId is null)
            return ToResponse(s);

        // Check unlock token status
        string tokenStatus;
        DateTime? doorOpenedAt, doorClosedAt;
        await using (var tokenCmd = new NpgsqlCommand("""
            SELECT status, door_opened_at, door_closed_at FROM kiosk_ops.unlock_tokens WHERE cmd_id = @cmd
            """, connection, transaction))
        {
            tokenCmd.Parameters.AddWithValue("cmd", s.CurrentCmdId.Value);
            await using var reader = await tokenCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return ToResponse(s);
            tokenStatus = reader.GetString(0);
            doorOpenedAt = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            doorClosedAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
        }

        if (tokenStatus != "CLOSED" || doorClosedAt is null)
        {
            if (tokenStatus == "OPENED" || (doorOpenedAt is not null && doorClosedAt is null))
            {
                var openedSubstate = s.Stage == "AwaitRemoval" ? "RemovalDoorOpened" : "LoadingDoorOpened";
                if (s.Substate != openedSubstate)
                {
                    await using var updateSub = new NpgsqlCommand("""
                        UPDATE kiosk_ops.slot_maintenance_sessions SET substate = @sub WHERE id = @id
                        """, connection, transaction);
                    updateSub.Parameters.AddWithValue("sub", openedSubstate);
                    updateSub.Parameters.AddWithValue("id", s.Id);
                    await updateSub.ExecuteNonQueryAsync(ct);
                    s = s with { Substate = openedSubstate };
                }
            }
            return ToResponse(s);
        }

        // Malformed genuine CLOSED: requires door_opened_at not null (no fallback to phaseStarted)
        if (doorOpenedAt is null)
        {
            if (s.Stage != "NeedsInspection")
            {
                await using var badCycleCmd = new NpgsqlCommand("""
                    UPDATE kiosk_ops.slot_maintenance_sessions
                    SET stage = 'NeedsInspection', latest_inspection_reason = 'DoorOpenedNotRecorded',
                        latest_inspection_at = CURRENT_TIMESTAMP, row_version = row_version + 1, updated_at = CURRENT_TIMESTAMP
                    WHERE id = @id
                    """, connection, transaction);
                badCycleCmd.Parameters.AddWithValue("id", s.Id);
                await badCycleCmd.ExecuteNonQueryAsync(ct);
                return ToResponse(s with
                {
                    Stage = "NeedsInspection",
                    LatestInspectionReason = "DoorOpenedNotRecorded",
                    LatestInspectionAt = now.UtcDateTime,
                    RowVersion = s.RowVersion + 1
                }, canOpenForCheck: false);
            }
            return ToResponse(s);
        }

        // Fetch latest flower presence
        long obsAtMs = 0;
        DateTime recAt = DateTime.MinValue;
        int[] states = [];
        bool hasObs = false;
        await using (var obsCmd = new NpgsqlCommand("""
            SELECT observed_at, received_at, states FROM kiosk_ops.flower_presence WHERE kiosk_id = @kiosk
            """, connection, transaction))
        {
            obsCmd.Parameters.AddWithValue("kiosk", s.KioskId);
            await using var reader = await obsCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                obsAtMs = Convert.ToInt64(reader.GetValue(0));
                recAt = reader.GetDateTime(1);
                states = reader.GetFieldValue<int[]>(2);
                hasObs = true;
            }
        }

        if (!hasObs)
            return ToResponse(s); // Pending sensor telemetry

        var obsDto = DateTimeOffset.FromUnixTimeMilliseconds(obsAtMs);
        var recDto = new DateTimeOffset(recAt, TimeSpan.Zero);
        var phaseStartedDto = new DateTimeOffset(s.PhaseStartedAt, TimeSpan.Zero);
        var doorOpenedDto = new DateTimeOffset(doorOpenedAt.Value, TimeSpan.Zero);
        var doorClosedDto = new DateTimeOffset(doorClosedAt.Value, TimeSpan.Zero);

        // Waiting for post-close snapshot before first fresh sensor: pending, not inspection!
        if (obsDto <= doorClosedDto || obsDto <= phaseStartedDto)
            return ToResponse(s);

        var observation = new SensorObservation(
            obsDto, recDto, states,
            Installed: auth.Capability.DoorVerified && auth.Capability.PresenceVerified,
            CapabilityEnabled: auth.Capability.Enabled);

        var cycle = new DoorCycleEvidence(phaseStartedDto, doorOpenedDto, doorClosedDto, IsClosed: true);

        var actionEnum = Enum.Parse<MaintenanceAction>(s.Action, true);
        var stageEnum = Enum.Parse<MaintenanceStage>(s.Stage, true);
        var intendedStageEnum = Enum.Parse<MaintenanceStage>(s.IntendedStage, true);

        StageTransitionResult transition;
        if (stageEnum == MaintenanceStage.NeedsInspection)
        {
            var priorObs = s.LatestInspectionObservedAt.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(s.LatestInspectionObservedAt.Value)
                : DateTimeOffset.MinValue;
            transition = SlotMaintenancePolicy.EvaluateInspectionRecovery(
                actionEnum, intendedStageEnum, auth.Slot.RelayChannel, priorObs, cycle, observation, now, kioskOnline);
        }
        else
        {
            transition = SlotMaintenancePolicy.EvaluateStageCompletion(
                actionEnum, stageEnum, auth.Slot.RelayChannel, cycle, observation, now, kioskOnline);
        }

        var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);

        if (transition.NextStage == MaintenanceStage.NeedsInspection)
        {
            await using var inspCmd = new NpgsqlCommand("""
                UPDATE kiosk_ops.slot_maintenance_sessions
                SET stage = 'NeedsInspection', latest_inspection_reason = @reason,
                    latest_inspection_observed_at = @obsAt, latest_inspection_at = CURRENT_TIMESTAMP,
                    row_version = row_version + 1, updated_at = CURRENT_TIMESTAMP
                WHERE id = @id
                """, connection, transaction);
            inspCmd.Parameters.AddWithValue("reason", transition.InspectionReason ?? "SensorMismatch");
            inspCmd.Parameters.AddWithValue("obsAt", obsAtMs);
            inspCmd.Parameters.AddWithValue("id", s.Id);
            await inspCmd.ExecuteNonQueryAsync(ct);

            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);

            bool canOpen = s.InspectionOpenCount < 3 && s.ExpiresAt > DateTime.UtcNow && s.CurrentCmdId.HasValue;
            return ToResponse(s with
            {
                Stage = "NeedsInspection",
                LatestInspectionReason = transition.InspectionReason,
                LatestInspectionObservedAt = obsAtMs,
                LatestInspectionAt = now.UtcDateTime,
                RowVersion = s.RowVersion + 1
            }, canOpen);
        }

        if (transition.NextStage == MaintenanceStage.Completed)
        {
            Guid? finalBouquetId = s.NewBouquetId;
            if (actionEnum == MaintenanceAction.Remove)
            {
                await using var restoreStatus = new NpgsqlCommand("UPDATE kiosk_ops.slots SET status = @prev WHERE id = @slot", connection, transaction);
                restoreStatus.Parameters.AddWithValue("prev", s.PreviousSlotStatus);
                restoreStatus.Parameters.AddWithValue("slot", s.SlotId);
                await restoreStatus.ExecuteNonQueryAsync(ct);

                await using var removeCall = new NpgsqlCommand("SELECT flow.seller_remove_slot_bouquet(@kiosk, @slot, @actor, @bouquet, @reason, true)", connection, transaction);
                removeCall.Parameters.AddWithValue("kiosk", s.KioskId);
                removeCall.Parameters.AddWithValue("slot", s.SlotId);
                removeCall.Parameters.AddWithValue("actor", s.ActorId);
                removeCall.Parameters.AddWithValue("bouquet", s.OldBouquetId!.Value);
                removeCall.Parameters.AddWithValue("reason", s.Reason);
                await removeCall.ExecuteNonQueryAsync(ct);
            }
            else // Load or Replace loading completion
            {
                await using var restoreEmpty = new NpgsqlCommand("UPDATE kiosk_ops.slots SET status = 'RENTED_EMPTY' WHERE id = @slot", connection, transaction);
                restoreEmpty.Parameters.AddWithValue("slot", s.SlotId);
                await restoreEmpty.ExecuteNonQueryAsync(ct);

                var batch = Guid.CreateVersion7();
                var qr = $"FB-{Guid.CreateVersion7():N}";
                await using var stockCall = new NpgsqlCommand("SELECT flow.stock_bouquet(@batch, @prod, @slot, @actor, @qr)", connection, transaction);
                stockCall.Parameters.AddWithValue("batch", batch);
                stockCall.Parameters.AddWithValue("prod", s.NewProductId!.Value);
                stockCall.Parameters.AddWithValue("slot", s.SlotId);
                stockCall.Parameters.AddWithValue("actor", s.ActorId);
                stockCall.Parameters.AddWithValue("qr", qr);
                finalBouquetId = (Guid)(await stockCall.ExecuteScalarAsync(ct))!;
            }

            await using var completeCmd = new NpgsqlCommand("""
                UPDATE kiosk_ops.slot_maintenance_sessions
                SET stage = 'Completed', substate = 'Completed', new_bouquet_id = @newBouquet,
                    row_version = row_version + 1, updated_at = CURRENT_TIMESTAMP
                WHERE id = @id
                """, connection, transaction);
            completeCmd.Parameters.AddWithValue("newBouquet", NpgsqlDbType.Uuid, (object?)finalBouquetId ?? DBNull.Value);
            completeCmd.Parameters.AddWithValue("id", s.Id);
            await completeCmd.ExecuteNonQueryAsync(ct);

            await using var audit = new NpgsqlCommand("""
                SELECT flow.audit(@actor, 'SLOT_MAINTENANCE_COMPLETED', 'slots', @slot,
                    jsonb_build_object('sessionId', @id, 'action', @action, 'newBouquetId', @newBouquet));
                """, connection, transaction);
            audit.Parameters.AddWithValue("actor", s.ActorId);
            audit.Parameters.AddWithValue("slot", s.SlotId);
            audit.Parameters.AddWithValue("id", s.Id);
            audit.Parameters.AddWithValue("action", s.Action);
            audit.Parameters.AddWithValue("newBouquet", NpgsqlDbType.Uuid, (object?)finalBouquetId ?? DBNull.Value);
            await audit.ExecuteNonQueryAsync(ct);

            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            return ToResponse(s with
            {
                Stage = "Completed",
                Substate = "Completed",
                NewBouquetId = finalBouquetId,
                RowVersion = s.RowVersion + 1
            });
        }

        if (actionEnum == MaintenanceAction.Replace && transition.NextStage == MaintenanceStage.AwaitLoading)
        {
            await using var restoreStatus = new NpgsqlCommand("UPDATE kiosk_ops.slots SET status = @prev WHERE id = @slot", connection, transaction);
            restoreStatus.Parameters.AddWithValue("prev", s.PreviousSlotStatus);
            restoreStatus.Parameters.AddWithValue("slot", s.SlotId);
            await restoreStatus.ExecuteNonQueryAsync(ct);

            await using var removeCall = new NpgsqlCommand("SELECT flow.seller_remove_slot_bouquet(@kiosk, @slot, @actor, @bouquet, @reason, true)", connection, transaction);
            removeCall.Parameters.AddWithValue("kiosk", s.KioskId);
            removeCall.Parameters.AddWithValue("slot", s.SlotId);
            removeCall.Parameters.AddWithValue("actor", s.ActorId);
            removeCall.Parameters.AddWithValue("bouquet", s.OldBouquetId!.Value);
            removeCall.Parameters.AddWithValue("reason", s.Reason);
            await removeCall.ExecuteNonQueryAsync(ct);

            await using var reLock = new NpgsqlCommand("UPDATE kiosk_ops.slots SET status = 'LOCKED' WHERE id = @slot", connection, transaction);
            reLock.Parameters.AddWithValue("slot", s.SlotId);
            await reLock.ExecuteNonQueryAsync(ct);

            await using var updatePhase = new NpgsqlCommand("""
                UPDATE kiosk_ops.slot_maintenance_sessions
                SET stage = 'AwaitLoading', intended_stage = 'AwaitLoading', substate = 'RemovalClosedEmptyConfirmed',
                    current_cmd_id = NULL, row_version = row_version + 1, updated_at = CURRENT_TIMESTAMP
                WHERE id = @id
                """, connection, transaction);
            updatePhase.Parameters.AddWithValue("id", s.Id);
            await updatePhase.ExecuteNonQueryAsync(ct);

            await using var audit = new NpgsqlCommand("""
                SELECT flow.audit(@actor, 'SLOT_MAINTENANCE_REMOVAL_CONFIRMED', 'slots', @slot,
                    jsonb_build_object('sessionId', @id));
                """, connection, transaction);
            audit.Parameters.AddWithValue("actor", s.ActorId);
            audit.Parameters.AddWithValue("slot", s.SlotId);
            audit.Parameters.AddWithValue("id", s.Id);
            await audit.ExecuteNonQueryAsync(ct);

            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            return ToResponse(s with
            {
                Stage = "AwaitLoading",
                IntendedStage = "AwaitLoading",
                Substate = "RemovalClosedEmptyConfirmed",
                CurrentCmdId = null,
                RowVersion = s.RowVersion + 1
            });
        }

        return ToResponse(s);
    }
}

public static class SellerSlotMaintenanceEndpoints
{
    public static void MapSellerSlotMaintenance(this WebApplication app)
    {
        app.MapPost("/api/me/kiosks/{kioskId:guid}/slots/{slotId:guid}/maintenance-sessions", async (
            Guid kioskId, Guid slotId, [FromBody] StartSlotMaintenanceRequest input,
            HttpContext http, SlotMaintenanceService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId) ||
                !Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId))
                return Results.Unauthorized();
            return await service.StartSessionAsync(kioskId, slotId, actorId, sellerId, input, ct);
        })
        .RequireAuthorization("Seller")
        .WithTags("KioskOps")
        .WithSummary("Bắt đầu phiên bảo trì tự phục vụ cho ô tủ (Load, Remove, Replace)")
        .WithDescription("Khóa ô chuyển sang LOCKED, phát lệnh SELLER_ACCESS và thiết lập phiên bảo trì.")
        .Produces<SlotMaintenanceSessionResponse>()
        .ProducesProblem(400).ProducesProblem(409)
        .Produces(401).Produces(403).Produces(404);

        app.MapGet("/api/me/kiosks/{kioskId:guid}/slots/{slotId:guid}/maintenance-sessions/{sessionId:guid}", async (
            Guid kioskId, Guid slotId, Guid sessionId,
            HttpContext http, SlotMaintenanceService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId))
                return Results.Unauthorized();
            return await service.GetSessionAsync(kioskId, slotId, sessionId, sellerId, ct);
        })
        .RequireAuthorization("Seller")
        .WithTags("KioskOps")
        .WithSummary("Xem chi tiết trạng thái phiên bảo trì ô tủ")
        .Produces<SlotMaintenanceSessionResponse>()
        .Produces(401).Produces(403).Produces(404);

        app.MapPost("/api/me/kiosks/{kioskId:guid}/slots/{slotId:guid}/maintenance-sessions/{sessionId:guid}/check", async (
            Guid kioskId, Guid slotId, Guid sessionId,
            HttpContext http, SlotMaintenanceService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId))
                return Results.Unauthorized();
            return await service.CheckSessionAsync(kioskId, slotId, sessionId, sellerId, ct);
        })
        .RequireAuthorization("Seller")
        .WithTags("KioskOps")
        .WithSummary("Đồng bộ và kiểm tra tiến trình phiên bảo trì dựa trên cảm biến cửa và hoa")
        .Produces<SlotMaintenanceSessionResponse>()
        .Produces(401).Produces(403).Produces(404);

        app.MapPost("/api/me/kiosks/{kioskId:guid}/slots/{slotId:guid}/maintenance-sessions/{sessionId:guid}/open-next", async (
            Guid kioskId, Guid slotId, Guid sessionId,
            HttpContext http, SlotMaintenanceService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId) ||
                !Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId))
                return Results.Unauthorized();
            return await service.OpenNextAsync(kioskId, slotId, sessionId, actorId, sellerId, ct);
        })
        .RequireAuthorization("Seller")
        .WithTags("KioskOps")
        .WithSummary("Mở cửa bước tiếp theo (nạp hoa mới) trong quy trình đổi hoa REPLACE")
        .Produces<SlotMaintenanceSessionResponse>()
        .ProducesProblem(409)
        .Produces(401).Produces(403).Produces(404);

        app.MapPost("/api/me/kiosks/{kioskId:guid}/slots/{slotId:guid}/maintenance-sessions/{sessionId:guid}/open-for-check", async (
            Guid kioskId, Guid slotId, Guid sessionId, [FromBody] OpenForCheckRequest input,
            HttpContext http, SlotMaintenanceService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId) ||
                !Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId))
                return Results.Unauthorized();
            return await service.OpenForCheckAsync(kioskId, slotId, sessionId, actorId, sellerId, input, ct);
        })
        .RequireAuthorization("Seller")
        .WithTags("KioskOps")
        .WithSummary("Mở cửa kiểm tra ô trong trạng thái NeedsInspection")
        .Produces<SlotMaintenanceSessionResponse>()
        .ProducesProblem(400).ProducesProblem(409)
        .Produces(401).Produces(403).Produces(404);

        app.MapPost("/api/me/kiosks/{kioskId:guid}/slots/{slotId:guid}/maintenance-sessions/{sessionId:guid}/report-issue", async (
            Guid kioskId, Guid slotId, Guid sessionId, [FromBody] ReportIssueRequest input,
            HttpContext http, SlotMaintenanceService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId) ||
                !Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId))
                return Results.Unauthorized();
            return await service.ReportIssueAsync(kioskId, slotId, sessionId, actorId, sellerId, input.Reason, ct);
        })
        .RequireAuthorization("Seller")
        .WithTags("KioskOps")
        .WithSummary("Báo lỗi trong phiên bảo trì để chuyển sang NeedsInspection")
        .Produces<SlotMaintenanceSessionResponse>()
        .ProducesProblem(400).ProducesProblem(409)
        .Produces(401).Produces(403).Produces(404);

        app.MapPut("/api/admin/kiosks/{kioskId:guid}/slots/{slotId:guid}/sensor-capabilities", async (
            Guid kioskId, Guid slotId, [FromBody] UpdateSensorCapabilityRequest input,
            HttpContext http, SlotMaintenanceService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var adminId))
                return Results.Unauthorized();
            return await service.UpdateSensorCapabilityAsync(kioskId, slotId, adminId, input, ct);
        })
        .RequireAuthorization("Admin")
        .WithTags("KioskOps")
        .WithSummary("Cấu hình năng lực cảm biến cho ô tủ (Admin)")
        .Produces<SlotSensorCapabilityResponse>()
        .ProducesProblem(400)
        .Produces(401).Produces(403).Produces(404);
    }
}
