using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Modules.Ordering;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Npgsql;

namespace FloraBot.Api.Modules.Notify;

public sealed record EvidenceTicket(string Purpose, string Binding, DateTimeOffset ExpiresAt, VerifiedMedia Media);
public sealed record EvidenceUploadResponse(string Reference, DateTimeOffset ExpiresAt);

public sealed class PrivateEvidence(IDataProtectionProvider protection, TimeProvider clock)
{
    private const string Prefix = "evidence:v1:";
    public static string SellerBinding(string actor, Guid seller, Guid slot) => $"seller:{actor}:{seller}:{slot}";
    public static string AdminBinding(string actor, Guid resource) => $"admin:{actor}:{resource}";
    public static string ReceiptBinding(Guid order, string token) => $"receipt:{order}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))}";

    public EvidenceUploadResponse Issue(string purpose, string binding, VerifiedMedia media)
    {
        var expires = clock.GetUtcNow().AddMinutes(30);
        var ticket = new EvidenceTicket(purpose, binding, expires, media);
        return new(Prefix + protection.CreateProtector("private-evidence-v1").Protect(JsonSerializer.Serialize(ticket)), expires);
    }

    public VerifiedMedia Resolve(string value, string purpose, string binding)
    {
        try
        {
            if (!value.StartsWith(Prefix, StringComparison.Ordinal) || value.Length > 10000) throw new CryptographicException();
            var ticket = JsonSerializer.Deserialize<EvidenceTicket>(protection.CreateProtector("private-evidence-v1").Unprotect(value[Prefix.Length..]));
            if (ticket is null || ticket.Purpose != purpose || ticket.Binding != binding || ticket.ExpiresAt <= clock.GetUtcNow()) throw new CryptographicException();
            return ticket.Media;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        { throw new ArgumentException("Ảnh đã hết hạn hoặc không thuộc hồ sơ này. Vui lòng tải lại ảnh."); }
    }

    public static async Task<bool> LockUnusedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string url, CancellationToken ct)
    {
        // Serialize concurrent uses of one upload reference until its business transaction ends.
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@url,0)); SELECT EXISTS(SELECT 1 FROM notify.attachments WHERE file_url=@url)", connection, transaction);
        command.Parameters.AddWithValue("url", url);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.NextResultAsync(ct);
        await reader.ReadAsync(ct);
        return !reader.GetBoolean(0);
    }

    public static async Task SetFlowMetadataAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, VerifiedMedia media, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("UPDATE notify.attachments SET mime_type=@mime,size_bytes=@size,sha256=@hash WHERE file_url=@url AND phase IN ('RETURN','EVIDENCE','PROOF')", connection, transaction);
        command.Parameters.AddWithValue("url", media.Url); command.Parameters.AddWithValue("mime", media.MimeType);
        command.Parameters.AddWithValue("size", media.SizeBytes); command.Parameters.AddWithValue("hash", media.Sha256);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Evidence attachment could not be recorded.");
    }

    public static void MapPrivateEvidence(WebApplication app)
    {
        DescribeUpload(app.MapPost("/api/admin/evidence/{purpose}/{resourceId:guid}", async (string purpose, Guid resourceId, HttpContext http, NpgsqlDataSource source, CloudinaryMedia media, PrivateEvidence tickets, CancellationToken ct) =>
        {
            if (!await Payment.FinancialEvidence.ExistsAsync(source, purpose, resourceId, ct)) return Results.NotFound();
            return await UploadAsync(http, media, tickets, purpose, AdminBinding(http.User.FindFirstValue("sub")!, resourceId), ct);
        }).RequireAuthorization("Admin").WithMetadata(new RequestSizeLimitAttribute(CloudinaryMedia.MaximumBytes)).Produces<EvidenceUploadResponse>());

        app.MapGet("/api/admin/evidence/{purpose}/{resourceId:guid}", async (string purpose, Guid resourceId, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var owner = purpose switch { "confirm_refund" => "refund", "pay_withdrawal" => "withdrawal", _ => null };
            if (owner is null) return Results.NotFound();
            await using var command = source.CreateCommand("SELECT id,created_at FROM notify.attachments WHERE owner_service='payment' AND owner_type=@owner AND owner_id=@id AND phase='PROOF' ORDER BY created_at,id LIMIT 21");
            command.Parameters.AddWithValue("owner", owner); command.Parameters.AddWithValue("id", resourceId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var items = new List<IncidentPhoto>();
            while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), $"/api/admin/evidence/{reader.GetGuid(0)}", reader.GetDateTime(1)));
            return Results.Ok(new IncidentEvidenceResponse(items.Take(20).ToList(), items.Count > 20));
        }).RequireAuthorization("Admin").Produces<IncidentEvidenceResponse>();

        DescribeUpload(app.MapPost("/api/sellers/{sellerId:guid}/slots/{slotId:guid}/evidence/{purpose}", async (Guid sellerId, Guid slotId, string purpose, HttpContext http, NpgsqlDataSource source, CloudinaryMedia media, PrivateEvidence tickets, CancellationToken ct) =>
        {
            if (purpose is not ("return_to_seller" or "report_device_fault")) return Results.NotFound();
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (await KioskOps.ResourceOwnership.SlotSellerAsync(connection, transaction, slotId, ct) != sellerId) return Results.NotFound();
            await transaction.CommitAsync(ct);
            return await UploadAsync(http, media, tickets, purpose, SellerBinding(http.User.FindFirstValue("sub")!, sellerId, slotId), ct);
        }).RequireAuthorization("Merchant", "SameSeller").WithMetadata(new RequestSizeLimitAttribute(CloudinaryMedia.MaximumBytes)).Produces<EvidenceUploadResponse>());

        DescribeUpload(app.MapPost("/api/receipts/{orderId:guid}/evidence", async (Guid orderId, [FromHeader(Name = "X-Receipt-Token")] string? trackingToken, HttpContext http, NpgsqlDataSource source, CloudinaryMedia media, PrivateEvidence tickets, CancellationToken ct) =>
        {
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var args = new Dictionary<string, JsonElement> { ["p_order"] = JsonSerializer.SerializeToElement(orderId), ["p_tracking"] = JsonSerializer.SerializeToElement(trackingToken) };
            var denied = await ReceiptAccess.CheckAsync(connection, transaction, args, ct);
            if (denied is not null) return denied;
            await transaction.CommitAsync(ct);
            return await UploadAsync(http, media, tickets, "open_dispute", ReceiptBinding(orderId, args["p_tracking"].GetString()!), ct);
        }).AllowAnonymous().RequireRateLimiting("receipt").WithMetadata(new RequestSizeLimitAttribute(CloudinaryMedia.MaximumBytes)).Produces<EvidenceUploadResponse>());

        app.MapGet("/api/admin/evidence/{attachmentId:guid}", async (Guid attachmentId, HttpContext http, NpgsqlDataSource source, CloudinaryMedia media, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await using var command = source.CreateCommand("SELECT file_url,mime_type,size_bytes,sha256 FROM notify.attachments WHERE id=@id AND phase IN ('RETURN','EVIDENCE','PROOF')");
            command.Parameters.AddWithValue("id", attachmentId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            var url = reader.GetString(0); var mime = reader.GetString(1); var size = reader.GetInt64(2); var hash = reader.GetString(3);
            await reader.DisposeAsync();
            try
            {
                var bytes = await media.ReadPrivateAsync(url, ct);
                if (bytes.Length != size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash || CloudinaryMedia.ImageType(bytes) != mime) throw new MediaUnavailableException();
                return Results.File(bytes, mime, enableRangeProcessing: false);
            }
            catch (Exception ex) when (IsProviderFailure(ex, ct) || ex is ArgumentException) { return Unavailable(); }
        }).RequireAuthorization("Admin");
    }

    private static async Task<IResult> UploadAsync(HttpContext http, CloudinaryMedia media, PrivateEvidence tickets, string purpose, string binding, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (http.Request.ContentType is not ("image/png" or "image/jpeg" or "image/webp")) return Results.StatusCode(415);
        if (http.Request.ContentLength > CloudinaryMedia.MaximumBytes) return Results.StatusCode(413);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > CloudinaryMedia.MaximumBytes) return Results.StatusCode(413);
            output.Write(buffer, 0, count);
        }
        try
        {
            var bytes = output.ToArray();
            if (CloudinaryMedia.ImageType(bytes) != http.Request.ContentType) return Results.BadRequest();
            var uploaded = await media.UploadAsync(bytes, ct, authenticated: true);
            return Results.Ok(tickets.Issue(purpose, binding, uploaded));
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, detail: "Chọn ảnh PNG, JPEG hoặc WebP, tối đa 5 MB."); }
        catch (Exception ex) when (IsProviderFailure(ex, ct)) { return Unavailable(); }
    }

    private static bool IsProviderFailure(Exception ex, CancellationToken ct) => ex is MediaUnavailableException or HttpRequestException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested;
    private static IResult Unavailable() => Results.Problem(statusCode: 503, detail: "Kho ảnh chưa sẵn sàng. Ảnh chưa được xác nhận; vui lòng thử lại sau.");

    private static void DescribeUpload(RouteHandlerBuilder endpoint) => endpoint
        .Accepts<byte[]>("image/png", "image/jpeg", "image/webp")
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
        .ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(503)
        .AddOpenApiOperationTransformer((operation, context, ct) =>
        {
            foreach (var content in operation.RequestBody!.Content!.Values)
                content.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
            return Task.CompletedTask;
        });
}
