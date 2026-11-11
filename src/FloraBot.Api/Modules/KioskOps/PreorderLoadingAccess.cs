using System.Security.Claims;
using System.Text.Json;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

internal sealed record PreorderLoadingAccessInput(Guid SlotId);
internal sealed record PreorderLoadingAccessSent(Guid Id, DateTimeOffset ExpiresAt, int ResendAfterSeconds,
    bool RequiresCode = true);
internal sealed record PreorderLoadingBinding(Guid RequestId);

internal static class PreorderLoadingAccess
{
    internal const string Operation = "PREORDER_LOAD";
    private const int ResendSeconds = 30;

    public static void MapPreorderLoadingAccess(this WebApplication app)
    {
        app.MapPost("/api/me/preorders/{requestId:guid}/loading-access",
            (Guid requestId, PreorderLoadingAccessInput input, HttpContext http, NpgsqlDataSource data,
                MaintenanceAccessStore store, ITransactionalEmailSender sender, EmailSettings settings, CancellationToken ct) =>
                IssueAsync(requestId, null, input, http, data, store, sender, settings, ct))
            .RequireAuthorization("Seller").RequireRateLimiting("maintenance-code-issue").WithTags("Ordering")
            .Produces<PreorderLoadingAccessSent>();

        app.MapPost("/api/staff/tasks/{taskId:guid}/preorders/{requestId:guid}/loading-access",
            (Guid taskId, Guid requestId, PreorderLoadingAccessInput input, HttpContext http, NpgsqlDataSource data,
                MaintenanceAccessStore store, ITransactionalEmailSender sender, EmailSettings settings, CancellationToken ct) =>
                IssueAsync(requestId, taskId, input, http, data, store, sender, settings, ct))
            .RequireAuthorization("SellerStaff").RequireRateLimiting("maintenance-code-issue").WithTags("Ordering")
            .Produces<PreorderLoadingAccessSent>();
    }

