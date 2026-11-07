using System.Security.Claims;
using System.Text.Json;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Npgsql;

namespace FloraBot.Api.Modules.Catalog;

public sealed record ProductPhotoResponse(Guid Id, string Url, string MimeType, int SizeBytes, string Sha256,
    bool IsCover = false, int SortOrder = 0);
public sealed record ProductPhotoPage(List<ProductPhotoResponse> Items, bool HasMore);
public sealed record ReorderProductPhotosRequest(List<Guid> PhotoIds);

public static class ProductPhotos
{
    public static void MapProductPhotos(this WebApplication app)
    {
        app.MapGet("/api/me/products/{productId:guid}/photos", async (Guid productId, int? page, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var current = page ?? 1;
            if (current is < 1 or > 100000) return Results.Problem(statusCode: 400, detail: "Trang ảnh không hợp lệ.");
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (!await CheckOwnerAsync(connection, transaction, productId, Guid.Parse(http.User.FindFirstValue("sub")!), ct, editing: false)) return Results.NotFound();
            var rows = await VerifiedAttachments.ProductPhotosAsync(connection, transaction, productId, current, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new ProductPhotoPage(rows.Take(20).Select(x => new ProductPhotoResponse(x.Id, x.Url, x.MimeType, x.SizeBytes, x.Sha256, x.IsCover, x.SortOrder)).ToList(), rows.Count > 20));
        }).RequireAuthorization("Merchant").WithTags("Catalog").Produces<ProductPhotoPage>();

        app.MapPut("/api/me/products/{productId:guid}/photos/order", async (
            Guid productId, ReorderProductPhotosRequest input, HttpContext http,
            NpgsqlDataSource source, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (input.PhotoIds is null || input.PhotoIds.Count is < 1 or > 100 ||
                input.PhotoIds.Distinct().Count() != input.PhotoIds.Count)
                return Results.Problem(statusCode: 400, detail: "Danh sách ảnh không hợp lệ.");
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (!await CheckOwnerAsync(connection, transaction, productId,
                Guid.Parse(http.User.FindFirstValue("sub")!), ct)) return Results.NotFound();
            await using (var check = new NpgsqlCommand("""
                SELECT array_agg(id ORDER BY id) FROM notify.attachments
                WHERE owner_service='catalog' AND owner_type='flower_product'
                  AND owner_id=@product AND phase='PRODUCT' AND NOT is_hidden
                """, connection, transaction))
            {
                check.Parameters.AddWithValue("product", productId);
                var existing = await check.ExecuteScalarAsync(ct) as Guid[] ?? [];
                if (existing.Length != input.PhotoIds.Count || existing.Except(input.PhotoIds).Any())
                    return Results.Problem(statusCode: 409, detail: "Danh sách ảnh đã thay đổi. Tải lại trước khi sắp xếp.");
            }
            await using (var clear = new NpgsqlCommand("""
                UPDATE notify.attachments SET is_cover=false
                WHERE owner_service='catalog' AND owner_type='flower_product'
                  AND owner_id=@product AND phase='PRODUCT' AND NOT is_hidden
                """, connection, transaction))
            {
                clear.Parameters.AddWithValue("product", productId);
                await clear.ExecuteNonQueryAsync(ct);
            }
            for (var position = 0; position < input.PhotoIds.Count; position++)
            {
                await using var order = new NpgsqlCommand("""
                    UPDATE notify.attachments SET sort_order=@position,is_cover=@cover
                    WHERE id=@id AND owner_id=@product AND owner_service='catalog'
                      AND owner_type='flower_product' AND phase='PRODUCT' AND NOT is_hidden
                    """, connection, transaction);
                order.Parameters.AddWithValue("position", position + 1);
                order.Parameters.AddWithValue("cover", position == 0);
                order.Parameters.AddWithValue("id", input.PhotoIds[position]);
                order.Parameters.AddWithValue("product", productId);
                await order.ExecuteNonQueryAsync(ct);
            }
            await AuditAsync(connection, transaction, Guid.Parse(http.User.FindFirstValue("sub")!),
                "PRODUCT_PHOTOS_REORDERED", productId, ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization("Merchant").WithTags("Catalog")
          .Produces(204).ProducesProblem(400).ProducesProblem(409);

        app.MapDelete("/api/me/products/{productId:guid}/photos/{photoId:guid}", async (
            Guid productId, Guid photoId, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actor = Guid.Parse(http.User.FindFirstValue("sub")!);
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (!await CheckOwnerAsync(connection, transaction, productId, actor, ct)) return Results.NotFound();
            await using (var hide = new NpgsqlCommand("""
                UPDATE notify.attachments SET is_hidden=true,is_cover=false
                WHERE id=@id AND owner_id=@product AND owner_service='catalog'
                  AND owner_type='flower_product' AND phase='PRODUCT' AND NOT is_hidden
                """, connection, transaction))
            {
                hide.Parameters.AddWithValue("id", photoId);
                hide.Parameters.AddWithValue("product", productId);
                if (await hide.ExecuteNonQueryAsync(ct) != 1) return Results.NotFound();
            }
            await using (var cover = new NpgsqlCommand("""
                UPDATE notify.attachments SET is_cover=true WHERE id=(
                  SELECT id FROM notify.attachments WHERE owner_service='catalog'
                    AND owner_type='flower_product' AND owner_id=@product
                    AND phase='PRODUCT' AND NOT is_hidden
                  ORDER BY is_cover DESC,sort_order,created_at,id LIMIT 1)
                  AND NOT EXISTS (
                    SELECT 1 FROM notify.attachments WHERE owner_service='catalog'
                      AND owner_type='flower_product' AND owner_id=@product
                      AND phase='PRODUCT' AND NOT is_hidden AND is_cover)
                """, connection, transaction))
            {
                cover.Parameters.AddWithValue("product", productId);
                await cover.ExecuteNonQueryAsync(ct);
            }
            await AuditAsync(connection, transaction, actor, "PRODUCT_PHOTO_HIDDEN", photoId, ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization("Merchant").WithTags("Catalog").Produces(204).Produces(404);

        app.MapPost("/api/me/products/{productId:guid}/photos", UploadAsync)
            .RequireAuthorization("Merchant").WithTags("Catalog")
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

    private static async Task<IResult> UploadAsync(Guid productId, HttpContext http, NpgsqlDataSource source, CloudinaryMedia media, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = Guid.Parse(http.User.FindFirstValue("sub")!);
        await using var connection = await source.OpenConnectionAsync(ct);
        // Check before sending bytes externally, then lock and recheck before committing metadata.
        await using (var before = await connection.BeginTransactionAsync(ct))
        {
            if (!await CheckOwnerAsync(connection, before, productId, actor, ct)) return Results.NotFound();
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
        if (!await CheckOwnerAsync(connection, transaction, productId, actor, ct)) return Results.NotFound();
        await using var attach = new NpgsqlCommand("SELECT flow.add_product_photo(@product,@actor,@url)", connection, transaction);
        attach.Parameters.AddWithValue("product", productId);
        attach.Parameters.AddWithValue("actor", actor);
        attach.Parameters.AddWithValue("url", uploaded.Url);
        var id = (Guid)(await attach.ExecuteScalarAsync(ct))!;
        await VerifiedAttachments.SetMetadataAsync(connection, transaction, id, uploaded, ct);
        bool isCover;
        int sortOrder;
        await using (var layout = new NpgsqlCommand("""
            UPDATE notify.attachments SET
              sort_order=coalesce((SELECT max(sort_order)+1 FROM notify.attachments
                WHERE owner_service='catalog' AND owner_type='flower_product'
                  AND owner_id=@product AND phase='PRODUCT' AND NOT is_hidden AND id<>@id),1),
              is_cover=NOT EXISTS (SELECT 1 FROM notify.attachments
                WHERE owner_service='catalog' AND owner_type='flower_product'
                  AND owner_id=@product AND phase='PRODUCT' AND NOT is_hidden
                  AND is_cover AND id<>@id)
            WHERE id=@id RETURNING is_cover,sort_order
            """, connection, transaction))
        {
            layout.Parameters.AddWithValue("id", id);
            layout.Parameters.AddWithValue("product", productId);
            await using var reader = await layout.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Uploaded photo not found.");
            isCover = reader.GetBoolean(0);
            sortOrder = reader.GetInt32(1);
        }
        await transaction.CommitAsync(ct);
        return Results.Ok(new ProductPhotoResponse(id, uploaded.Url, uploaded.MimeType, uploaded.SizeBytes, uploaded.Sha256, isCover, sortOrder));
    }

    private static async Task<bool> CheckOwnerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid product, Guid actor, CancellationToken ct, bool editing = true)
    {
        await using var command = new NpgsqlCommand("SELECT seller_id FROM catalog.flower_products WHERE id=@product FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("product", product);
        if (await command.ExecuteScalarAsync(ct) is not Guid owner) return false;

        // Verify the actor belongs to the product's owner (or is active admin) to preserve foreign/missing indistinguishability (404)
        await using var userCheck = new NpgsqlCommand("SELECT 1 FROM identity.users WHERE id=@actor AND status='ACTIVE' AND (role='ADMIN' OR (role='SELLER' AND seller_id=@owner))", connection, transaction);
        userCheck.Parameters.AddWithValue("actor", actor);
        userCheck.Parameters.AddWithValue("owner", owner);
        if (await userCheck.ExecuteScalarAsync(ct) is null) return false;

        await using var access = new NpgsqlCommand(editing ? "SELECT flow.check_catalog_editor(@product,@actor)" : "SELECT flow.check_seller_user(@actor,@seller)", connection, transaction);
        access.Parameters.AddWithValue("actor", actor);
        access.Parameters.AddWithValue(editing ? "product" : "seller", editing ? product : owner);
        await access.ExecuteNonQueryAsync(ct);
        return true;
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid actor, string action, Guid entityId, CancellationToken ct)
    {
        await using var audit = new NpgsqlCommand("SELECT flow.audit(@actor,@action,'attachments',@id)", connection, transaction);
        audit.Parameters.AddWithValue("actor", actor);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("id", entityId);
        await audit.ExecuteNonQueryAsync(ct);
    }
}
