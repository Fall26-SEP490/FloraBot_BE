using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace FloraBot.Api.Modules.Notify;

public sealed record StaffEvidenceInput(Guid Id, int Version, string Reference);
public sealed record StaffEvidenceItem(Guid Id, DateTime CreatedAt);
public sealed record StaffEvidencePage(List<StaffEvidenceItem> Items, int Page, bool HasMore);
public static class StaffEvidence
{
    private static Guid Actor(HttpContext http) => Guid.Parse(http.User.FindFirstValue("sub")!);
    private static string Binding(Guid actor, Guid task, int version) => $"staff:{actor}:{task}:{version}";
    public static void MapStaffEvidence(this WebApplication app)
    {
        PrivateEvidence.DescribeUpload(app.MapPost("/api/staff/tasks/{taskId:guid}/evidence/upload", async (Guid taskId, HttpContext http, NpgsqlDataSource data, CloudinaryMedia media, PrivateEvidence tickets, CancellationToken ct) =>
        {
            await using var connection = await data.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var scope = await StaffTaskAccess.Lock(connection, transaction, taskId, http, ct);
            if (scope is null) return Results.NotFound();
            if (scope.Status != "IN_PROGRESS") return Results.Problem(statusCode: 409, detail: "Chỉ tải ảnh khi công việc đang thực hiện.");
            await transaction.CommitAsync(ct);
            return await PrivateEvidence.UploadAsync(http, media, tickets, "staff_task", Binding(Actor(http), taskId, scope.Version), ct);
        }).RequireAuthorization("Staff").WithMetadata(new RequestSizeLimitAttribute(CloudinaryMedia.MaximumBytes)).Produces<EvidenceUploadResponse>());

        app.MapPost("/api/staff/tasks/{taskId:guid}/evidence", async (Guid taskId, StaffEvidenceInput input, HttpContext http, NpgsqlDataSource data, PrivateEvidence tickets, CancellationToken ct) =>
        {
            if (input.Id == Guid.Empty || input.Version < 1 || string.IsNullOrWhiteSpace(input.Reference)) return Results.BadRequest();
            var actor = Actor(http);
            VerifiedMedia verified;
            try { verified = tickets.Resolve(input.Reference, "staff_task", Binding(actor, taskId, input.Version)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
            await using var connection = await data.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var scope = await StaffTaskAccess.Lock(connection, transaction, taskId, http, ct);
            if (scope is null) return Results.NotFound();
            // Serialize retries even when the same attachment ID targets different tasks.
            await using (var identityLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))", connection, transaction))
            {
                identityLock.Parameters.AddWithValue("key", "staff-evidence:" + input.Id);
                await identityLock.ExecuteNonQueryAsync(ct);
            }
            await using (var existing = new NpgsqlCommand("SELECT owner_id,uploaded_by,file_url,created_at,owner_service,owner_type,phase::text FROM notify.attachments WHERE id=@id", connection, transaction))
            {
                existing.Parameters.AddWithValue("id", input.Id);
                await using var reader = await existing.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct)) return reader.GetGuid(0) == taskId && !reader.IsDBNull(1) && reader.GetGuid(1) == actor && reader.GetString(2) == verified.Url
                    && reader.GetString(4) == "kiosk_ops" && reader.GetString(5) == "staff_task" && reader.GetString(6) == "EVIDENCE"
                    ? Results.Ok(new StaffEvidenceItem(input.Id, reader.GetDateTime(3))) : Results.Conflict();
            }
            if (scope.Status != "IN_PROGRESS" || scope.Version != input.Version) return Results.Problem(statusCode: 409, detail: "Công việc đã thay đổi. Tải lại công việc và tải ảnh mới trước khi gửi.");
            if (!await PrivateEvidence.LockUnusedAsync(connection, transaction, verified.Url, ct)) return Results.Problem(statusCode: 409, detail: "Ảnh đã được gắn vào hồ sơ. Tải lại danh sách để kiểm tra.");
            await using var insert = new NpgsqlCommand("""
                INSERT INTO notify.attachments(id,owner_service,owner_type,owner_id,file_url,mime_type,size_bytes,sha256,phase,uploaded_by)
                VALUES(@id,'kiosk_ops','staff_task',@task,@url,@mime,@size,@hash,'EVIDENCE',@actor) RETURNING created_at
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", input.Id); insert.Parameters.AddWithValue("task", taskId); insert.Parameters.AddWithValue("url", verified.Url);
            insert.Parameters.AddWithValue("mime", verified.MimeType); insert.Parameters.AddWithValue("size", verified.SizeBytes); insert.Parameters.AddWithValue("hash", verified.Sha256); insert.Parameters.AddWithValue("actor", actor);
            DateTime created;
            try { created = (DateTime)(await insert.ExecuteScalarAsync(ct))!; }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Results.Conflict(); }
            await using var audit = new NpgsqlCommand("SELECT flow.audit(@actor,'STAFF_TASK_EVIDENCE_ADDED','staff_tasks',@task,jsonb_build_object('attachment',@id))", connection, transaction);
            audit.Parameters.AddWithValue("actor", actor); audit.Parameters.AddWithValue("task", taskId); audit.Parameters.AddWithValue("id", input.Id); await audit.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new StaffEvidenceItem(input.Id, created));
        }).RequireAuthorization("Staff").Produces<StaffEvidenceItem>();

        foreach (var prefix in new[] { "/api/staff/tasks", "/api/admin/staff-tasks", "/api/operations/staff-tasks", "/api/sellers/{sellerId:guid}/staff-tasks" })
        {
            var policy = prefix.Contains("/admin/") ? "Admin" :
                         prefix.Contains("/operations/") ? "OperationsManager" :
                         prefix.Contains("/sellers/") ? "Merchant" : "Staff";
            if (prefix.Contains("/sellers/"))
            {
                app.MapGet(prefix + "/{taskId:guid}/evidence", List).RequireAuthorization(policy, "SameSeller").Produces<StaffEvidencePage>();
                app.MapGet(prefix + "/{taskId:guid}/evidence/{attachmentId:guid}", Read).RequireAuthorization(policy, "SameSeller");
            }
            else
            {
                app.MapGet(prefix + "/{taskId:guid}/evidence", List).RequireAuthorization(policy).Produces<StaffEvidencePage>();
                app.MapGet(prefix + "/{taskId:guid}/evidence/{attachmentId:guid}", Read).RequireAuthorization(policy);
            }
        }
    }
    private static async Task<IResult> List(Guid taskId, int? page, Guid? attachmentId, HttpContext http, NpgsqlDataSource data, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var number = page ?? 1; if (number is < 1 or > 100000) return Results.BadRequest();
        await using var connection = await data.OpenConnectionAsync(ct); await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await StaffTaskAccess.Lock(connection, transaction, taskId, http, ct, readOnly: true) is null) return Results.NotFound();
        await using var command = new NpgsqlCommand("SELECT id,created_at FROM notify.attachments WHERE owner_service='kiosk_ops' AND owner_type='staff_task' AND owner_id=@id AND phase='EVIDENCE' AND (@attachment IS NULL OR id=@attachment) ORDER BY created_at DESC,id DESC LIMIT 26 OFFSET @offset", connection, transaction);
        command.Parameters.Add("attachment", NpgsqlTypes.NpgsqlDbType.Uuid).Value = (object?)attachmentId ?? DBNull.Value;
        command.Parameters.AddWithValue("id", taskId); command.Parameters.AddWithValue("offset", (number - 1) * 25);
        await using var reader = await command.ExecuteReaderAsync(ct); var items = new List<StaffEvidenceItem>();
        while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetDateTime(1)));
        return Results.Ok(new StaffEvidencePage(items.Take(25).ToList(), number, items.Count > 25));
    }
    private static async Task<IResult> Read(Guid taskId, Guid attachmentId, HttpContext http, NpgsqlDataSource data, CloudinaryMedia media, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store"; http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await using var connection = await data.OpenConnectionAsync(ct); await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await StaffTaskAccess.Lock(connection, transaction, taskId, http, ct, readOnly: true) is null) return Results.NotFound();
        await using var command = new NpgsqlCommand("SELECT file_url,mime_type,size_bytes,sha256 FROM notify.attachments WHERE id=@id AND owner_service='kiosk_ops' AND owner_type='staff_task' AND owner_id=@task AND phase='EVIDENCE'", connection, transaction);
        command.Parameters.AddWithValue("id", attachmentId); command.Parameters.AddWithValue("task", taskId);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return Results.NotFound();
        var url = reader.GetString(0); var mime = reader.GetString(1); var size = reader.GetInt64(2); var hash = reader.GetString(3);
        await reader.DisposeAsync(); await transaction.CommitAsync(ct);
        try
        {
            var bytes = await media.ReadPrivateAsync(url, ct);
            if (bytes.Length != size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash || CloudinaryMedia.ImageType(bytes) != mime) throw new MediaUnavailableException();
            return Results.File(bytes, mime, enableRangeProcessing: false);
        }
        catch (Exception ex) when (ex is MediaUnavailableException or HttpRequestException or JsonException or ArgumentException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Results.Problem(statusCode: 503, detail: "Chưa đọc được ảnh minh chứng. Vui lòng thử lại sau.");
        }
    }
}