    private static async Task<IResult> IssueAsync(Guid requestId, Guid? taskId, PreorderLoadingAccessInput input,
        HttpContext http, NpgsqlDataSource data, MaintenanceAccessStore store, ITransactionalEmailSender sender,
        EmailSettings settings, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (requestId == Guid.Empty || input.SlotId == Guid.Empty || taskId == Guid.Empty)
            return Results.BadRequest();
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)
            || !Guid.TryParse(http.User.FindFirstValue("seller_id"), out var seller))
            return Results.Forbid();

        MaintenanceAccessIssued issued;
        string email;
        await using (var connection = await data.OpenConnectionAsync(ct))
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            await using var scope = new NpgsqlCommand("""
                SELECT kiosk_id,actor_role,email
                FROM flow.preorder_load_scope(@request,@slot,@actor,@task)
                WHERE seller_id=@seller
                """, connection, transaction);
            scope.Parameters.AddWithValue("request", requestId);
            scope.Parameters.AddWithValue("slot", input.SlotId);
            scope.Parameters.AddWithValue("actor", actor);
            scope.Parameters.AddWithValue("seller", seller);
            scope.Parameters.AddWithValue("task", NpgsqlDbType.Uuid, (object?)taskId ?? DBNull.Value);
            Guid kiosk;
            string role;
            await using (var reader = await scope.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return Results.NotFound();
                kiosk = reader.GetGuid(0);
                role = reader.GetString(1);
                email = reader.GetString(2);
            }

            if (!EmailSettings.IsAddress(email))
                return Results.Problem(statusCode: 409, detail: "Tài khoản nạp hoa chưa có email hợp lệ.");
            if (settings.Provider == EmailProvider.Disabled)
                return Results.Problem(statusCode: 503, detail: "Dịch vụ gửi email chưa sẵn sàng.");

            await using var accessLock = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))", connection, transaction);
            accessLock.Parameters.AddWithValue("key", $"preorder-load-issue:{actor}:{input.SlotId}");
            await accessLock.ExecuteNonQueryAsync(ct);

            await using var existing = new NpgsqlCommand("""
                SELECT s.challenge_id,s.expires_at
                FROM kiosk_ops.preorder_loading_sessions s
                JOIN kiosk_ops.maintenance_access_challenges c ON c.id=s.challenge_id
                WHERE s.request_id=@request AND s.slot_id=@slot AND c.actor_id=@actor
                  AND c.task_id IS NOT DISTINCT FROM @task AND s.completed_bouquet_id IS NULL
                  AND s.expires_at>clock_timestamp()
                ORDER BY s.expires_at DESC LIMIT 1
                """, connection, transaction);
            existing.Parameters.AddWithValue("request", requestId);
            existing.Parameters.AddWithValue("slot", input.SlotId);
            existing.Parameters.AddWithValue("actor", actor);
            existing.Parameters.AddWithValue("task", NpgsqlDbType.Uuid, (object?)taskId ?? DBNull.Value);
            await using (var reader = await existing.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                    return Results.Ok(new PreorderLoadingAccessSent(reader.GetGuid(0),
                        reader.GetFieldValue<DateTimeOffset>(1), 0, RequiresCode: false));
            }

            await using var throttle = new NpgsqlCommand("""
                SELECT EXISTS(
                    SELECT 1 FROM kiosk_ops.maintenance_access_challenges
                    WHERE actor_id=@actor AND slot_id=@slot AND operation='PREORDER_LOAD'
                      AND created_at>clock_timestamp()-make_interval(secs=>@seconds)
                );
                """, connection, transaction);
            throttle.Parameters.AddWithValue("actor", actor);
            throttle.Parameters.AddWithValue("slot", input.SlotId);
            throttle.Parameters.AddWithValue("seconds", ResendSeconds);
            if (await throttle.ExecuteScalarAsync(ct) is true)
            {
                http.Response.Headers.RetryAfter = ResendSeconds.ToString();
                return Results.Problem(statusCode: 429, detail: "Chờ 30 giây trước khi yêu cầu mã mới.");
            }

            issued = await store.IssueAsync(connection, transaction,
                new(kiosk, input.SlotId, seller, actor, role, taskId, Operation,
                    JsonSerializer.Serialize(new PreorderLoadingBinding(requestId))), ct);
            await transaction.CommitAsync(ct);
        }

        try
        {
            await sender.SendAsync(new TransactionalEmail(email, "FloraBot - Mã OTP nạp hoa đặt trước",
                $"Mã nạp hoa: {issued.Code}\nNhập mã trên màn hình tại đúng kiosk được giao. Mã dùng một lần, hết hạn sau 2 phút."), ct);
        }
        catch (EmailUnavailableException)
        {
            await using var revoke = data.CreateCommand("""
                UPDATE kiosk_ops.maintenance_access_challenges SET consumed_at=clock_timestamp()
                WHERE id=@id AND consumed_at IS NULL
                """);
            revoke.Parameters.AddWithValue("id", issued.Id);
            await revoke.ExecuteNonQueryAsync(CancellationToken.None);
            return Results.Problem(statusCode: 503, detail: "Chưa xác nhận gửi được mã. Vui lòng thử lại sau 30 giây.");
        }

        return Results.Ok(new PreorderLoadingAccessSent(issued.Id, issued.ExpiresAt, ResendSeconds));
    }

    public static async Task<IResult> ConfirmAsync(MaintenanceAccessBinding binding, Guid id, string code,
        NpgsqlDataSource data, MaintenanceAccessStore store, ICapPublisher publisher, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<PreorderLoadingBinding>(binding.Payload);
        if (payload is null || payload.RequestId == Guid.Empty) return Results.NotFound();

        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        var verification = await store.VerifyAsync(connection, transaction, id, binding.KioskId, code, ct);
        if (verification == MaintenanceAccessVerification.Invalid)
        {
            await outbox.CommitAsync(ct);
            return Results.Problem(statusCode: 400, detail: "Mã xác nhận không hợp lệ.");
        }
        if (verification != MaintenanceAccessVerification.Valid) return Results.NotFound();

        var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
        await using var open = new NpgsqlCommand("SELECT flow.open_preorder_loading(@challenge,@request)", connection, transaction);
        open.Parameters.AddWithValue("challenge", id);
        open.Parameters.AddWithValue("request", payload.RequestId);
        try
        {
            if (await open.ExecuteScalarAsync(ct) is not Guid) return Results.NotFound();
            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);
            return Results.Ok(new AcceptedMaintenanceAccess());
        }
        catch (PostgresException ex) when (ex.SqlState is "22023" or "40001" or "40P01")
        {
            return Results.Problem(statusCode: 409, detail: "Chưa xác nhận được ô trống hoặc ô đang được xử lý.");
        }
    }
}
