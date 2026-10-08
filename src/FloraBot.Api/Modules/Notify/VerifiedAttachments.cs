using Npgsql;

namespace FloraBot.Api.Modules.Notify;

public sealed record AttachmentMetadata(Guid Id, string Url, string MimeType, int SizeBytes, string Sha256);

public static class VerifiedAttachments
{
    public static async Task<List<AttachmentMetadata>> ProductPhotosAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid product, int page, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id,file_url,mime_type,size_bytes,sha256 FROM notify.attachments
            WHERE owner_service='catalog' AND owner_type='flower_product' AND owner_id=@product AND phase='PRODUCT'
            ORDER BY created_at DESC,id DESC LIMIT 21 OFFSET @offset
            """, connection, transaction);
        command.Parameters.AddWithValue("product", product);
        command.Parameters.AddWithValue("offset", (page - 1) * 20);
        var rows = new List<AttachmentMetadata>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), checked((int)reader.GetInt64(3)), reader.GetString(4)));
        return rows;
    }

    public static async Task SetMetadataAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, VerifiedMedia media, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE notify.attachments SET mime_type=@mime,size_bytes=@size,sha256=@hash
            WHERE id=@id AND file_url=@url
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("url", media.Url);
        command.Parameters.AddWithValue("mime", media.MimeType);
        command.Parameters.AddWithValue("size", media.SizeBytes);
        command.Parameters.AddWithValue("hash", media.Sha256);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Attachment metadata could not be recorded.");
    }
}
