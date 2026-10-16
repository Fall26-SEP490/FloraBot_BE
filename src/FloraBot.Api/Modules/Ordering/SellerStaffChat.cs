using System.Security.Claims;
using System.Security.Cryptography;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Realtime;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Ordering;

public sealed record SellerStaffChatItem(
    Guid Id,
    string OtherPartyName,
    string OtherPartyRole,
    bool OtherPartyActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt,
    string? LastMessage,
    bool LastMessageHasImage,
    long UnreadCount);
public sealed record SellerStaffChatPage(List<SellerStaffChatItem> Items, int Page, bool HasMore);
public sealed record SellerStaffChatMessageInput(string? Content, string? ImageReference = null);
public sealed record SellerStaffChatMessage(
    Guid Id,
    string SenderName,
    string SenderRole,
    bool IsMine,
    string? Content,
    DateTimeOffset CreatedAt,
    string? ImageUrl,
    DateTimeOffset? ReadAt);
public sealed record SellerStaffChatMessagePage(List<SellerStaffChatMessage> Items, int Page, bool HasMore);
public sealed record SellerStaffChatReadResult(
    Guid ChatId,
    int MessagesMarkedRead,
    long UnreadCount,
    Guid? ReadThroughMessageId,
    DateTimeOffset? ReadThroughCreatedAt,
    DateTimeOffset? ReadAt);

public static class SellerStaffChatEndpoints
{
    private const string UploadPurpose = "seller_staff_chat";
    private const int ChatPageSize = 25;
    private const int MessagePageSize = 50;

    public static void MapSellerStaffChat(this WebApplication app)
    {
        app.MapPost("/api/sellers/{sellerId:guid}/staff/{staffId:guid}/chat/open", OpenForSellerAsync)
            .WithTags("Seller Staff Chat")
            .WithSummary("Seller mở hoặc tiếp tục chat riêng với một nhân viên của shop")
            .RequireAuthorization("Seller", "SameSeller")
            .RequireRateLimiting("chat")
            .Produces<SellerStaffChatItem>()
            .ProducesProblem(401).ProducesProblem(404).ProducesProblem(429);

        app.MapPost("/api/staff/chats/open", OpenForStaffAsync)
            .WithTags("Seller Staff Chat")
            .WithSummary("Seller_staff mở hoặc tiếp tục chat với shop của mình")
            .RequireAuthorization("SellerStaff")
            .RequireRateLimiting("chat")
            .Produces<SellerStaffChatItem>()
            .ProducesProblem(401).ProducesProblem(404).ProducesProblem(429);

        app.MapGet("/api/seller-staff/chats", ListAsync)
            .WithTags("Seller Staff Chat")
            .WithSummary("Danh sách chat nội bộ theo shop hoặc nhân viên đang đăng nhập")
            .RequireAuthorization("SellerStaffChatParticipant")
            .Produces<SellerStaffChatPage>()
            .ProducesProblem(400).ProducesProblem(401);

        app.MapGet("/api/seller-staff/chats/{chatId:guid}/messages", ReadMessagesAsync)
            .WithTags("Seller Staff Chat")
            .WithSummary("Đọc lịch sử chat nội bộ; trang 1 là các tin mới nhất")
            .RequireAuthorization("SellerStaffChatParticipant")
            .Produces<SellerStaffChatMessagePage>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

        app.MapPost("/api/seller-staff/chats/{chatId:guid}/read", MarkMessagesReadAsync)
            .WithTags("Seller Staff Chat")
            .WithSummary("Đánh dấu các tin nhắn từ phía còn lại là đã đọc")
            .WithDescription("Gọi sau khi lịch sử hiển thị. Thao tác lặp lại an toàn; bên gửi nhận sự kiện ChatMessagesRead qua SignalR.")
            .RequireAuthorization("SellerStaffChatParticipant")
            .RequireRateLimiting("chat")
            .Produces<SellerStaffChatReadResult>()
            .ProducesProblem(401).ProducesProblem(404).ProducesProblem(429);

        app.MapPost("/api/seller-staff/chats/{chatId:guid}/messages", SendMessageAsync)
            .WithTags("Seller Staff Chat")
            .WithSummary("Gửi tin nhắn văn bản hoặc ảnh trong chat nội bộ")
            .WithDescription("Content tối đa 2000 ký tự. Có thể gửi ảnh bằng ImageReference lấy từ API tải ảnh; tin nhắn phải có nội dung hoặc ảnh.")
            .RequireAuthorization("SellerStaffChatParticipant")
            .RequireRateLimiting("chat")
            .Produces<SellerStaffChatMessage>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);

