using System.Security.Claims;
using System.Text.Json;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Ordering;

public sealed record WebRequestInput(Guid Id, string Kind, Guid ProductId, Guid KioskId, Guid? BouquetId, DateTimeOffset PickupAt, string Instructions);
public sealed record WebQuoteInput(long Price, DateTimeOffset PickupAt, string Note, bool Reject = false);
public sealed record WebFulfillInput(Guid SlotId, string QrCode);
public sealed record WebCancelInput(string Reason);
public sealed record WebAcceptInput(long Price, DateTimeOffset PickupAt, string Note);
public sealed record WebRequestRow(Guid Id, string Kind, string State, Guid ProductId, Guid KioskId, Guid SellerId, string Instructions, DateTimeOffset PickupAt, DateTimeOffset PickupBefore, long? QuotedPrice, string? QuoteNote, DateTimeOffset? QuoteExpiresAt, Guid? OrderId, Guid? CheckoutId, string? OrderCode, string? OrderStatus, string? TrackingToken, bool Ready, DateTimeOffset? PayBefore, string? ProductName, string? ShopName, string? KioskName, string? Address);
public sealed record WebRequestPage(List<WebRequestRow> Items, int Page, bool HasMore);

public static class WebOrders
{
    public static void MapWebOrders(this WebApplication app)
    {
        app.MapGet("/api/member/preorders", (int? page, HttpContext http, NpgsqlDataSource source, CancellationToken ct) => ReadAsync(source, User(http), null, true, page, ct)).RequireAuthorization("Member").Produces<WebRequestPage>();
        app.MapGet("/api/sellers/{sellerId:guid}/preorders", (Guid sellerId, int? page, NpgsqlDataSource source, CancellationToken ct) => ReadAsync(source, null, sellerId, false, page, ct)).RequireAuthorization("Merchant", "SameSeller").Produces<WebRequestPage>();
        app.MapGet("/api/admin/preorders", (int? page, NpgsqlDataSource source, CancellationToken ct) => ReadAsync(source, null, null, false, page, ct)).RequireAuthorization("Admin").Produces<WebRequestPage>();
        app.MapPost("/api/member/preorders", async (WebRequestInput input, HttpContext http, NpgsqlDataSource source, ICapPublisher publisher, CancellationToken ct) =>
        {
            if (input.Id == Guid.Empty || input.Kind is not ("STOCK" or "CUSTOM") || input.Instructions is null || input.Instructions.Length > 2000 || input.Kind == "STOCK" && input.BouquetId is null) return Results.BadRequest();
            return await Run(source, publisher, "SELECT flow.web_request(@id,@customer,@kind,@product,@kiosk,@bouquet,@pickup,@instructions)", command =>
            {
                command.Parameters.AddWithValue("id", input.Id); command.Parameters.AddWithValue("customer", User(http));
                command.Parameters.AddWithValue("kind", input.Kind); command.Parameters.AddWithValue("product", input.ProductId); command.Parameters.AddWithValue("kiosk", input.KioskId);
                command.Parameters.AddWithValue("bouquet", NpgsqlDbType.Uuid, (object?)input.BouquetId ?? DBNull.Value);
                command.Parameters.AddWithValue("pickup", input.PickupAt.UtcDateTime); command.Parameters.AddWithValue("instructions", input.Instructions.Trim());
            }, ct);
        }).RequireAuthorization("Member").RequireRateLimiting("auth").Produces<FlowResult>();
        app.MapPost("/api/member/preorders/{requestId:guid}/accept", async (Guid requestId, WebAcceptInput input, HttpContext http, NpgsqlDataSource source, ICapPublisher publisher, CancellationToken ct) =>
        {
            if (!await Owns(source, requestId, User(http), null, ct)) return Results.NotFound();
            if (input.Note is null || input.Note.Length > 1000) return Results.BadRequest();
            return await Run(source, publisher, "SELECT flow.web_accept(@id,@customer,@price,@pickup,@note)", command => { command.Parameters.AddWithValue("id", requestId); command.Parameters.AddWithValue("customer", User(http)); command.Parameters.AddWithValue("price", input.Price); command.Parameters.AddWithValue("pickup", input.PickupAt.UtcDateTime); command.Parameters.AddWithValue("note", input.Note); }, ct);
        }).RequireAuthorization("Member").Produces<FlowResult>();
        app.MapPost("/api/member/preorders/{requestId:guid}/cancel", (Guid requestId, WebCancelInput input, HttpContext http, NpgsqlDataSource source, ICapPublisher publisher, CancellationToken ct) => Cancel(requestId, input, User(http), User(http), null, source, publisher, ct)).RequireAuthorization("Member");
        app.MapPost("/api/sellers/{sellerId:guid}/preorders/{requestId:guid}/cancel", (Guid sellerId, Guid requestId, WebCancelInput input, HttpContext http, NpgsqlDataSource source, ICapPublisher publisher, CancellationToken ct) => Cancel(requestId, input, User(http), null, sellerId, source, publisher, ct)).RequireAuthorization("Merchant", "SameSeller");
        app.MapPost("/api/admin/preorders/{requestId:guid}/cancel", (Guid requestId, WebCancelInput input, HttpContext http, NpgsqlDataSource source, ICapPublisher publisher, CancellationToken ct) => Cancel(requestId, input, User(http), null, null, source, publisher, ct)).RequireAuthorization("Admin");
        app.MapPost("/api/sellers/{sellerId:guid}/preorders/{requestId:guid}/quote", async (Guid sellerId, Guid requestId, WebQuoteInput input, HttpContext http, NpgsqlDataSource source, ICapPublisher publisher, CancellationToken ct) =>
        {
            if (input.Note is null || input.Note.Length is < 5 or > 1000) return Results.BadRequest();
            if (!await Owns(source, requestId, null, sellerId, ct)) return Results.NotFound();
            return await Run(source, publisher, "SELECT flow.web_quote(@id,@seller,@actor,@price,@pickup,@note,@reject)", command =>
            {
                command.Parameters.AddWithValue("id", requestId); command.Parameters.AddWithValue("seller", sellerId); command.Parameters.AddWithValue("actor", User(http));
                command.Parameters.AddWithValue("price", input.Price); command.Parameters.AddWithValue("pickup", input.PickupAt.UtcDateTime); command.Parameters.AddWithValue("note", input.Note.Trim()); command.Parameters.AddWithValue("reject", input.Reject);
            }, ct);
        }).RequireAuthorization("Merchant", "SameSeller").Produces<FlowResult>();
        app.MapPost("/api/sellers/{sellerId:guid}/preorders/{requestId:guid}/fulfill", async (Guid sellerId, Guid requestId, WebFulfillInput input, HttpContext http, NpgsqlDataSource source, ICapPublisher publisher, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.QrCode) || input.QrCode.Length > 120) return Results.BadRequest();
            if (!await Owns(source, requestId, null, sellerId, ct)) return Results.NotFound();
            return await Run(source, publisher, "SELECT flow.web_fulfill(@id,@seller,@actor,@slot,@qr)", command =>
            {
                command.Parameters.AddWithValue("id", requestId); command.Parameters.AddWithValue("seller", sellerId); command.Parameters.AddWithValue("actor", User(http)); command.Parameters.AddWithValue("slot", input.SlotId); command.Parameters.AddWithValue("qr", input.QrCode.Trim());
            }, ct);
        }).RequireAuthorization("Merchant", "SameSeller").Produces<FlowResult>();
    }

    internal static async Task<bool> Owns(NpgsqlDataSource source, Guid id, Guid? customer, Guid? seller, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT EXISTS(SELECT 1 FROM ordering.web_requests WHERE id=@id AND (@customer IS NULL OR customer_id=@customer) AND (@seller IS NULL OR seller_id=@seller))");
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("customer", NpgsqlDbType.Uuid, (object?)customer ?? DBNull.Value); command.Parameters.AddWithValue("seller", NpgsqlDbType.Uuid, (object?)seller ?? DBNull.Value);
        return Equals(await command.ExecuteScalarAsync(ct), true);
    }
    private static Guid User(HttpContext http) => Guid.Parse(http.User.FindFirstValue("sub")!);
    private static async Task<IResult> Cancel(Guid id, WebCancelInput input, Guid actor, Guid? customer, Guid? seller, NpgsqlDataSource source, ICapPublisher publisher, CancellationToken ct)
    {
        if (input.Reason is null || input.Reason.Trim().Length is < 5 or > 1000) return Results.BadRequest();
        if (!await Owns(source, id, customer, seller, ct)) return Results.NotFound();
        return await Run(source, publisher, "SELECT flow.web_cancel(@id,@actor,@reason)", command => { command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("reason", input.Reason.Trim()); }, ct);
    }
    private static async Task<IResult> Run(NpgsqlDataSource source, ICapPublisher publisher, string sql, Action<NpgsqlCommand> parameters, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
        await using var command = new NpgsqlCommand(sql, connection, transaction); parameters(command);
        var result = await command.ExecuteScalarAsync(ct);
        await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
        await outbox.CommitAsync(ct);
        return Results.Ok(new FlowResult(result is Guid id ? JsonSerializer.SerializeToElement(id) : null));
    }
    private static async Task<IResult> ReadAsync(NpgsqlDataSource source, Guid? customer, Guid? seller, bool privateReceipt, int? page, CancellationToken ct)
    {
        var number = page ?? 1; if (number is < 1 or > 100000) return Results.BadRequest();
        await using var command = source.CreateCommand("""
            SELECT r.id,r.kind,r.state,r.product_id,r.kiosk_id,r.seller_id,r.instructions,r.pickup_at,r.pickup_before,r.quoted_price,r.quote_note,r.quote_expires_at,
              o.id,o.checkout_id,o.order_code,o.status,o.tracking_token,EXISTS(SELECT 1 FROM ordering.order_items i WHERE i.order_id=o.id AND i.item_type='BOUQUET'),o.created_at+flow.cfg_min('hold_minutes'),r.display_snapshot->>'product',r.display_snapshot->>'shop',r.display_snapshot->>'kiosk',r.display_snapshot->>'address'
            FROM ordering.web_requests r LEFT JOIN ordering.orders o ON o.id=r.order_id
            WHERE (@customer IS NULL OR r.customer_id=@customer) AND (@seller IS NULL OR r.seller_id=@seller)
            ORDER BY r.created_at DESC,r.id DESC LIMIT 26 OFFSET @offset
            """);
        command.Parameters.AddWithValue("customer", NpgsqlDbType.Uuid, (object?)customer ?? DBNull.Value); command.Parameters.AddWithValue("seller", NpgsqlDbType.Uuid, (object?)seller ?? DBNull.Value); command.Parameters.AddWithValue("offset", (number - 1) * 25);
        await using var reader = await command.ExecuteReaderAsync(ct); var items = new List<WebRequestRow>();
        while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3), reader.GetGuid(4), reader.GetGuid(5), reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8), reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11), reader.IsDBNull(12) ? null : reader.GetGuid(12), reader.IsDBNull(13) ? null : reader.GetGuid(13), reader.IsDBNull(14) ? null : reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15), privateReceipt && !reader.IsDBNull(16) ? reader.GetString(16) : null, reader.GetBoolean(17), reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18), reader.IsDBNull(19) ? null : reader.GetString(19), reader.IsDBNull(20) ? null : reader.GetString(20), reader.IsDBNull(21) ? null : reader.GetString(21), reader.IsDBNull(22) ? null : reader.GetString(22)));
        return Results.Ok(new WebRequestPage(items.Take(25).ToList(), number, items.Count > 25));
    }

    public static async Task<Guid?> MemberCheckoutAsync(NpgsqlDataSource source, Guid requestId, Guid member, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT o.checkout_id FROM ordering.web_requests r JOIN ordering.orders o ON o.id=r.order_id WHERE r.id=@id AND r.customer_id=@member AND o.status='AWAITING_PAYMENT'");
        command.Parameters.AddWithValue("id", requestId); command.Parameters.AddWithValue("member", member);
        return await command.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }
}
