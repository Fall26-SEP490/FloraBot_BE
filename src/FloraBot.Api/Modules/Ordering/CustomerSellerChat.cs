using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Realtime;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Ordering;

public sealed record OpenCustomerSellerChatInput(Guid SellerId, Guid? ProductId = null);
public sealed record CustomerSellerChatItem(
    Guid Id,
    string OtherPartyName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt,
    string? LastMessage,
    bool LastMessageHasImage,
    string? ProductContext);
public sealed record CustomerSellerChatPage(List<CustomerSellerChatItem> Items, int Page, bool HasMore);
public sealed record CustomerSellerChatMessageInput(string? Content, string? ImageReference = null);
public sealed record CustomerSellerChatMessage(
    Guid Id,
    string SenderName,
    bool IsMine,
    string? Content,
    DateTimeOffset CreatedAt,
    string? ImageUrl);
public sealed record CustomerSellerChatMessagePage(List<CustomerSellerChatMessage> Items, int Page, bool HasMore);

public static partial class CustomerSellerChatEndpoints
{
    private const string UploadPurpose = "customer_seller_chat";
    private const int PageSize = 50;

    public static void MapCustomerSellerChat(this WebApplication app)
    {
        app.MapPost("/api/member/chats/open", OpenAsync)
            .WithTags("Customer Seller Chat")
            .WithSummary("Mở hoặc tiếp tục cuộc trò chuyện với một shop")
            .WithDescription("Mỗi customer chỉ có một cuộc trò chuyện chung với mỗi shop. Có thể truyền ProductId để lưu ngữ cảnh bó hoa đang xem.")
            .RequireAuthorization("PreorderCustomer")
            .RequireRateLimiting("chat")
            .Produces<CustomerSellerChatItem>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(429);

        app.MapGet("/api/member/chats", (int? page, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return ListAsync(page, Actor(http), source, ct);
            })
            .WithTags("Customer Seller Chat")
            .WithSummary("Danh sách chat của customer đang đăng nhập")
            .RequireAuthorization("PreorderCustomer")
            .Produces<CustomerSellerChatPage>()
            .ProducesProblem(400).ProducesProblem(401);

        app.MapGet("/api/sellers/{sellerId:guid}/chats", (Guid sellerId, int? page, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return ListSellerAsync(sellerId, page, http, source, ct);
            })
            .WithTags("Customer Seller Chat")
            .WithSummary("Danh sách chat của shop")
            .RequireAuthorization("Seller", "SameSeller")
            .Produces<CustomerSellerChatPage>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

        app.MapGet("/api/chats/{chatId:guid}/messages", (Guid chatId, int? page, HttpContext http, NpgsqlDataSource source, CancellationToken ct) =>
                ReadMessagesAsync(chatId, page, http, source, ct))
            .WithTags("Customer Seller Chat")
            .WithSummary("Đọc lịch sử tin nhắn; trang 1 là các tin mới nhất")
            .RequireAuthorization()
            .Produces<CustomerSellerChatMessagePage>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

        app.MapPost("/api/chats/{chatId:guid}/messages", SendMessageAsync)
            .WithTags("Customer Seller Chat")
            .WithSummary("Gửi tin nhắn văn bản hoặc ảnh")
            .WithDescription("Content tối đa 2000 ký tự. Có thể gửi ảnh bằng ImageReference lấy từ API tải ảnh; tin nhắn phải có nội dung hoặc ảnh. Email, số điện thoại và đường dẫn sẽ bị từ chối.")
            .RequireAuthorization()
            .RequireRateLimiting("chat")
            .Produces<CustomerSellerChatMessage>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);

