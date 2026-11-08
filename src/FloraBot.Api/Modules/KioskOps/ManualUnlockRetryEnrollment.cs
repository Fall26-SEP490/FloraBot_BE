using System.Security.Claims;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record ManualUnlockRetryEnrollmentRequest(bool Enabled, bool FirmwareVerified);

public static class ManualUnlockRetryEnrollment
{
    public static void MapManualUnlockRetryEnrollment(this WebApplication app)
    {
        app.MapPut("/api/admin/kiosks/{kioskId:guid}/slots/{slotId:guid}/manual-unlock-retry", async (
            Guid kioskId,
            Guid slotId,
            [FromBody] ManualUnlockRetryEnrollmentRequest input,
            HttpContext http,
            NpgsqlDataSource data,
            ICapPublisher publisher,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var adminId))
                return Results.Unauthorized();

            // When enabling, firmware confirmation is strictly required
            if (input.Enabled && !input.FirmwareVerified)
                return Results.Problem(statusCode: 400, detail: "Yêu cầu xác nhận phiên bản firmware hỗ trợ mở thủ công (firmwareVerified).");

            var now = DateTimeOffset.UtcNow;
            await using var connection = await data.OpenConnectionAsync(ct);
            using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
            var transaction = (NpgsqlTransaction)outbox.DbTransaction!;

            try
            {
                await using var timeoutCmd = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", connection, transaction);
                await timeoutCmd.ExecuteNonQueryAsync(ct);

                // Active admin recheck
                await using (var adminCheck = new NpgsqlCommand("SELECT 1 WHERE flow.check_active_admin(@admin) IS NOT NULL", connection, transaction))
                {
                    adminCheck.Parameters.AddWithValue("admin", adminId);
                    if (await adminCheck.ExecuteScalarAsync(ct) is null)
                        return Results.Forbid();
                }

                // Kiosk SHARE
                string kioskStatus;
                DateTime? lastHeartbeat;
                string? hardwareId;
                await using (var kioskCmd = new NpgsqlCommand("""
                    SELECT status, last_heartbeat_at, hardware_id
                    FROM kiosk_ops.kiosks
                    WHERE id = @kiosk
                    FOR SHARE
                    """, connection, transaction))
                {
                    kioskCmd.Parameters.AddWithValue("kiosk", kioskId);
                    await using var reader = await kioskCmd.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct)) return Results.NotFound();
                    kioskStatus = reader.GetString(0);
                    lastHeartbeat = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
                    hardwareId = reader.IsDBNull(2) ? null : reader.GetString(2);
                }

                // Slot UPDATE
                int relay;
                await using (var slotCmd = new NpgsqlCommand("""
                    SELECT relay_channel
                    FROM kiosk_ops.slots
                    WHERE id = @slot AND kiosk_id = @kiosk
                    FOR UPDATE
                    """, connection, transaction))
                {
                    slotCmd.Parameters.AddWithValue("slot", slotId);
                    slotCmd.Parameters.AddWithValue("kiosk", kioskId);
                    var res = await slotCmd.ExecuteScalarAsync(ct);
                    if (res is null) return Results.NotFound();
                    relay = Convert.ToInt32(res);
                }

                if (relay is < 0 or > 3)
                    return Results.Problem(statusCode: 400, detail: "Kênh relay của ô không hợp lệ (phải từ 0 đến 3).");

