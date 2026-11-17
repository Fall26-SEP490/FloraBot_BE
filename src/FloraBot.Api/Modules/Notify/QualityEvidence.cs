using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Notify;

public static class QualityEvidence
{
    public static async Task<IResult> Read(Guid complaint, Guid attachment, Guid? actor, Guid? order, string? tracking,
        HttpContext http, NpgsqlDataSource data, CloudinaryMedia media, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await using var command = data.CreateCommand("""
            SELECT e.file_url,e.mime_type,e.size_bytes,e.sha256 FROM flow.quality_evidence_rows(@complaint) e
            WHERE e.id=@attachment AND flow.quality_complaint_visible(@complaint,@actor,@order,@tracking)
            """);
        command.Parameters.AddWithValue("complaint", complaint); command.Parameters.AddWithValue("attachment", attachment);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, (object?)actor ?? DBNull.Value);
        command.Parameters.AddWithValue("order", NpgsqlDbType.Uuid, (object?)order ?? DBNull.Value);
        command.Parameters.AddWithValue("tracking", NpgsqlDbType.Text, (object?)tracking ?? DBNull.Value);
        string url, mime, hash; long size;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
                return Results.Problem(statusCode: 503, detail: "Ảnh cũ chưa có thông tin kiểm tra toàn vẹn. Liên hệ hỗ trợ.");
            url = reader.GetString(0); mime = reader.GetString(1); size = reader.GetInt64(2); hash = reader.GetString(3);
        }
        try
        {
            var bytes = await media.ReadPrivateAsync(url, ct);
            if (bytes.Length != size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash || CloudinaryMedia.ImageType(bytes) != mime)
                throw new MediaUnavailableException();
            return Results.File(bytes, mime, enableRangeProcessing: false);
        }
        catch (Exception ex) when (ex is MediaUnavailableException or HttpRequestException or JsonException or ArgumentException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        { return Results.Problem(statusCode: 503, detail: "Chưa tải được ảnh xử lý. Vui lòng thử lại."); }
    }
}
