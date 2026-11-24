using System.Security.Claims;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.ReadModels;

public sealed record FlowerReview(Guid Id, short Stars, string? Comment, string? PhotoUrl, DateTime CreatedAt,
    string ReviewerName);
public sealed record FlowerReviewPage(decimal? ProductAverage, long ProductCount, decimal? ShopAverage,
    long ShopCount, List<FlowerReview> Items, bool HasMore);
public sealed record ShopFlowerReview(Guid Id, Guid ProductId, string ProductName, short Stars,
    string? Comment, string? PhotoUrl, DateTime CreatedAt, string ReviewerName);
public sealed record ShopFlowerReviewPage(Guid SellerId, string ShopName, decimal? ShopAverage,
    long ShopCount, List<ShopFlowerReview> Items, int Page, bool HasMore);
public sealed record FlowerReviewEligibility(bool Eligible);
public sealed record FlowerReviewInput(int Stars, string? Comment, string? PhotoBase64);
public sealed record FlowerReviewCreated(Guid Id);

public static class FlowerReviews
{
    private const int PageSize = 12;
    private const int MaxImage = 2 * 1024 * 1024;
    private const int MaxBase64 = (MaxImage + 2) / 3 * 4;

    private const string EligibleOrder = """
        SELECT o.id
        FROM ordering.orders o
        JOIN identity.users u ON u.id = o.customer_id AND u.status = 'ACTIVE' AND u.role = 'CUSTOMER'
        JOIN catalog.flower_products p ON p.id = @product AND p.seller_id = o.seller_id
        WHERE o.customer_id = @customer AND o.status = 'COMPLETED'
          AND EXISTS (
            SELECT 1 FROM ordering.web_requests w
            WHERE w.order_id = o.id AND w.customer_id = o.customer_id
              AND w.seller_id = o.seller_id AND w.product_id = p.id AND w.state = 'ORDERED'
          )
        ORDER BY o.created_at DESC, o.id
        LIMIT 1
        """;

    public static void MapFlowerReviews(this WebApplication app)
    {
        var products = app.MapGroup("/api/shop/products/{productId:guid}").WithTags("Flower reviews");
        products.MapGet("/reviews", ReadAsync).AllowAnonymous().Produces<FlowerReviewPage>();
        products.MapGet("/review-eligibility", EligibilityAsync).RequireAuthorization("PreorderCustomer")
            .Produces<FlowerReviewEligibility>();
        products.MapPost("/reviews", CreateAsync).RequireAuthorization("PreorderCustomer")
            .RequireRateLimiting("flower-review")
            .WithMetadata(new RequestSizeLimitAttribute(MaxBase64 + 16 * 1024))
            .Produces<FlowerReviewCreated>(201).ProducesProblem(400).ProducesProblem(409);
        products.MapGet("/reviews/{reviewId:guid}/photo", PhotoAsync).AllowAnonymous()
            .Produces(200, contentType: "image/jpeg", additionalContentTypes: ["image/png", "image/webp"]);
        app.MapGet("/api/shop/sellers/{sellerId:guid}/reviews", ReadShopAsync)
            .WithTags("Flower reviews").AllowAnonymous()
            .Produces<ShopFlowerReviewPage>().Produces(400).Produces(404);
    }

