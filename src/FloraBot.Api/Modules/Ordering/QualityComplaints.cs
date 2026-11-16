using System.Security.Claims;
using System.Text.Json;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Ordering;

public sealed record QualityVersion(int Version);
public sealed record AssignQualityTask(int Version, Guid TaskId, Guid AssigneeId, Guid OrderItemId, string Instructions);
public sealed record QualityReplacementPlan(int Version, int TaskVersion, Guid ProductId, Guid SlotId);
public sealed record ReviewQualityComplaint(int Version, int TaskVersion, string Outcome, string Resolution);
public sealed record CancelQualityReplacement(int Version, string Reason);
public sealed record QualityPickupRequest(Guid ComplaintId, Guid OrderId, string TrackingToken, Guid AttemptId);
public sealed record QualityPickupResponse(Guid ComplaintId, Guid CommandId, string CommandStatus);
public sealed record QualityOrderItem(Guid OrderItemId, Guid? BouquetId, Guid? ProductId, Guid? SlotId, string? SlotCode, string Name);
public sealed record QualityTaskInfo(Guid Id, int Version, string Status, Guid AssigneeId, Guid OrderItemId, string Instructions, string? Report);
public sealed record QualityReplacementInfo(Guid TaskId, string State, Guid ProductId, Guid? BouquetId,
    Guid SlotId, string SlotCode, DateTimeOffset? ExpiresAt, DateTimeOffset? CollectedAt, Guid? CommandId, string? CommandStatus);
public sealed record QualityPhoto(Guid Id, DateTimeOffset CreatedAt, string Source, Guid? TaskId, string Url);
public sealed record QualityEvidencePage(int Page, bool HasMore, List<QualityPhoto> Items);
public sealed record QualityComplaintDetail(Guid Id, Guid OrderId, string OrderCode, Guid KioskId, string Status,
    string HandlingState, int Version, string Reason, DateTimeOffset CreatedAt, DateTimeOffset? AcceptedAt,
    string? Outcome, string? Resolution, DateTimeOffset? ResolvedAt, List<QualityOrderItem> Items,
    QualityTaskInfo? Task, QualityReplacementInfo? Replacement, QualityEvidencePage Evidence);
public sealed record QualityComplaintPage(int Page, bool HasMore, List<QualityComplaintDetail> Items);