        app.MapPost("/api/chats/{chatId:guid}/images", UploadImageAsync)
            .WithTags("Customer Seller Chat")
            .WithSummary("Tải ảnh chat riêng tư lên Cloudinary")
            .WithDescription("Gửi raw body dạng image/png, image/jpeg hoặc image/webp, tối đa 5 MB. API trả ImageReference dùng một lần trong tin nhắn và hết hạn sau 30 phút.")
            .RequireAuthorization()
            .RequireRateLimiting("chat")
            .WithMetadata(new RequestSizeLimitAttribute(CloudinaryMedia.MaximumBytes))
            .Accepts<byte[]>("image/png", "image/jpeg", "image/webp")
            .Produces<EvidenceUploadResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(503)
            .AddOpenApiOperationTransformer((operation, context, ct) =>
            {
                foreach (var content in operation.RequestBody!.Content!.Values)
                    content.Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String, Format = "binary" };
                return Task.CompletedTask;
            });

        app.MapGet("/api/chats/{chatId:guid}/messages/{messageId:guid}/image", ReadImageAsync)
            .WithTags("Customer Seller Chat")
            .WithSummary("Đọc ảnh của tin nhắn trong cuộc chat")
            .RequireAuthorization()
            .Produces(200, contentType: "image/png")
            .Produces(200, contentType: "image/jpeg")
            .Produces(200, contentType: "image/webp")
            .ProducesProblem(401).ProducesProblem(404).ProducesProblem(503);
    }

    private static async Task<IResult> OpenAsync(OpenCustomerSellerChatInput input, HttpContext http, NpgsqlDataSource source, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (input.SellerId == Guid.Empty || input.ProductId == Guid.Empty) return Results.BadRequest();
        var actor = Actor(http);
        if (actor is null || actor.Role != "CUSTOMER") return Results.NotFound();

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        string shopName;
        string? productName = null;
        await using (var verify = new NpgsqlCommand("""
            SELECT s.shop_name, p.name
            FROM identity.sellers s
            LEFT JOIN catalog.flower_products p ON p.seller_id=s.id AND p.id=@product AND p.status='ACTIVE'
            WHERE s.id=@seller AND s.status='ACTIVE'
            """, connection, transaction))
        {
            verify.Parameters.AddWithValue("seller", input.SellerId);
            verify.Parameters.AddWithValue("product", NpgsqlDbType.Uuid, (object?)input.ProductId ?? DBNull.Value);
            await using var reader = await verify.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || input.ProductId.HasValue && reader.IsDBNull(1)) return Results.NotFound();
            shopName = reader.GetString(0);
            productName = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ordering.customer_seller_chats(customer_id,seller_id)
            VALUES(@customer,@seller)
            ON CONFLICT (customer_id,seller_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("customer", actor.UserId);
            insert.Parameters.AddWithValue("seller", input.SellerId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        Guid chatId;
        await using (var find = new NpgsqlCommand("SELECT id FROM ordering.customer_seller_chats WHERE customer_id=@customer AND seller_id=@seller", connection, transaction))
        {
            find.Parameters.AddWithValue("customer", actor.UserId);
            find.Parameters.AddWithValue("seller", input.SellerId);
            chatId = (Guid)(await find.ExecuteScalarAsync(ct))!;
        }

        if (input.ProductId.HasValue && productName is not null)
        {
            await using var context = new NpgsqlCommand("""
                INSERT INTO ordering.customer_seller_chat_products(chat_id,product_id,product_name_snapshot)
                VALUES(@chat,@product,@name)
                ON CONFLICT (chat_id,product_id) DO UPDATE
                  SET product_name_snapshot=EXCLUDED.product_name_snapshot,first_opened_at=public.app_now()
                """, connection, transaction);
            context.Parameters.AddWithValue("chat", chatId);
            context.Parameters.AddWithValue("product", input.ProductId.Value);
            context.Parameters.AddWithValue("name", productName);
            await context.ExecuteNonQueryAsync(ct);
        }
        var summary = await ReadOpenedChatAsync(connection, transaction, chatId, actor.UserId, shopName, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(summary);
    }

    private static async Task<IResult> ListAsync(int? page, ChatActor? actor, NpgsqlDataSource source, CancellationToken ct)
    {
        if (actor is null || actor.Role != "CUSTOMER") return Results.NotFound();
        return await ListCoreAsync(page, actor, source, ct);
    }

    private static async Task<IResult> ListSellerAsync(Guid sellerId, int? page, HttpContext http, NpgsqlDataSource source, CancellationToken ct)
    {
        var actor = Actor(http);
        if (actor is null || actor.Role != "SELLER" || actor.SellerId != sellerId) return Results.NotFound();
        return await ListCoreAsync(page, actor, source, ct);
    }

    private static async Task<IResult> ListCoreAsync(int? page, ChatActor actor, NpgsqlDataSource source, CancellationToken ct)
    {
        var number = page ?? 1;
        if (number is < 1 or > 100000) return Results.BadRequest();
        await using var command = source.CreateCommand("""
            SELECT c.id,
              CASE WHEN @role='CUSTOMER' THEN s.shop_name ELSE customer.full_name END,
              c.created_at,c.last_message_at,last.content,
              COALESCE(last.has_image,false),context.product_name
            FROM ordering.customer_seller_chats c
            JOIN identity.users customer ON customer.id=c.customer_id
            JOIN identity.sellers s ON s.id=c.seller_id
            LEFT JOIN LATERAL (
              SELECT m.content, EXISTS(SELECT 1 FROM ordering.customer_seller_chat_images i WHERE i.message_id=m.id) AS has_image
              FROM ordering.customer_seller_chat_messages m
              WHERE m.chat_id=c.id ORDER BY m.created_at DESC,m.id DESC LIMIT 1
            ) last ON true
            LEFT JOIN LATERAL (
              SELECT p.product_name_snapshot AS product_name
              FROM ordering.customer_seller_chat_products p
              WHERE p.chat_id=c.id ORDER BY p.first_opened_at DESC,p.product_id DESC LIMIT 1
            ) context ON true
            WHERE (@role='CUSTOMER' AND c.customer_id=@user)
               OR (@role='SELLER' AND c.seller_id=@seller)
            ORDER BY c.last_message_at DESC NULLS LAST,c.created_at DESC,c.id DESC
            LIMIT 26 OFFSET @offset
            """);
        command.Parameters.AddWithValue("role", actor.Role);
        command.Parameters.AddWithValue("user", actor.UserId);
        command.Parameters.AddWithValue("seller", NpgsqlDbType.Uuid, (object?)actor.SellerId ?? DBNull.Value);
        command.Parameters.AddWithValue("offset", (number - 1) * 25);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<CustomerSellerChatItem>();
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return Results.Ok(new CustomerSellerChatPage(rows.Take(25).ToList(), number, rows.Count > 25));
    }

    private static async Task<IResult> ReadMessagesAsync(Guid chatId, int? page, HttpContext http, NpgsqlDataSource source, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = Actor(http);
        if (actor is null) return Results.NotFound();
        var number = page ?? 1;
        if (number is < 1 or > 100000) return Results.BadRequest();
        await using var command = source.CreateCommand("""
            SELECT m.id,
              CASE WHEN m.sender_user_id=c.customer_id THEN customer.full_name ELSE s.shop_name END,
              m.sender_user_id=@user,m.content,m.created_at,
              EXISTS(SELECT 1 FROM ordering.customer_seller_chat_images i WHERE i.message_id=m.id)
            FROM ordering.customer_seller_chat_messages m
            JOIN ordering.customer_seller_chats c ON c.id=m.chat_id
            JOIN identity.users customer ON customer.id=c.customer_id
            JOIN identity.sellers s ON s.id=c.seller_id
            WHERE c.id=@chat
              AND ((@role='CUSTOMER' AND c.customer_id=@user) OR (@role='SELLER' AND c.seller_id=@seller))
            ORDER BY m.created_at DESC,m.id DESC
            LIMIT 51 OFFSET @offset
            """);
        command.Parameters.AddWithValue("chat", chatId);
        command.Parameters.AddWithValue("user", actor.UserId);
        command.Parameters.AddWithValue("role", actor.Role);
        command.Parameters.AddWithValue("seller", NpgsqlDbType.Uuid, (object?)actor.SellerId ?? DBNull.Value);
        command.Parameters.AddWithValue("offset", (number - 1) * PageSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<CustomerSellerChatMessage>();
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var hasImage = reader.GetBoolean(5);
            rows.Add(new(id, reader.GetString(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4), hasImage ? $"/api/chats/{chatId}/messages/{id}/image" : null));
        }
        var hasMore = rows.Count > PageSize;
        rows = rows.Take(PageSize).ToList();
        rows.Reverse();
        return Results.Ok(new CustomerSellerChatMessagePage(rows, number, hasMore));
    }

    private static async Task<IResult> SendMessageAsync(Guid chatId, CustomerSellerChatMessageInput input, HttpContext http,
        NpgsqlDataSource source, PrivateEvidence tickets, CustomerSellerChatNotifier chatNotifier, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = Actor(http);
        if (actor is null) return Results.NotFound();
        var content = string.IsNullOrWhiteSpace(input.Content) ? null : input.Content.Trim();
        if (content is { Length: > 2000 } || content is null && string.IsNullOrWhiteSpace(input.ImageReference) || input.ImageReference?.Length > 10000)
            return Results.BadRequest();
        if (content is not null && ChatContactFilter.ContainsContact(content))
            return Results.Problem(statusCode: 400, detail: "Tin nhắn không được chứa email, số điện thoại hoặc đường dẫn.");

        VerifiedMedia? image = null;
        if (!string.IsNullOrWhiteSpace(input.ImageReference))
        {
            try { image = tickets.Resolve(input.ImageReference, UploadPurpose, Binding(chatId, actor.UserId)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
        }

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await CanAccessAsync(connection, transaction, chatId, actor, ct)) return Results.NotFound();
        var recipientUserIds = await ChatRecipientUserIdsAsync(connection, transaction, chatId, actor, ct);
        Guid messageId;
        DateTimeOffset createdAt;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ordering.customer_seller_chat_messages(id,chat_id,sender_user_id,content)
            VALUES(public.uuid_v7(),@chat,@sender,@content)
            RETURNING id,created_at
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("chat", chatId);
            insert.Parameters.AddWithValue("sender", actor.UserId);
            insert.Parameters.AddWithValue("content", NpgsqlDbType.Text, (object?)content ?? DBNull.Value);
            await using var reader = await insert.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            messageId = reader.GetGuid(0);
            createdAt = reader.GetFieldValue<DateTimeOffset>(1);
        }

        if (image is not null)
        {
            await using var attach = new NpgsqlCommand("""
                INSERT INTO ordering.customer_seller_chat_images(message_id,chat_id,file_url,mime_type,size_bytes,sha256,uploaded_by)
                VALUES(@message,@chat,@url,@mime,@size,@hash,@sender)
                """, connection, transaction);
            attach.Parameters.AddWithValue("message", messageId);
            attach.Parameters.AddWithValue("chat", chatId);
            attach.Parameters.AddWithValue("url", image.Url);
            attach.Parameters.AddWithValue("mime", image.MimeType);
            attach.Parameters.AddWithValue("size", image.SizeBytes);
            attach.Parameters.AddWithValue("hash", image.Sha256);
            attach.Parameters.AddWithValue("sender", actor.UserId);
            try { await attach.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Results.Conflict(); }
        }

        await using (var update = new NpgsqlCommand("UPDATE ordering.customer_seller_chats SET last_message_at=@at WHERE id=@chat", connection, transaction))
        {
            update.Parameters.AddWithValue("at", createdAt);
            update.Parameters.AddWithValue("chat", chatId);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);

        var senderName = await SenderDisplayNameAsync(source, chatId, actor, ct) ?? "Bạn";
        var imageUrl = image is null ? null : $"/api/chats/{chatId}/messages/{messageId}/image";
        await chatNotifier.MessageReceivedAsync(recipientUserIds,
            new CustomerSellerChatMessageReceived(chatId, messageId, senderName, actor.Role, content, createdAt, imageUrl));
        return Results.Ok(new CustomerSellerChatMessage(messageId, senderName, true, content, createdAt, imageUrl));
    }

    private static async Task<Guid[]> ChatRecipientUserIdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid chatId, ChatActor actor, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT recipient.id
            FROM ordering.customer_seller_chats c
            JOIN identity.users recipient ON recipient.status='ACTIVE'
            WHERE c.id=@chat AND (
                (@role='CUSTOMER' AND recipient.role='SELLER' AND recipient.seller_id=c.seller_id)
                OR (@role='SELLER' AND recipient.role='CUSTOMER' AND recipient.id=c.customer_id)
            )
            ORDER BY recipient.id
            """, connection, transaction);
        command.Parameters.AddWithValue("chat", chatId);
        command.Parameters.AddWithValue("role", actor.Role);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var recipients = new List<Guid>();
        while (await reader.ReadAsync(ct)) recipients.Add(reader.GetGuid(0));
        return recipients.ToArray();
    }

    private static async Task<IResult> UploadImageAsync(Guid chatId, HttpContext http, NpgsqlDataSource source,
        CloudinaryMedia media, PrivateEvidence tickets, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = Actor(http);
        if (actor is null || !await CanAccessAsync(source, chatId, actor, ct)) return Results.NotFound();
        return await PrivateEvidence.UploadAsync(http, media, tickets, UploadPurpose, Binding(chatId, actor.UserId), ct);
    }

    private static async Task<IResult> ReadImageAsync(Guid chatId, Guid messageId, HttpContext http,
        NpgsqlDataSource source, CloudinaryMedia media, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var actor = Actor(http);
        if (actor is null) return Results.NotFound();
        await using var command = source.CreateCommand("""
            SELECT i.file_url,i.mime_type,i.size_bytes,i.sha256
            FROM ordering.customer_seller_chat_images i
            JOIN ordering.customer_seller_chats c ON c.id=i.chat_id
            WHERE i.chat_id=@chat AND i.message_id=@message
              AND ((@role='CUSTOMER' AND c.customer_id=@user) OR (@role='SELLER' AND c.seller_id=@seller))
            """);
        command.Parameters.AddWithValue("chat", chatId);
        command.Parameters.AddWithValue("message", messageId);
        command.Parameters.AddWithValue("role", actor.Role);
        command.Parameters.AddWithValue("user", actor.UserId);
        command.Parameters.AddWithValue("seller", NpgsqlDbType.Uuid, (object?)actor.SellerId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return Results.NotFound();
        var url = reader.GetString(0);
        var mime = reader.GetString(1);
        var size = reader.GetInt64(2);
        var hash = reader.GetString(3);
        await reader.DisposeAsync();
        try
        {
            var bytes = await media.ReadPrivateAsync(url, ct);
            if (bytes.Length != size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash || CloudinaryMedia.ImageType(bytes) != mime)
                throw new MediaUnavailableException();
            return Results.File(bytes, mime, enableRangeProcessing: false);
        }
        catch (Exception ex) when (ex is MediaUnavailableException or HttpRequestException or ArgumentException ||
                                   ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Results.Problem(statusCode: 503, detail: "Kho ảnh chat hiện chưa sẵn sàng.");
        }
    }

    private static async Task<bool> CanAccessAsync(NpgsqlDataSource source, Guid chatId, ChatActor actor, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM ordering.customer_seller_chats c WHERE c.id=@chat
              AND ((@role='CUSTOMER' AND c.customer_id=@user) OR (@role='SELLER' AND c.seller_id=@seller)))
            """);
        AddActor(command, actor);
        command.Parameters.AddWithValue("chat", chatId);
        return Equals(await command.ExecuteScalarAsync(ct), true);
    }

    private static async Task<bool> CanAccessAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid chatId, ChatActor actor, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT c.id FROM ordering.customer_seller_chats c WHERE c.id=@chat
              AND ((@role='CUSTOMER' AND c.customer_id=@user) OR (@role='SELLER' AND c.seller_id=@seller))
            FOR SHARE
            """, connection, transaction);
        AddActor(command, actor);
        command.Parameters.AddWithValue("chat", chatId);
        return await command.ExecuteScalarAsync(ct) is Guid;
    }

    private static async Task<CustomerSellerChatItem> ReadOpenedChatAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid chatId, Guid customerId, string shopName, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT c.created_at,c.last_message_at,last.content,COALESCE(last.has_image,false),context.product_name
            FROM ordering.customer_seller_chats c
            LEFT JOIN LATERAL (
              SELECT m.content,EXISTS(SELECT 1 FROM ordering.customer_seller_chat_images i WHERE i.message_id=m.id) AS has_image
              FROM ordering.customer_seller_chat_messages m WHERE m.chat_id=c.id
              ORDER BY m.created_at DESC,m.id DESC LIMIT 1
            ) last ON true
            LEFT JOIN LATERAL (
              SELECT p.product_name_snapshot AS product_name FROM ordering.customer_seller_chat_products p
              WHERE p.chat_id=c.id ORDER BY p.first_opened_at DESC,p.product_id DESC LIMIT 1
            ) context ON true
            WHERE c.id=@chat AND c.customer_id=@customer
            """, connection, transaction);
        command.Parameters.AddWithValue("chat", chatId);
        command.Parameters.AddWithValue("customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(chatId, shopName, reader.GetFieldValue<DateTimeOffset>(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static void AddActor(NpgsqlCommand command, ChatActor actor)
    {
        command.Parameters.AddWithValue("role", actor.Role);
        command.Parameters.AddWithValue("user", actor.UserId);
        command.Parameters.AddWithValue("seller", NpgsqlDbType.Uuid, (object?)actor.SellerId ?? DBNull.Value);
    }

    private static ChatActor? Actor(HttpContext http)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var userId) || http.User.HasClaim(c => c.Type == "kiosk_id")) return null;
        if (http.User.IsInRole("CUSTOMER")) return new(userId, "CUSTOMER", null);
        if (http.User.IsInRole("SELLER") && Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId))
            return new(userId, "SELLER", sellerId);
        return null;
    }

    private static string Binding(Guid chatId, Guid actorId) => $"chat:{chatId}:{actorId}";

    private static async Task<string?> SenderDisplayNameAsync(NpgsqlDataSource source, Guid chatId, ChatActor actor, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT CASE WHEN @role='CUSTOMER' THEN customer.full_name ELSE s.shop_name END
            FROM ordering.customer_seller_chats c
            JOIN identity.users customer ON customer.id=c.customer_id
            JOIN identity.sellers s ON s.id=c.seller_id
            WHERE c.id=@chat
            """);
        command.Parameters.AddWithValue("chat", chatId);
        command.Parameters.AddWithValue("role", actor.Role);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private sealed record ChatActor(Guid UserId, string Role, Guid? SellerId);
}

public static partial class ChatContactFilter
{
    [GeneratedRegex(@"(?i)\b[\w.+-]+@[\w.-]+\.[a-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?i)\b(?:https?://|www\.)\S+|\b(?:[a-z0-9-]+\.)+(?:com|vn|net|org|io|co|edu|gov|app|dev|shop|store|info|biz|me|ly|gg|link|online|site|xyz)(?:/\S*)?", RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])0[2-9](?:[\s().-]*\d){8}(?![A-Za-z0-9])|(?<![A-Za-z0-9])\+?84[2-9](?:[\s().-]*\d){7}(?![A-Za-z0-9])|(?<![A-Za-z0-9])\+[1-9](?:[\s().-]*\d){7,14}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    public static bool ContainsContact(string value) => EmailRegex().IsMatch(value) || LinkRegex().IsMatch(value) || PhoneRegex().IsMatch(value);
}