    private static async Task<IResult> ReadAsync(Guid productId, int? page, NpgsqlDataSource source,
        HttpContext http, CancellationToken ct)
    {
        var number = page ?? 1;
        if (number is < 1 or > 10000) return Results.BadRequest();

        http.Response.Headers.CacheControl = "no-store";
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using var totals = new NpgsqlCommand("""
            SELECT avg(r.stars) FILTER (WHERE r.product_id = p.id),
                   count(r.id) FILTER (WHERE r.product_id = p.id),
                   avg(r.stars), count(r.id)
            FROM catalog.flower_products p
            JOIN catalog.flower_products shop_product ON shop_product.seller_id = p.seller_id
            LEFT JOIN catalog.flower_product_reviews r ON r.product_id = shop_product.id
            WHERE p.id = @product
            GROUP BY p.id
            """, connection, transaction);
        totals.Parameters.AddWithValue("product", productId);

        decimal? productAverage;
        long productCount;
        decimal? shopAverage;
        long shopCount;
        await using (var reader = await totals.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            productAverage = reader.IsDBNull(0) ? null : reader.GetDecimal(0);
            productCount = reader.GetInt64(1);
            shopAverage = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
            shopCount = reader.GetInt64(3);
        }

        await using var list = new NpgsqlCommand("""
            SELECT r.id, r.stars, r.comment, r.photo_type IS NOT NULL, r.created_at,
                   COALESCE(NULLIF(btrim(u.full_name), ''), 'Khách hàng')
            FROM catalog.flower_product_reviews r
            LEFT JOIN identity.users u ON u.id = r.customer_id
            WHERE r.product_id = @product
            ORDER BY r.created_at DESC, r.id DESC
            LIMIT @limit OFFSET @offset
            """, connection, transaction);
        list.Parameters.AddWithValue("product", productId);
        list.Parameters.AddWithValue("limit", PageSize + 1);
        list.Parameters.AddWithValue("offset", (number - 1) * PageSize);
        var items = new List<FlowerReview>();
        await using (var reader = await list.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                items.Add(new(id, reader.GetInt16(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetBoolean(3) ? PhotoPath(productId, id) : null,
                    reader.GetDateTime(4), reader.GetString(5)));
            }
        }
        await transaction.CommitAsync(ct);
        return Results.Ok(new FlowerReviewPage(productAverage, productCount, shopAverage, shopCount,
            items.Take(PageSize).ToList(), items.Count > PageSize));
    }

    private static async Task<IResult> ReadShopAsync(Guid sellerId, int? page, NpgsqlDataSource source,
        HttpContext http, CancellationToken ct)
    {
        var number = page ?? 1;
        if (number is < 1 or > 10000) return Results.BadRequest();

        http.Response.Headers.CacheControl = "no-store";
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using var totals = new NpgsqlCommand("""
            SELECT s.shop_name, avg(r.stars), count(r.id)
            FROM identity.sellers s
            LEFT JOIN catalog.flower_products p ON p.seller_id = s.id
            LEFT JOIN catalog.flower_product_reviews r ON r.product_id = p.id
            WHERE s.id = @seller AND s.status = 'ACTIVE'
            GROUP BY s.id, s.shop_name
            """, connection, transaction);
        totals.Parameters.AddWithValue("seller", sellerId);

        string shopName;
        decimal? shopAverage;
        long shopCount;
        await using (var reader = await totals.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            shopName = reader.GetString(0);
            shopAverage = reader.IsDBNull(1) ? null : reader.GetDecimal(1);
            shopCount = reader.GetInt64(2);
        }

        await using var list = new NpgsqlCommand("""
            SELECT r.id, p.id, p.name, r.stars, r.comment, r.photo_type IS NOT NULL, r.created_at,
                   COALESCE(NULLIF(btrim(u.full_name), ''), 'Khách hàng')
            FROM catalog.flower_products p
            JOIN catalog.flower_product_reviews r ON r.product_id = p.id
            LEFT JOIN identity.users u ON u.id = r.customer_id
            WHERE p.seller_id = @seller
            ORDER BY r.created_at DESC, r.id DESC
            LIMIT @limit OFFSET @offset
            """, connection, transaction);
        list.Parameters.AddWithValue("seller", sellerId);
        list.Parameters.AddWithValue("limit", PageSize + 1);
        list.Parameters.AddWithValue("offset", (number - 1) * PageSize);
        var items = new List<ShopFlowerReview>();
        await using (var reader = await list.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                var productId = reader.GetGuid(1);
                items.Add(new(id, productId, reader.GetString(2), reader.GetInt16(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetBoolean(5) ? PhotoPath(productId, id) : null,
                    reader.GetDateTime(6), reader.GetString(7)));
            }
        }
        await transaction.CommitAsync(ct);
        return Results.Ok(new ShopFlowerReviewPage(sellerId, shopName, shopAverage, shopCount,
            items.Take(PageSize).ToList(), number, items.Count > PageSize));
    }