public static class QualityComplaints
{
    public static void MapQualityComplaints(this WebApplication app)
    {
        app.MapGet("/api/me/complaints", List).RequireAuthorization("Seller").WithTags("Ordering").Produces<QualityComplaintPage>();
        app.MapGet("/api/admin/quality-complaints", List).RequireAuthorization("Admin").WithTags("Ordering").Produces<QualityComplaintPage>();
        app.MapGet("/api/me/complaints/{complaintId:guid}", ReadSeller).RequireAuthorization("Seller").WithTags("Ordering").Produces<QualityComplaintDetail>();
        app.MapGet("/api/admin/quality-complaints/{complaintId:guid}", ReadSeller).RequireAuthorization("Admin").WithTags("Ordering").Produces<QualityComplaintDetail>();
        app.MapGet("/api/staff/tasks/{taskId:guid}/quality", async (Guid taskId, int? evidencePage,
            HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            await using var command = data.CreateCommand("SELECT flow.quality_task_complaint(@task,@actor)");
            command.Parameters.AddWithValue("task", taskId); command.Parameters.AddWithValue("actor", Actor(http));
            return await command.ExecuteScalarAsync(ct) is Guid complaint
                ? await Read(complaint, Actor(http), null, null, evidencePage, http, data, ct) : Results.NotFound();
        }).RequireAuthorization("SellerStaff").WithTags("Ordering").Produces<QualityComplaintDetail>();

        app.MapPost("/api/receipts/complaints/lookup", async (ReceiptLookupRequest input, int? evidencePage,
            HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var token = ReceiptAccess.NormalizeToken(input.TrackingToken);
            if (token is null || input.OrderId == Guid.Empty) return Results.NotFound();
            await using var command = data.CreateCommand("SELECT id FROM ordering.disputes WHERE order_id=@order AND kind='COMPLAINT'");
            command.Parameters.AddWithValue("order", input.OrderId);
            return await command.ExecuteScalarAsync(ct) is Guid complaint
                ? await Read(complaint, null, input.OrderId, token, evidencePage, http, data, ct) : Results.NotFound();
        }).AllowAnonymous().RequireRateLimiting("receipt").WithTags("Ordering").Produces<QualityComplaintDetail>();

        app.MapPost("/api/me/complaints/{complaintId:guid}/accept", (Guid complaintId, QualityVersion input,
            HttpContext http, NpgsqlDataSource data, ICapPublisher publisher, CancellationToken ct) =>
            Mutate("accept_quality_complaint", complaintId, input.Version, http, data, publisher, ct))
            .RequireAuthorization("Seller").WithTags("Ordering").Produces<QualityVersion>();
        app.MapPost("/api/me/complaints/{complaintId:guid}/assignment", (Guid complaintId, AssignQualityTask input,
            HttpContext http, NpgsqlDataSource data, ICapPublisher publisher, CancellationToken ct) =>
            input.TaskId == Guid.Empty || input.AssigneeId == Guid.Empty || input.OrderItemId == Guid.Empty || !ValidText(input.Instructions, 500)
                ? Task.FromResult<IResult>(Results.BadRequest())
                : Mutate("assign_quality_task", complaintId, input.Version, http, data, publisher, ct,
                    ("task", input.TaskId), ("staff", input.AssigneeId), ("item", input.OrderItemId), ("instructions", input.Instructions.Trim())))
            .RequireAuthorization("Seller").WithTags("Ordering").Produces<QualityVersion>();
        app.MapPost("/api/me/complaints/{complaintId:guid}/replacement-plan", (Guid complaintId, QualityReplacementPlan input,
            HttpContext http, NpgsqlDataSource data, ICapPublisher publisher, CancellationToken ct) =>
            input.ProductId == Guid.Empty || input.SlotId == Guid.Empty || input.TaskVersion < 1
                ? Task.FromResult<IResult>(Results.BadRequest())
                : Mutate("plan_quality_replacement", complaintId, input.Version, http, data, publisher, ct,
                    ("task_version", input.TaskVersion), ("product", input.ProductId), ("slot", input.SlotId)))
            .RequireAuthorization("Seller").WithTags("Ordering").Produces<QualityVersion>();
        app.MapPost("/api/me/complaints/{complaintId:guid}/review", (Guid complaintId, ReviewQualityComplaint input,
            HttpContext http, NpgsqlDataSource data, ICapPublisher publisher, CancellationToken ct) =>
            input.TaskVersion < 1 || !ValidText(input.Resolution) || input.Outcome is not ("RETURN" or "EXPLANATION" or "REPLACEMENT" or "REFUND_REVIEW")
                ? Task.FromResult<IResult>(Results.BadRequest())
                : Mutate("review_quality_complaint", complaintId, input.Version, http, data, publisher, ct,
                    ("task_version", input.TaskVersion), ("outcome", input.Outcome), ("resolution", input.Resolution.Trim())))
            .RequireAuthorization("Seller").WithTags("Ordering").Produces<QualityVersion>();
        app.MapPost("/api/me/complaints/{complaintId:guid}/replacement/cancel", (Guid complaintId, CancelQualityReplacement input,
            HttpContext http, NpgsqlDataSource data, ICapPublisher publisher, CancellationToken ct) =>
            !ValidText(input.Reason) ? Task.FromResult<IResult>(Results.BadRequest())
                : Mutate("cancel_quality_replacement", complaintId, input.Version, http, data, publisher, ct, ("reason", input.Reason.Trim())))
            .RequireAuthorization("Seller").WithTags("Ordering").Produces<QualityVersion>();

        app.MapPost("/api/kiosks/{kioskId:guid}/quality-replacements/open", Open)
            .RequireAuthorization("Kiosk").RequireRateLimiting("receipt").WithTags("Ordering").Produces<QualityPickupResponse>();
        // Status and receipt evidence use POST so the receipt secret never enters a URL/log.
        app.MapPost("/api/kiosks/{kioskId:guid}/quality-replacements/status", Status)
            .RequireAuthorization("Kiosk").RequireRateLimiting("receipt").WithTags("Ordering").Produces<QualityComplaintDetail>();

        app.MapGet("/api/me/complaints/{complaintId:guid}/evidence/{attachmentId:guid}", ReadPhoto)
            .RequireAuthorization("Seller").WithTags("Ordering");
        app.MapGet("/api/admin/quality-complaints/{complaintId:guid}/evidence/{attachmentId:guid}", ReadPhoto)
            .RequireAuthorization("Admin").WithTags("Ordering");
        app.MapGet("/api/staff/quality-complaints/{complaintId:guid}/evidence/{attachmentId:guid}", ReadPhoto)
            .RequireAuthorization("SellerStaff").WithTags("Ordering");
        app.MapPost("/api/receipts/complaints/{complaintId:guid}/evidence/{attachmentId:guid}",
            (Guid complaintId, Guid attachmentId, ReceiptLookupRequest input, HttpContext http,
                NpgsqlDataSource data, CloudinaryMedia media, CancellationToken ct) =>
                QualityEvidence.Read(complaintId, attachmentId, null, input.OrderId, ReceiptAccess.NormalizeToken(input.TrackingToken), http, data, media, ct))
            .AllowAnonymous().RequireRateLimiting("receipt").WithTags("Ordering");
    }

