using System.Security.Claims;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

internal sealed record TechnicalRepairRow(Guid Id, string KioskName, string SlotCode, string Reason, DateTimeOffset? ExpiresAt);
internal sealed record TechnicalRepairApproval(DateTimeOffset ExpiresAt);

internal static class TechnicalRepairAccess
{
    public static void MapTechnicalRepairAccess(this WebApplication app)
    {
        app.MapGet("/api/me/technical-repairs", ListAsync).RequireAuthorization("Seller").WithTags("KioskOps")
            .Produces<List<TechnicalRepairRow>>();
        app.MapPost("/api/me/technical-repairs/{taskId:guid}/approve", ApproveAsync)
            .RequireAuthorization("Seller").WithTags("KioskOps").Produces<TechnicalRepairApproval>();
        app.MapPost("/api/staff/tasks/{taskId:guid}/repair-access", IssueAsync)
            .RequireAuthorization("Technician").RequireRateLimiting("maintenance-code-issue").WithTags("KioskOps")
            .Produces<MaintenanceAccessIssued>();
    }

    private static async Task<IResult> ListAsync(HttpContext http, NpgsqlDataSource data, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId)) return Results.Forbid();
        await using var query = data.CreateCommand("""
            SELECT t.id,k.name,s.slot_code,d.reason,
                CASE WHEN g.revoked_at IS NULL AND g.technician_id=t.assignee_id AND g.expires_at>clock_timestamp() THEN g.expires_at END
            FROM kiosk_ops.staff_tasks t
            JOIN ordering.disputes d ON d.id=t.incident_id AND d.kiosk_id=t.kiosk_id
            JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id AND k.seller_id=@seller
            JOIN kiosk_ops.slots s ON s.id=d.slot_id AND s.kiosk_id=k.id AND s.current_seller_id=@seller
            JOIN kiosk_ops.manager_kiosks m ON m.kiosk_id=k.id AND m.manager_id=t.assigned_by
            LEFT JOIN kiosk_ops.technical_repair_access g ON g.task_id=t.id
            WHERE t.kind='INCIDENT' AND t.status IN ('ASSIGNED','IN_PROGRESS')
                AND d.status='OPEN' AND s.status='FAULT'
            ORDER BY t.created_at DESC LIMIT 50
            """);
        query.Parameters.AddWithValue("seller", sellerId);
        await using var reader = await query.ExecuteReaderAsync(ct);
        var result = new List<TechnicalRepairRow>();
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetDateTime(4))));
        return Results.Ok(result);
    }

    private static async Task<IResult> ApproveAsync(Guid taskId, HttpContext http, NpgsqlDataSource data, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId)) return Results.Forbid();
        await using var query = data.CreateCommand("SELECT flow.approve_technical_repair(@task,@actor)");
        query.Parameters.AddWithValue("task", taskId);
        query.Parameters.AddWithValue("actor", actorId);
        try
        {
            return await query.ExecuteScalarAsync(ct) is DateTime expiry
                ? Results.Ok(new TechnicalRepairApproval(new(expiry)))
                : Results.NotFound();
        }
        catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01")
        {
            return Results.Problem(statusCode: 409, detail: "Ô tủ còn giao dịch hoặc đang có thao tác mở cửa.");
        }
    }

    private static async Task<IResult> IssueAsync(Guid taskId, HttpContext http, NpgsqlDataSource data,
        MaintenanceAccessStore store, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId)) return Results.Forbid();
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var query = new NpgsqlCommand("SELECT kiosk_id,slot_id,seller_id FROM flow.technical_repair_scope(@task,@actor)", connection, transaction);
        query.Parameters.AddWithValue("task", taskId);
        query.Parameters.AddWithValue("actor", actorId);

        MaintenanceAccessBinding binding;
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            binding = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), actorId, "TECHNICIAN", taskId, "REPAIR", "{}");
        }
        var code = await store.IssueAsync(connection, transaction, binding, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(code);
    }

    public static async Task<IResult> ConfirmAsync(MaintenanceAccessBinding binding, Guid challengeId, string code,
        NpgsqlDataSource data, ICapPublisher publisher, MaintenanceAccessStore store, CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        var verification = await store.VerifyAsync(connection, transaction, challengeId, binding.KioskId, code, ct);
        if (verification == MaintenanceAccessVerification.Invalid)
        {
            await outbox.CommitAsync(ct);
            return Results.Problem(statusCode: 400, detail: "Mã xác nhận không hợp lệ.");
        }
        if (verification != MaintenanceAccessVerification.Valid || binding.TaskId is not Guid taskId)
            return Results.NotFound();

        var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
        await using var open = new NpgsqlCommand("SELECT flow.open_technical_repair(@task,@actor,@kiosk,@slot)", connection, transaction);
        open.Parameters.AddWithValue("task", taskId);
        open.Parameters.AddWithValue("actor", binding.ActorId);
        open.Parameters.AddWithValue("kiosk", binding.KioskId);
        open.Parameters.AddWithValue("slot", binding.SlotId);
        try
        {
            if (await open.ExecuteScalarAsync(ct) is not Guid) return Results.NotFound();
            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);
            return Results.Ok(new AcceptedMaintenanceAccess());
        }
        catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01" or "23505")
        {
            return Results.Problem(statusCode: 409, detail: "Ô tủ đang được xử lý. Vui lòng kiểm tra lại ca sửa.");
        }
    }
}
