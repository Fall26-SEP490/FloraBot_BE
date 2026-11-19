using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record RepairReadiness(string? Code, string? Detail, Guid? BlockingSessionId,
    string? BlockingSessionStage, List<Guid> PendingCommandIds);

public static class TechnicalRepairReadiness
{
    private static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>
    {
        ["TASK_VERSION_CONFLICT"] = "Công việc đã đổi phiên bản. Tải lại chi tiết trước khi gửi tiếp.",
        ["REPAIR_APPROVAL_REQUIRED"] = "Chưa có quyền sửa hợp lệ. Liên hệ Seller để duyệt quyền sửa.",
        ["REPAIR_DOOR_CYCLE_REQUIRED"] = "Còn lệnh hoặc chu kỳ cửa chưa được xác nhận đóng. Kiểm tra cửa và phản hồi thiết bị.",
        ["REPAIR_SENSOR_STALE"] = "Tín hiệu kết nối hoặc cảm biến chưa đủ mới. Kiểm tra thiết bị rồi tải lại.",
        ["REPAIR_SENSOR_MISMATCH"] = "Cảm biến không khớp tồn kho. Đối chiếu hoa thực tế trước khi gửi báo cáo.",
        ["REPAIR_SLOT_STATE_CONFLICT"] = "Trạng thái ô chưa phù hợp để nghiệm thu. Quản lý vận hành cần kiểm tra ô và phiên thao tác.",
        ["REPAIR_SESSION_RECOVERY_REQUIRED"] = "Ô đang khóa bởi phiên thao tác. Quản lý vận hành cần kiểm tra và khôi phục phiên đủ điều kiện.",
        ["REPAIR_EVIDENCE_REQUIRED"] = "Cần ảnh kiểm tra gắn với công việc và báo cáo xác minh sau phục hồi."
    };

    public static IResult Problem(string code) => Results.Problem(statusCode: 409,
        detail: Messages.GetValueOrDefault(code, "Điều kiện sửa ô đã thay đổi. Tải lại để kiểm tra."),
        extensions: new Dictionary<string, object?> { ["code"] = code });

    public static void MapTechnicalRepairReadiness(this WebApplication app)
    {
        app.MapGet("/api/staff/tasks/{taskId:guid}/repair-readiness", ReadAsync)
            .RequireAuthorization("Technician").Produces<RepairReadiness>();
        app.MapGet("/api/operations/staff-tasks/{taskId:guid}/repair-readiness", ReadAsync)
            .RequireAuthorization("OperationsManager").Produces<RepairReadiness>();
    }

    private static async Task<IResult> ReadAsync(Guid taskId, NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        if (await StaffTaskAccess.Lock(connection, transaction, taskId, http, ct, readOnly: true) is null) return Results.NotFound();
        await using var command = new NpgsqlCommand("""
            SELECT flow.technical_repair_blocker(t.id),ms.id,ms.stage,
              ARRAY(SELECT u.cmd_id FROM kiosk_ops.unlock_tokens u WHERE u.slot_id=d.slot_id
                AND (u.status IN ('ISSUED','SENT','ACKED','OPENED')
                  OR (u.door_opened_at IS NOT NULL AND u.door_closed_at IS NULL)
                  OR EXISTS(SELECT 1 FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id=u.cmd_id AND i.disposition='PENDING'))
                ORDER BY u.issued_at,u.cmd_id)
            FROM kiosk_ops.staff_tasks t JOIN ordering.disputes d ON d.id=t.incident_id AND d.slot_id IS NOT NULL
            LEFT JOIN LATERAL (SELECT id,stage FROM kiosk_ops.slot_maintenance_sessions
                WHERE slot_id=d.slot_id AND stage NOT IN ('Completed','Recovered') ORDER BY updated_at DESC,id DESC LIMIT 1) ms ON true
            WHERE t.id=@task AND t.kind='INCIDENT'
            """, connection, transaction);
        command.Parameters.AddWithValue("task", taskId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return Results.NotFound();
        var code = reader.IsDBNull(0) ? null : reader.GetString(0);
        var result = new RepairReadiness(code, code is null ? null : Messages.GetValueOrDefault(code),
            reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetFieldValue<Guid[]>(3).ToList());
        await reader.DisposeAsync();
        await transaction.CommitAsync(ct);
        return Results.Ok(result);
    }
}
