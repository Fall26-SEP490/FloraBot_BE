using System.Security.Cryptography;
using System.Text.Json;
using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Modules.Notify;

public sealed record IncidentPhoto(Guid Id, string Url, DateTime CreatedAt);
public sealed record IncidentEvidenceResponse(List<IncidentPhoto> Items, bool HasMore);
public sealed record AdminIncidentEvidenceItem(Guid Id, string Url, DateTime CreatedAt, string Source, Guid? TaskId);
public sealed record AdminIncidentEvidencePage(List<AdminIncidentEvidenceItem> Items, int Page, bool HasMore);

public static class IncidentEvidence
{
    private const int PageSize = 20;

    public static async Task<IncidentEvidenceResponse> ReadAsync(FloraDbContext db, Guid incidentId, CancellationToken ct)
    {
        var rows = await db.Attachments.AsNoTracking()
            .Where(x => x.OwnerService == "ordering" && x.OwnerType == "dispute" && x.OwnerId == incidentId && x.Phase == "EVIDENCE")
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new IncidentPhoto(x.Id, x.FileUrl, x.CreatedAt)).Take(21).ToListAsync(ct);
        return new(rows.Take(20).Select(row => row.Url.Contains("/image/authenticated/", StringComparison.Ordinal)
            ? row with { Url = $"/api/admin/evidence/{row.Id}" } : row).ToList(), rows.Count > 20);
    }

    public static async Task<AdminIncidentEvidencePage> ReadForAdminIncidentAsync(
        NpgsqlDataSource data, Guid incidentId, int page, CancellationToken ct)
    {
        var offset = (long)(page - 1) * PageSize;
        await using var command = data.CreateCommand("""
            WITH evidence AS (
                SELECT a.id, a.file_url, a.created_at, 'INCIDENT'::text AS source, NULL::uuid AS task_id
                FROM notify.attachments a
                WHERE a.owner_service = 'ordering' AND a.owner_type = 'dispute'
                  AND a.owner_id = @incident AND a.phase = 'EVIDENCE'

                UNION ALL

                SELECT a.id, a.file_url, a.created_at, 'STAFF_TASK'::text AS source, t.id AS task_id
                FROM kiosk_ops.staff_tasks t
                JOIN notify.attachments a
                  ON a.owner_service = 'kiosk_ops' AND a.owner_type = 'staff_task'
                 AND a.owner_id = t.id AND a.phase = 'EVIDENCE'
                WHERE t.incident_id = @incident AND t.kind = 'INCIDENT'
            ), deduplicated AS (
                SELECT DISTINCT ON (id) id, file_url, created_at, source, task_id
                FROM evidence
                ORDER BY id, created_at, source
            )
            SELECT id, file_url, created_at, source, task_id
            FROM deduplicated
            ORDER BY created_at, id
            LIMIT @limit OFFSET @offset
            """);
        command.Parameters.AddWithValue("incident", incidentId);
        command.Parameters.AddWithValue("limit", PageSize + 1);
        command.Parameters.AddWithValue("offset", offset);

        var rows = new List<(Guid Id, string FileUrl, DateTime CreatedAt, string Source, Guid? TaskId)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetDateTime(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4)));
        }

        var items = rows.Take(PageSize).Select(row => new AdminIncidentEvidenceItem(
            row.Id,
            row.Source == "STAFF_TASK"
                ? $"/api/admin/incidents/{incidentId}/evidence/{row.Id}"
                : row.FileUrl.Contains("/image/authenticated/", StringComparison.Ordinal)
                    ? $"/api/admin/evidence/{row.Id}"
                    : row.FileUrl,
            row.CreatedAt,
            row.Source,
            row.TaskId)).ToList();

        return new(items, page, rows.Count > PageSize);
    }

    public static void MapAdminIncidentTaskEvidenceRead(this WebApplication app)
    {
        app.MapGet("/api/admin/incidents/{incidentId:guid}/evidence/{attachmentId:guid}", ReadAdminIncidentTaskEvidenceAsync)
            .RequireAuthorization("Admin")
            .WithTags("Incidents")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithDescription("Reads private evidence only when the attachment belongs to an INCIDENT staff task linked to this incident.");
    }

    private static async Task<IResult> ReadAdminIncidentTaskEvidenceAsync(
        Guid incidentId, Guid attachmentId, HttpContext http, NpgsqlDataSource data, CloudinaryMedia media, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";

        await using var command = data.CreateCommand("""
            SELECT a.file_url, a.mime_type, a.size_bytes, a.sha256
            FROM ordering.disputes d
            JOIN kiosk_ops.staff_tasks t
              ON t.incident_id = d.id AND t.kind = 'INCIDENT'
            JOIN notify.attachments a
              ON a.owner_service = 'kiosk_ops' AND a.owner_type = 'staff_task'
             AND a.owner_id = t.id AND a.phase = 'EVIDENCE'
            WHERE d.id = @incident
              AND d.kind IN ('DEVICE_FAULT', 'DISPENSE_FAILED')
              AND a.id = @attachment
            """);
        command.Parameters.AddWithValue("incident", incidentId);
        command.Parameters.AddWithValue("attachment", attachmentId);

        string url;
        string mime;
        long size;
        string hash;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            url = reader.GetString(0);
            mime = reader.GetString(1);
            size = reader.GetInt64(2);
            hash = reader.GetString(3);
        }

        try
        {
            var bytes = await media.ReadPrivateAsync(url, ct);
            if (bytes.Length != size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash || CloudinaryMedia.ImageType(bytes) != mime)
                throw new MediaUnavailableException();
            return Results.File(bytes, mime, enableRangeProcessing: false);
        }
        catch (Exception ex) when (ex is MediaUnavailableException or HttpRequestException or JsonException or ArgumentException ||
                                   ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Results.Problem(statusCode: 503, detail: "Chưa đọc được ảnh minh chứng. Vui lòng thử lại sau.");
        }
    }
}