    private static Guid Actor(HttpContext http) => Guid.Parse(http.User.FindFirstValue("sub")!);
    private static bool ValidText(string? text, int max = 2000) => text?.Trim() is { Length: >= 10 } t && t.Length <= max
        && !t.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));
    private static bool DeviceMatches(HttpContext http, Guid kioskId) =>
        Guid.TryParse(http.User.FindFirstValue("kiosk_id"), out var kiosk) && kiosk == kioskId;
    private static IResult Problem(PostgresException ex) => ex.SqlState switch
    {
        "42501" => Results.Forbid(), "P0002" => Results.NotFound(),
        "22023" => Results.Problem(statusCode: 400, detail: ex.MessageText),
        _ => Results.Problem(statusCode: 409, detail: ex.SqlState == "40001" ? ex.MessageText : "Hồ sơ hoặc ô tủ đã thay đổi. Kiểm tra trạng thái trước khi tiếp tục.")
    };
    private static bool Handled(PostgresException ex) => ex.SqlState is "42501" or "P0002" or "22023" or "40001" or "40P01" or "23505" or "23503" or "55P03";
    private static IResult Json(object? value)
    {
        if (value is not string json) return Results.NotFound();
        using var document = JsonDocument.Parse(json);
        return Results.Json(document.RootElement.Clone());
    }
    private static void Optional(NpgsqlCommand command, string key, NpgsqlDbType type, object? value) =>
        command.Parameters.AddWithValue(key, type, value ?? DBNull.Value);

    private static async Task<IResult> List(string? state, int? page, HttpContext http, NpgsqlDataSource data, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var number = page ?? 1;
        if (number is < 1 or > 100000 || state is not (null or "REPORTED" or "ACCEPTED" or "IN_PROGRESS" or "REVIEW_REQUIRED"
            or "REPLACEMENT_READY" or "ATTENTION_REQUIRED" or "REFUND_REVIEW" or "RESOLVED")) return Results.BadRequest();
        await using var command = data.CreateCommand("SELECT flow.list_quality_complaints(@actor,@state,@page)::text");
        command.Parameters.AddWithValue("actor", Actor(http)); command.Parameters.AddWithValue("page", number);
        Optional(command, "state", NpgsqlDbType.Text, state);
        return Json(await command.ExecuteScalarAsync(ct));
    }

    private static Task<IResult> ReadSeller(Guid complaintId, int? evidencePage, HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        Read(complaintId, Actor(http), null, null, evidencePage, http, data, ct);

    private static async Task<IResult> Read(Guid id, Guid? actor, Guid? order, string? tracking, int? evidencePage,
        HttpContext http, NpgsqlDataSource data, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var page = evidencePage ?? 1;
        if (page is < 1 or > 100000) return Results.BadRequest();
        await using var command = data.CreateCommand("SELECT flow.read_quality_complaint(@id,@actor,@order,@tracking,@page)::text");
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("page", page);
        Optional(command, "actor", NpgsqlDbType.Uuid, actor); Optional(command, "order", NpgsqlDbType.Uuid, order);
        Optional(command, "tracking", NpgsqlDbType.Text, tracking);
        return Json(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<IResult> Mutate(string flow, Guid id, int version, HttpContext http, NpgsqlDataSource data,
        ICapPublisher publisher, CancellationToken ct, params (string Key, object Value)[] args)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (version < 1) return Results.BadRequest();
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        try
        {
            await using var setup = new NpgsqlCommand("SET LOCAL lock_timeout='5s'; SELECT flow.reset_clock()", connection, transaction);
            await setup.ExecuteNonQueryAsync(ct);
            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            // flow and argument names come only from this file's fixed route definitions.
            await using var command = new NpgsqlCommand($"SELECT flow.{flow}(@id,@actor,@version{string.Concat(args.Select(a => ",@" + a.Key))})", connection, transaction);
            command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("actor", Actor(http)); command.Parameters.AddWithValue("version", version);
            foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
            var next = (int)(await command.ExecuteScalarAsync(ct))!;
            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);
            return Results.Ok(new QualityVersion(next));
        }
        catch (PostgresException ex) when (Handled(ex)) { return Problem(ex); }
    }

    private static async Task<IResult> Open(Guid kioskId, QualityPickupRequest input, HttpContext http,
        NpgsqlDataSource data, ICapPublisher publisher, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!DeviceMatches(http, kioskId)) return Results.Forbid();
        var tracking = ReceiptAccess.NormalizeToken(input.TrackingToken);
        if (tracking is null || input.AttemptId == Guid.Empty || input.OrderId == Guid.Empty || input.ComplaintId == Guid.Empty) return Results.BadRequest();
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        try
        {
            await using var setup = new NpgsqlCommand("SET LOCAL lock_timeout='5s'; SELECT flow.reset_clock()", connection, transaction);
            await setup.ExecuteNonQueryAsync(ct);
            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            await using var command = new NpgsqlCommand("SELECT flow.open_quality_pickup(@complaint,@order,@tracking,@kiosk,@attempt)", connection, transaction);
            command.Parameters.AddWithValue("complaint", input.ComplaintId); command.Parameters.AddWithValue("order", input.OrderId);
            command.Parameters.AddWithValue("tracking", tracking); command.Parameters.AddWithValue("kiosk", kioskId); command.Parameters.AddWithValue("attempt", input.AttemptId);
            var cmd = (Guid)(await command.ExecuteScalarAsync(ct))!;
            command.CommandText = "SELECT flow.quality_command_status(@cmd)";
            command.Parameters.AddWithValue("cmd", cmd);
            var status = (string)(await command.ExecuteScalarAsync(ct))!;
            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);
            return Results.Ok(new QualityPickupResponse(input.ComplaintId, cmd, status));
        }
        catch (PostgresException ex) when (Handled(ex)) { return Problem(ex); }
    }

    private static async Task<IResult> Status(Guid kioskId, QualityPickupRequest input, HttpContext http, NpgsqlDataSource data, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!DeviceMatches(http, kioskId)) return Results.Forbid();
        await using var command = data.CreateCommand("SELECT 1 FROM ordering.orders WHERE id=@order AND kiosk_id=@kiosk");
        command.Parameters.AddWithValue("order", input.OrderId); command.Parameters.AddWithValue("kiosk", kioskId);
        return await command.ExecuteScalarAsync(ct) is null ? Results.NotFound()
            : await Read(input.ComplaintId, null, input.OrderId, ReceiptAccess.NormalizeToken(input.TrackingToken), 1, http, data, ct);
    }

    private static Task<IResult> ReadPhoto(Guid complaintId, Guid attachmentId, HttpContext http, NpgsqlDataSource data, CloudinaryMedia media, CancellationToken ct) =>
        QualityEvidence.Read(complaintId, attachmentId, Actor(http), null, null, http, data, media, ct);
}