    private static string PhotoPath(Guid productId, Guid reviewId) =>
        $"/api/shop/products/{productId}/reviews/{reviewId}/photo";

    private static async Task<IResult> EligibilityAsync(Guid productId, NpgsqlDataSource source,
        HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var customer)) return Results.Unauthorized();
        await using var command = source.CreateCommand($"""
            SELECT EXISTS ({EligibleOrder}) AND NOT EXISTS (
                SELECT 1 FROM catalog.flower_product_reviews WHERE product_id = @product AND customer_id = @customer
            )
            """);
        command.Parameters.AddWithValue("product", productId);
        command.Parameters.AddWithValue("customer", customer);
        return Results.Ok(new FlowerReviewEligibility(await command.ExecuteScalarAsync(ct) is true));
    }

    private static async Task<IResult> CreateAsync(Guid productId, FlowerReviewInput input,
        NpgsqlDataSource source, HttpContext http, CancellationToken ct)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var customer)) return Results.Unauthorized();
        var comment = input.Comment?.Trim();
        if (input.Stars is < 1 or > 5 || comment?.Length > 1000 || comment?.Contains('\0') == true)
            return Results.Problem("Chọn từ 1 đến 5 sao và nhập tối đa 1.000 ký tự.", statusCode: 400);

        byte[]? photo = null;
        string? photoType = null;
        if (input.PhotoBase64 is { } value)
        {
            if (value.Length > MaxBase64) return Results.Problem("Ảnh tối đa 2 MB.", statusCode: 400);
            try
            {
                photo = Convert.FromBase64String(value);
                if (photo.Length > MaxImage) return Results.Problem("Ảnh tối đa 2 MB.", statusCode: 400);
                photoType = CloudinaryMedia.ImageType(photo);
            }
            catch (Exception error) when (error is FormatException or ArgumentException)
            {
                return Results.Problem("Chọn ảnh JPEG, PNG hoặc WebP hợp lệ.", statusCode: 400);
            }
        }

        await using var command = source.CreateCommand($"""
            INSERT INTO catalog.flower_product_reviews(product_id, order_id, customer_id, stars, comment, photo, photo_type)
            SELECT @product, eligible.id, @customer, @stars, @comment, @photo, @photo_type
            FROM ({EligibleOrder}) eligible
            ON CONFLICT (product_id, customer_id) DO NOTHING
            RETURNING id
            """);
        command.Parameters.AddWithValue("product", productId);
        command.Parameters.AddWithValue("customer", customer);
        command.Parameters.AddWithValue("stars", (short)input.Stars);
        command.Parameters.AddWithValue("comment", NpgsqlDbType.Text, string.IsNullOrEmpty(comment) ? DBNull.Value : comment);
        command.Parameters.AddWithValue("photo", NpgsqlDbType.Bytea, (object?)photo ?? DBNull.Value);
        command.Parameters.AddWithValue("photo_type", NpgsqlDbType.Text, (object?)photoType ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(ct) is not Guid id)
            return Results.Problem("Bạn có thể đánh giá một lần sau khi nhận mẫu hoa đã đặt trước.", statusCode: 409);

        return Results.Json(new FlowerReviewCreated(id), statusCode: 201);
    }

    private static async Task<IResult> PhotoAsync(Guid productId, Guid reviewId, NpgsqlDataSource source,
        HttpContext http, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT photo, photo_type FROM catalog.flower_product_reviews
            WHERE product_id = @product AND id = @id AND photo IS NOT NULL
            """);
        command.Parameters.AddWithValue("product", productId);
        command.Parameters.AddWithValue("id", reviewId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return Results.NotFound();
        http.Response.Headers.CacheControl = "public,max-age=3600";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(reader.GetFieldValue<byte[]>(0), reader.GetString(1));
    }
}