        app.MapPost("/api/seller-staff/chats/{chatId:guid}/images", UploadImageAsync)
            .WithTags("Seller Staff Chat")
            .WithSummary("Tải ảnh chat nội bộ lên kho riêng tư")
            .WithDescription("Raw body dạng image/png, image/jpeg hoặc image/webp, tối đa 5 MB. Reference dùng một lần và hết hạn sau 30 phút.")
            .RequireAuthorization("SellerStaffChatParticipant")
            .RequireRateLimiting("chat")
            .WithMetadata(new RequestSizeLimitAttribute(CloudinaryMedia.MaximumBytes))
            .Accepts<byte[]>("image/png", "image/jpeg", "image/webp")
            .Produces<EvidenceUploadResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(503)
            .AddOpenApiOperationTransformer((operation, context, ct) =>
            {
                foreach (var content in operation.RequestBody!.Content!.Values)
                    content.Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String, Format = "binary" };
                return Task.CompletedTask;
            });

        app.MapGet("/api/seller-staff/chats/{chatId:guid}/messages/{messageId:guid}/image", ReadImageAsync)
            .WithTags("Seller Staff Chat")
            .WithSummary("Đọc ảnh riêng tư trong chat nội bộ")
            .RequireAuthorization("SellerStaffChatParticipant")
            .Produces(200, contentType: "image/png")
            .Produces(200, contentType: "image/jpeg")
            .Produces(200, contentType: "image/webp")
            .ProducesProblem(401).ProducesProblem(404).ProducesProblem(503);
    }

    private static async Task<IResult> OpenForSellerAsync(Guid sellerId, Guid staffId, HttpContext http,
        NpgsqlDataSource source, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = Actor(http);
        if (actor is null || actor.Role != "SELLER" || actor.SellerId != sellerId || staffId == Guid.Empty)
            return Results.NotFound();

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await IsValidPairAsync(connection, transaction, actor, staffId, ct)) return Results.NotFound();

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ordering.seller_staff_chats(seller_id,staff_user_id)
            VALUES(@seller,@staff)
            ON CONFLICT (seller_id,staff_user_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("seller", sellerId);
            insert.Parameters.AddWithValue("staff", staffId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        var chatId = await FindChatIdAsync(connection, transaction, sellerId, staffId, ct);
        var item = await ReadChatItemAsync(connection, transaction, chatId, actor, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(item);
    }

    private static async Task<IResult> OpenForStaffAsync(HttpContext http, NpgsqlDataSource source, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = Actor(http);
        if (actor is null || actor.Role != "SELLER_STAFF") return Results.NotFound();

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var verify = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1 FROM identity.users staff
              WHERE staff.id=@user AND staff.role='SELLER_STAFF' AND staff.status='ACTIVE'
                AND staff.seller_id=@seller
                AND EXISTS (
                  SELECT 1 FROM identity.users seller
                  WHERE seller.seller_id=staff.seller_id AND seller.role='SELLER' AND seller.status='ACTIVE'
                )
            )
            """, connection, transaction))
        {
            AddActor(verify, actor);
            if (!Equals(await verify.ExecuteScalarAsync(ct), true)) return Results.NotFound();
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ordering.seller_staff_chats(seller_id,staff_user_id)
            VALUES(@seller,@staff)
            ON CONFLICT (seller_id,staff_user_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("seller", actor.SellerId);
            insert.Parameters.AddWithValue("staff", actor.UserId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        var chatId = await FindChatIdAsync(connection, transaction, actor.SellerId, actor.UserId, ct);
        var item = await ReadChatItemAsync(connection, transaction, chatId, actor, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(item);
    }

    private static async Task<Guid> FindChatIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid sellerId, Guid staffId, CancellationToken ct)
    {
        await using var find = new NpgsqlCommand("""
            SELECT id FROM ordering.seller_staff_chats
            WHERE seller_id=@seller AND staff_user_id=@staff
            """, connection, transaction);
        find.Parameters.AddWithValue("seller", sellerId);
        find.Parameters.AddWithValue("staff", staffId);
        return (Guid)(await find.ExecuteScalarAsync(ct))!;
    }

    private static async Task<IResult> ListAsync(int? page, HttpContext http, NpgsqlDataSource source, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = Actor(http);
        if (actor is null) return Results.NotFound();
        var number = page ?? 1;
        if (number is < 1 or > 100000) return Results.BadRequest();

        await using var command = source.CreateCommand("""
            SELECT c.id,
              CASE WHEN @role='SELLER' THEN staff.full_name ELSE shop.shop_name END,
              CASE WHEN @role='SELLER' THEN 'SELLER_STAFF' ELSE 'SELLER' END,
              CASE WHEN @role='SELLER' THEN staff.status='ACTIVE'
                   ELSE EXISTS (SELECT 1 FROM identity.users seller
                                WHERE seller.seller_id=c.seller_id AND seller.role='SELLER' AND seller.status='ACTIVE') END,
              c.created_at,c.last_message_at,last.content,COALESCE(last.has_image,false),COALESCE(unread.unread_count,0)
            FROM ordering.seller_staff_chats c
            JOIN identity.sellers shop ON shop.id=c.seller_id
            JOIN identity.users staff ON staff.id=c.staff_user_id
            LEFT JOIN LATERAL (
              SELECT m.content,EXISTS(SELECT 1 FROM ordering.seller_staff_chat_images i WHERE i.message_id=m.id) AS has_image
              FROM ordering.seller_staff_chat_messages m
              WHERE m.chat_id=c.id ORDER BY m.created_at DESC,m.id DESC LIMIT 1
            ) last ON true
            LEFT JOIN LATERAL (
              SELECT count(*)::bigint AS unread_count
              FROM ordering.seller_staff_chat_messages m
              JOIN identity.users sender ON sender.id=m.sender_user_id
              WHERE m.chat_id=c.id AND m.read_at IS NULL
                AND ((@role='SELLER' AND sender.role='SELLER_STAFF' AND sender.id=c.staff_user_id)
                  OR (@role='SELLER_STAFF' AND sender.role='SELLER' AND sender.seller_id=c.seller_id))
            ) unread ON true
            WHERE EXISTS (SELECT 1 FROM identity.users actor_user
                          WHERE actor_user.id=@user AND actor_user.role=@role
                            AND actor_user.status='ACTIVE' AND actor_user.seller_id=@seller)
              AND ((@role='SELLER' AND c.seller_id=@seller)
                OR (@role='SELLER_STAFF' AND c.seller_id=@seller AND c.staff_user_id=@user))
            ORDER BY c.last_message_at DESC NULLS LAST,c.created_at DESC,c.id DESC
            LIMIT 26 OFFSET @offset
            """);
        AddActor(command, actor);
        command.Parameters.AddWithValue("offset", (number - 1) * ChatPageSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<SellerStaffChatItem>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadChatItem(reader));
        return Results.Ok(new SellerStaffChatPage(rows.Take(ChatPageSize).ToList(), number, rows.Count > ChatPageSize));
    }

    private static async Task<IResult> ReadMessagesAsync(Guid chatId, int? page, HttpContext http,
        NpgsqlDataSource source, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = Actor(http);
        if (actor is null) return Results.NotFound();
        var number = page ?? 1;
        if (number is < 1 or > 100000) return Results.BadRequest();

        await using var command = source.CreateCommand("""
            SELECT m.id,
              CASE WHEN sender.role='SELLER' THEN shop.shop_name ELSE sender.full_name END,
              sender.role,m.sender_user_id=@user,m.content,m.created_at,
              EXISTS(SELECT 1 FROM ordering.seller_staff_chat_images i WHERE i.message_id=m.id),m.read_at
            FROM ordering.seller_staff_chat_messages m
            JOIN ordering.seller_staff_chats c ON c.id=m.chat_id
            JOIN identity.sellers shop ON shop.id=c.seller_id
            JOIN identity.users sender ON sender.id=m.sender_user_id AND sender.seller_id=c.seller_id
            WHERE c.id=@chat
              AND EXISTS (SELECT 1 FROM identity.users actor_user
                          WHERE actor_user.id=@user AND actor_user.role=@role
                            AND actor_user.status='ACTIVE' AND actor_user.seller_id=c.seller_id)
              AND ((@role='SELLER' AND c.seller_id=@seller)
                OR (@role='SELLER_STAFF' AND c.seller_id=@seller AND c.staff_user_id=@user))
            ORDER BY m.created_at DESC,m.id DESC
            LIMIT 51 OFFSET @offset
            """);
        AddActor(command, actor);
        command.Parameters.AddWithValue("chat", chatId);
        command.Parameters.AddWithValue("offset", (number - 1) * MessagePageSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<SellerStaffChatMessage>();
        while (await reader.ReadAsync(ct))
        {
            var messageId = reader.GetGuid(0);
            var hasImage = reader.GetBoolean(6);
            rows.Add(new(messageId, reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
                hasImage ? $"/api/seller-staff/chats/{chatId}/messages/{messageId}/image" : null,
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        }
        var hasMore = rows.Count > MessagePageSize;
        rows = rows.Take(MessagePageSize).ToList();
        rows.Reverse();
        return Results.Ok(new SellerStaffChatMessagePage(rows, number, hasMore));
    }

    private static async Task<IResult> SendMessageAsync(Guid chatId, SellerStaffChatMessageInput input,
        HttpContext http, NpgsqlDataSource source, PrivateEvidence tickets,
        CustomerSellerChatNotifier chatNotifier, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = Actor(http);
        if (actor is null) return Results.NotFound();
        var content = string.IsNullOrWhiteSpace(input.Content) ? null : input.Content.Trim();
        if (content is { Length: > 2000 } || content is null && string.IsNullOrWhiteSpace(input.ImageReference) || input.ImageReference?.Length > 10000)
            return Results.BadRequest();

        VerifiedMedia? image = null;
        if (!string.IsNullOrWhiteSpace(input.ImageReference))
        {
            try { image = tickets.Resolve(input.ImageReference, UploadPurpose, Binding(chatId, actor.UserId)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
        }

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await CanAccessAsync(connection, transaction, chatId, actor, ct)) return Results.NotFound();
        if (!await CounterpartActiveAsync(connection, transaction, chatId, actor, ct))
            return Results.Problem(statusCode: 409, detail: "Tài khoản bên kia hiện không hoạt động nên chưa thể gửi tin nhắn.");
        var recipientUserIds = await ChatRecipientUserIdsAsync(connection, transaction, chatId, actor, ct);

        Guid messageId;
        DateTimeOffset createdAt;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ordering.seller_staff_chat_messages(id,chat_id,sender_user_id,content)
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
                INSERT INTO ordering.seller_staff_chat_images(message_id,chat_id,file_url,mime_type,size_bytes,sha256,uploaded_by)
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

        await using (var update = new NpgsqlCommand("UPDATE ordering.seller_staff_chats SET last_message_at=@at WHERE id=@chat", connection, transaction))
        {
            update.Parameters.AddWithValue("at", createdAt);
            update.Parameters.AddWithValue("chat", chatId);
            await update.ExecuteNonQueryAsync(ct);
        }
        var recipientRole = actor.Role == "SELLER" ? "SELLER_STAFF" : "SELLER";
        var recipientUnreadCount = await UnreadCountAsync(connection, transaction, chatId, recipientRole, ct);
        await transaction.CommitAsync(ct);

        var senderName = await SenderDisplayNameAsync(source, chatId, actor, ct) ?? "Bạn";
        var imageUrl = image is null ? null : $"/api/seller-staff/chats/{chatId}/messages/{messageId}/image";
        await chatNotifier.MessageReceivedAsync(recipientUserIds,
            new CustomerSellerChatMessageReceived(chatId, messageId, senderName, actor.Role, content, createdAt, imageUrl, recipientUnreadCount));
        return Results.Ok(new SellerStaffChatMessage(messageId, senderName, actor.Role, true, content, createdAt, imageUrl, null));
    }

    private static async Task<IResult> MarkMessagesReadAsync(Guid chatId, HttpContext http,
        NpgsqlDataSource source, CustomerSellerChatNotifier chatNotifier, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = Actor(http);
        if (actor is null) return Results.NotFound();

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await CanAccessAsync(connection, transaction, chatId, actor, ct)) return Results.NotFound();
        var recipientUserIds = await ChatRecipientUserIdsAsync(connection, transaction, chatId, actor, ct);
        var marked = new List<(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset ReadAt)>();
        await using (var update = new NpgsqlCommand("""
            UPDATE ordering.seller_staff_chat_messages m
            SET read_at=public.app_now()
            FROM ordering.seller_staff_chats c
            WHERE c.id=@chat AND m.chat_id=c.id AND m.read_at IS NULL
              AND ((@role='SELLER' AND m.sender_user_id=c.staff_user_id)
                OR (@role='SELLER_STAFF' AND EXISTS (
                  SELECT 1 FROM identity.users sender
                  WHERE sender.id=m.sender_user_id AND sender.role='SELLER' AND sender.seller_id=c.seller_id
                )))
            RETURNING m.id,m.created_at,m.read_at
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("chat", chatId);
            update.Parameters.AddWithValue("role", actor.Role);
            await using var reader = await update.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                marked.Add((reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        var unreadCount = await UnreadCountAsync(connection, transaction, chatId, actor.Role, ct);
        await transaction.CommitAsync(ct);

        Guid? readThroughId = null;
        DateTimeOffset? readThroughCreatedAt = null;
        DateTimeOffset? readAt = null;
        if (marked.Count > 0)
        {
            var through = marked.OrderByDescending(message => message.CreatedAt)
                .ThenByDescending(message => message.Id.ToString("N"), StringComparer.Ordinal).First();
            readThroughId = through.Id;
            readThroughCreatedAt = through.CreatedAt;
            readAt = through.ReadAt;
            var readerName = await SenderDisplayNameAsync(source, chatId, actor, ct) ?? "Người tham gia";
            await chatNotifier.MessagesReadAsync(recipientUserIds,
                new CustomerSellerChatMessagesRead(chatId, readerName, actor.Role, through.Id,
                    through.CreatedAt, through.ReadAt, marked.Count));
        }

        return Results.Ok(new SellerStaffChatReadResult(chatId, marked.Count, unreadCount,
            readThroughId, readThroughCreatedAt, readAt));
    }

    private static async Task<long> UnreadCountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid chatId, string role, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*)::bigint
            FROM ordering.seller_staff_chat_messages m
            JOIN ordering.seller_staff_chats c ON c.id=m.chat_id
            JOIN identity.users sender ON sender.id=m.sender_user_id
            WHERE c.id=@chat AND m.read_at IS NULL
              AND ((@role='SELLER' AND sender.role='SELLER_STAFF' AND sender.id=c.staff_user_id)
                OR (@role='SELLER_STAFF' AND sender.role='SELLER' AND sender.seller_id=c.seller_id))
            """, connection, transaction);
        command.Parameters.AddWithValue("chat", chatId);
        command.Parameters.AddWithValue("role", role);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<Guid[]> ChatRecipientUserIdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid chatId, ChatActor actor, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT recipient.id
            FROM ordering.seller_staff_chats c
            JOIN identity.users recipient ON recipient.status='ACTIVE' AND recipient.seller_id=c.seller_id
            WHERE c.id=@chat AND (
              (@role='SELLER' AND recipient.id=c.staff_user_id AND recipient.role='SELLER_STAFF')
              OR (@role='SELLER_STAFF' AND recipient.role='SELLER')
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
        if (!await CounterpartActiveAsync(source, chatId, actor, ct))
            return Results.Problem(statusCode: 409, detail: "Tài khoản bên kia hiện không hoạt động nên chưa thể gửi ảnh.");
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
            FROM ordering.seller_staff_chat_images i
            JOIN ordering.seller_staff_chats c ON c.id=i.chat_id
            JOIN identity.users actor_user ON actor_user.id=@user AND actor_user.role=@role
              AND actor_user.status='ACTIVE' AND actor_user.seller_id=c.seller_id
            WHERE i.chat_id=@chat AND i.message_id=@message
              AND ((@role='SELLER' AND c.seller_id=@seller)
                OR (@role='SELLER_STAFF' AND c.staff_user_id=@user AND c.seller_id=@seller))
            """);
        AddActor(command, actor);
        command.Parameters.AddWithValue("chat", chatId);
        command.Parameters.AddWithValue("message", messageId);
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

    private static async Task<bool> IsValidPairAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ChatActor sellerActor, Guid staffId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1
              FROM identity.users seller
              JOIN identity.users staff ON staff.seller_id=seller.seller_id
              WHERE seller.id=@user AND seller.role='SELLER' AND seller.status='ACTIVE'
                AND seller.seller_id=@seller
                AND staff.id=@staff AND staff.role='SELLER_STAFF' AND staff.status='ACTIVE'
            )
            """, connection, transaction);
        AddActor(command, sellerActor);
        command.Parameters.AddWithValue("staff", staffId);
        return Equals(await command.ExecuteScalarAsync(ct), true);
    }

    private static async Task<bool> CanAccessAsync(NpgsqlDataSource source, Guid chatId, ChatActor actor, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT EXISTS (
              SELECT 1 FROM ordering.seller_staff_chats c
              JOIN identity.users actor_user ON actor_user.id=@user AND actor_user.role=@role
                AND actor_user.status='ACTIVE' AND actor_user.seller_id=c.seller_id
              WHERE c.id=@chat AND c.seller_id=@seller
                AND (@role='SELLER' OR (@role='SELLER_STAFF' AND c.staff_user_id=@user))
            )
            """);
        AddActor(command, actor);
        command.Parameters.AddWithValue("chat", chatId);
        return Equals(await command.ExecuteScalarAsync(ct), true);
    }

    private static async Task<bool> CanAccessAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid chatId, ChatActor actor, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT c.id FROM ordering.seller_staff_chats c
            JOIN identity.users actor_user ON actor_user.id=@user AND actor_user.role=@role
              AND actor_user.status='ACTIVE' AND actor_user.seller_id=c.seller_id
            WHERE c.id=@chat AND c.seller_id=@seller
              AND (@role='SELLER' OR (@role='SELLER_STAFF' AND c.staff_user_id=@user))
            FOR SHARE OF c, actor_user
            """, connection, transaction);
        AddActor(command, actor);
        command.Parameters.AddWithValue("chat", chatId);
        return await command.ExecuteScalarAsync(ct) is Guid;
    }

    private static async Task<bool> CounterpartActiveAsync(NpgsqlDataSource source, Guid chatId, ChatActor actor, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT CASE WHEN @role='SELLER'
              THEN EXISTS (SELECT 1 FROM ordering.seller_staff_chats c
                           JOIN identity.users staff ON staff.id=c.staff_user_id AND staff.role='SELLER_STAFF' AND staff.status='ACTIVE'
                           WHERE c.id=@chat AND c.seller_id=@seller)
              ELSE EXISTS (SELECT 1 FROM ordering.seller_staff_chats c
                           JOIN identity.users seller ON seller.seller_id=c.seller_id AND seller.role='SELLER' AND seller.status='ACTIVE'
                           WHERE c.id=@chat AND c.seller_id=@seller)
            END
            """);
        AddActor(command, actor);
        command.Parameters.AddWithValue("chat", chatId);
        return Equals(await command.ExecuteScalarAsync(ct), true);
    }

    private static async Task<bool> CounterpartActiveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid chatId, ChatActor actor, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT CASE WHEN @role='SELLER'
              THEN EXISTS (SELECT 1 FROM ordering.seller_staff_chats c
                           JOIN identity.users staff ON staff.id=c.staff_user_id AND staff.role='SELLER_STAFF' AND staff.status='ACTIVE'
                           WHERE c.id=@chat AND c.seller_id=@seller)
              ELSE EXISTS (SELECT 1 FROM ordering.seller_staff_chats c
                           JOIN identity.users seller ON seller.seller_id=c.seller_id AND seller.role='SELLER' AND seller.status='ACTIVE'
                           WHERE c.id=@chat AND c.seller_id=@seller)
            END
            """, connection, transaction);
        AddActor(command, actor);
        command.Parameters.AddWithValue("chat", chatId);
        return Equals(await command.ExecuteScalarAsync(ct), true);
    }

    private static async Task<SellerStaffChatItem> ReadChatItemAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid chatId, ChatActor actor, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT c.id,
              CASE WHEN @role='SELLER' THEN staff.full_name ELSE shop.shop_name END,
              CASE WHEN @role='SELLER' THEN 'SELLER_STAFF' ELSE 'SELLER' END,
              CASE WHEN @role='SELLER' THEN staff.status='ACTIVE'
                   ELSE EXISTS (SELECT 1 FROM identity.users seller
                                WHERE seller.seller_id=c.seller_id AND seller.role='SELLER' AND seller.status='ACTIVE') END,
              c.created_at,c.last_message_at,last.content,COALESCE(last.has_image,false),COALESCE(unread.unread_count,0)
            FROM ordering.seller_staff_chats c
            JOIN identity.sellers shop ON shop.id=c.seller_id
            JOIN identity.users staff ON staff.id=c.staff_user_id
            LEFT JOIN LATERAL (
              SELECT m.content,EXISTS(SELECT 1 FROM ordering.seller_staff_chat_images i WHERE i.message_id=m.id) AS has_image
              FROM ordering.seller_staff_chat_messages m WHERE m.chat_id=c.id
              ORDER BY m.created_at DESC,m.id DESC LIMIT 1
            ) last ON true
            LEFT JOIN LATERAL (
              SELECT count(*)::bigint AS unread_count
              FROM ordering.seller_staff_chat_messages m
              JOIN identity.users sender ON sender.id=m.sender_user_id
              WHERE m.chat_id=c.id AND m.read_at IS NULL
                AND ((@role='SELLER' AND sender.role='SELLER_STAFF' AND sender.id=c.staff_user_id)
                  OR (@role='SELLER_STAFF' AND sender.role='SELLER' AND sender.seller_id=c.seller_id))
            ) unread ON true
            WHERE c.id=@chat AND c.seller_id=@seller
              AND (@role='SELLER' OR (@role='SELLER_STAFF' AND c.staff_user_id=@user))
            """, connection, transaction);
        AddActor(command, actor);
        command.Parameters.AddWithValue("chat", chatId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ReadChatItem(reader);
    }

    private static SellerStaffChatItem ReadChatItem(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetBoolean(7), reader.GetInt64(8));

    private static async Task<string?> SenderDisplayNameAsync(NpgsqlDataSource source, Guid chatId, ChatActor actor, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT CASE WHEN @role='SELLER' THEN shop.shop_name ELSE sender.full_name END
            FROM ordering.seller_staff_chats c
            JOIN identity.sellers shop ON shop.id=c.seller_id
            JOIN identity.users sender ON sender.id=@user AND sender.seller_id=c.seller_id
            WHERE c.id=@chat
            """);
        AddActor(command, actor);
        command.Parameters.AddWithValue("chat", chatId);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static void AddActor(NpgsqlCommand command, ChatActor actor)
    {
        command.Parameters.AddWithValue("role", actor.Role);
        command.Parameters.AddWithValue("user", actor.UserId);
        command.Parameters.AddWithValue("seller", actor.SellerId);
    }

    private static ChatActor? Actor(HttpContext http)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var userId) ||
            !Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId) ||
            http.User.HasClaim(c => c.Type == "kiosk_id")) return null;
        if (http.User.IsInRole("SELLER")) return new(userId, "SELLER", sellerId);
        if (http.User.IsInRole("SELLER_STAFF")) return new(userId, "SELLER_STAFF", sellerId);
        return null;
    }

    private static string Binding(Guid chatId, Guid actorId) => $"seller-staff-chat:{chatId}:{actorId}";

    private sealed record ChatActor(Guid UserId, string Role, Guid SellerId);
}