                // Capability check: exact mapping (kiosk_id + relay_channel)
                bool capExists = false;
                bool doorVerified = false;
                await using (var capCmd = new NpgsqlCommand("""
                    SELECT door_verified
                    FROM kiosk_ops.slot_sensor_capabilities
                    WHERE slot_id = @slot AND kiosk_id = @kiosk AND relay_channel = @relay
                    FOR SHARE
                    """, connection, transaction))
                {
                    capCmd.Parameters.AddWithValue("slot", slotId);
                    capCmd.Parameters.AddWithValue("kiosk", kioskId);
                    capCmd.Parameters.AddWithValue("relay", relay);
                    await using var reader = await capCmd.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        capExists = true;
                        doorVerified = reader.GetBoolean(0);
                    }
                }

                if (!capExists)
                    return Results.Problem(statusCode: 409, detail: "Ô chưa có đăng ký năng lực cảm biến khớp với kiosk và kênh relay.");

                if (input.Enabled)
                {
                    if (kioskStatus != "ONLINE")
                        return Results.Problem(statusCode: 409, detail: "Kiosk hiện đang không trực tuyến.");

                    if (!lastHeartbeat.HasValue ||
                        now.UtcDateTime - lastHeartbeat.Value > TimeSpan.FromSeconds(SlotMaintenancePolicy.TelemetryFreshnessSeconds) ||
                        lastHeartbeat.Value > now.UtcDateTime.AddSeconds(SlotMaintenancePolicy.TelemetryMaxFutureSeconds))
                    {
                        return Results.Problem(statusCode: 409, detail: "Tín hiệu heartbeat của kiosk không mới.");
                    }

                    var keyRing = http.RequestServices.GetService<DeviceKeyRing>();
                    if (hardwareId is null || keyRing is null || !keyRing.HardwareIds.Contains(hardwareId))
                        return Results.Problem(statusCode: 409, detail: "Kiosk chưa có khóa thiết bị phần cứng được cấp phát.");

                    if (!doorVerified)
                        return Results.Problem(statusCode: 409, detail: "Cảm biến cửa của ô chưa được xác minh (doorVerified = false).");
                }

                // Update supports_manual_retry
                SlotSensorCapabilityResponse response;
                await using (var updateCmd = new NpgsqlCommand("""
                    UPDATE kiosk_ops.slot_sensor_capabilities
                    SET supports_manual_retry = @supportsRetry,
                        manual_retry_hardware_id = @hardware,
                        updated_at = CURRENT_TIMESTAMP
                    WHERE slot_id = @slot AND kiosk_id = @kiosk
                    RETURNING slot_id, kiosk_id, relay_channel, enabled, door_verified, presence_verified, supports_manual_retry, enrolled_by, updated_at
                    """, connection, transaction))
                {
                    updateCmd.Parameters.AddWithValue("supportsRetry", input.Enabled);
                    updateCmd.Parameters.AddWithValue("hardware", NpgsqlTypes.NpgsqlDbType.Text,
                        input.Enabled ? hardwareId! : DBNull.Value);
                    updateCmd.Parameters.AddWithValue("slot", slotId);
                    updateCmd.Parameters.AddWithValue("kiosk", kioskId);
                    await using var reader = await updateCmd.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct))
                        return Results.NotFound();

                    response = new(
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        Convert.ToInt32(reader.GetValue(2)),
                        reader.GetBoolean(3),
                        reader.GetBoolean(4),
                        reader.GetBoolean(5),
                        reader.GetBoolean(6),
                        reader.IsDBNull(7) ? null : reader.GetGuid(7),
                        reader.GetDateTime(8));
                }

                var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
                await using (var audit = new NpgsqlCommand("""
                    SELECT flow.audit(@admin, 'SLOT_MANUAL_RETRY_ENROLLMENT_UPDATED', 'slots', @slot,
                        jsonb_build_object('kioskId', @kiosk, 'supportsManualRetry', @supportsRetry, 'firmwareVerified', @firmwareVerified));
                    """, connection, transaction))
                {
                    audit.Parameters.AddWithValue("admin", adminId);
                    audit.Parameters.AddWithValue("slot", slotId);
                    audit.Parameters.AddWithValue("kiosk", kioskId);
                    audit.Parameters.AddWithValue("supportsRetry", input.Enabled);
                    audit.Parameters.AddWithValue("firmwareVerified", input.FirmwareVerified);
                    await audit.ExecuteNonQueryAsync(ct);
                }

                await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
                await outbox.CommitAsync(ct);

                return Results.Ok(response);
            }
            catch (PostgresException ex) when (ex.SqlState is "55P03" or "40P01")
            {
                return Results.Problem(statusCode: 409, detail: "Ô đang được xử lý đồng thời. Hãy tải lại trạng thái trước khi thử lại.");
            }
        })
        .RequireAuthorization("Admin")
        .WithTags("KioskOps")
        .WithSummary("Cấu hình quyền thử lại lệnh mở khóa thủ công (Admin)")
        .WithDescription("Bật hoặc tắt supports_manual_retry cho ô tủ sau khi xác nhận phiên bản firmware và cảm biến cửa.")
        .Produces<SlotSensorCapabilityResponse>()
        .ProducesProblem(400).ProducesProblem(409)
        .Produces(401).Produces(403).Produces(404);
    }
}
