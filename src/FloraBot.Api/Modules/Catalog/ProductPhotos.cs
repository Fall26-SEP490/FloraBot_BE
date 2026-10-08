using System.Security.Claims;
using System.Text.Json;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Npgsql;

namespace FloraBot.Api.Modules.Catalog;

public sealed record ProductPhotoResponse(Guid Id, string Url, string MimeType, int SizeBytes, string Sha256);
public sealed record ProductPhotoPage(List<ProductPhotoResponse> Items, bool HasMore);

public static class ProductPhotos
{
    public static void MapProductPhotos(this WebApplication app)
    {
        app.MapGet("/api/sellers/{sellerId:guid}/products/{productId:guid}/photos", async (Guid sellerId, Guid productId, int? page, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var current = page ?? 1;
            if (current is < 1 or > 100000) return Results.Problem(statusCode: 400, detail: "Trang ảnh không hợp lệ.");
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (!await CheckOwnerAsync(connection, transaction, productId, sellerId, Guid.Parse(http.User.FindFirstValue("sub")!), ct, editing: false)) return Results.NotFound();
            var rows = await VerifiedAttachments.ProductPhotosAsync(connection, transaction, productId, current, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new ProductPhotoPage(rows.Take(20).Select(x => new ProductPhotoResponse(x.Id, x.Url, x.MimeType, x.SizeBytes, x.Sha256)).ToList(), rows.Count > 20));
        }).RequireAuthorization("Merchant", "SameSeller").WithTags("Catalog").Produces<ProductPhotoPage>();

        app.MapPost("/api/sellers/{sellerId:guid}/products/{productId:guid}/photos", UploadAsync)
            .RequireAuthorization("Merchant", "SameSeller").WithTags("Catalog")
            .WithMetadata(new RequestSizeLimitAttribute(CloudinaryMedia.MaximumBytes))
            .Accepts<byte[]>("image/png", "image/jpeg", "image/webp")
            .AddOpenApiOperationTransformer((operation, context, cancellationToken) =>
            {
                foreach (var content in operation.RequestBody!.Content!.Values)
                    content.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
                return Task.CompletedTask;
            })
            .Produces<ProductPhotoResponse>().ProducesProblem(400).ProducesProblem(413).ProducesProblem(503);
    }

    private static async Task<IResult> UploadAsync(Guid sellerId, Guid productId, HttpContext http, NpgsqlDataSource source, CloudinaryMedia media, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = Guid.Parse(http.User.FindFirstValue("sub")!);
        await using var connection = await source.OpenConnectionAsync(ct);
        // Check before sending bytes externally, then lock and recheck before committing metadata.
        await using (var before = await connection.BeginTransactionAsync(ct))
        {
            if (!await CheckOwnerAsync(connection, before, productId, sellerId, actor, ct)) return Results.NotFound();
            await before.CommitAsync(ct);
        }
        if (http.Request.ContentType is not ("image/png" or "image/jpeg" or "image/webp"))
            return Results.Problem(statusCode: 415, detail: "Chọn ảnh PNG, JPEG hoặc WebP.");
        if (http.Request.ContentLength > CloudinaryMedia.MaximumBytes) return Results.StatusCode(413);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (data.Length + count > CloudinaryMedia.MaximumBytes) return Results.StatusCode(413);
            data.Write(buffer, 0, count);
        }
        VerifiedMedia uploaded;
        try
        {
            var bytes = data.ToArray();
            if (CloudinaryMedia.ImageType(bytes) != http.Request.ContentType)
                return Results.Problem(statusCode: 400, detail: "Loại ảnh không khớp nội dung tệp.");
            uploaded = await media.UploadAsync(bytes, ct);
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, detail: "Chọn ảnh PNG, JPEG hoặc WebP, tối đa 5 MB."); }
        catch (Exception ex) when (ex is MediaUnavailableException or HttpRequestException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Results.Problem(statusCode: 503, detail: "Chưa xác minh được ảnh đã lưu. Liên hệ đội ngũ để kiểm tra trước khi tải lại.");
        }
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await CheckOwnerAsync(connection, transaction, productId, sellerId, actor, ct)) return Results.NotFound();
        await using var attach = new NpgsqlCommand("SELECT flow.add_product_photo(@product,@actor,@url)", connection, transaction);
        attach.Parameters.AddWithValue("product", productId);
        attach.Parameters.AddWithValue("actor", actor);
        attach.Parameters.AddWithValue("url", uploaded.Url);
        var id = (Guid)(await attach.ExecuteScalarAsync(ct))!;
        await VerifiedAttachments.SetMetadataAsync(connection, transaction, id, uploaded, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new ProductPhotoResponse(id, uploaded.Url, uploaded.MimeType, uploaded.SizeBytes, uploaded.Sha256));
    }

    private static async Task<bool> CheckOwnerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid product, Guid seller, Guid actor, CancellationToken ct, bool editing = true)
    {
        await using var command = new NpgsqlCommand("SELECT seller_id FROM catalog.flower_products WHERE id=@product FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("product", product);
        if (await command.ExecuteScalarAsync(ct) is not Guid owner || owner != seller) return false;
        await using var access = new NpgsqlCommand(editing ? "SELECT flow.check_catalog_editor(@product,@actor)" : "SELECT flow.check_seller_user(@actor,@seller)", connection, transaction);
        access.Parameters.AddWithValue("actor", actor);
        access.Parameters.AddWithValue(editing ? "product" : "seller", editing ? product : seller);
        await access.ExecuteNonQueryAsync(ct);
        return true;
    }
}
