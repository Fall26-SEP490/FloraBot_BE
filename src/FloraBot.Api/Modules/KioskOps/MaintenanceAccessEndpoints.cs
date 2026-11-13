using System.Security.Claims;
using System.Text.Json;
using DotNetCore.CAP;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

internal sealed record MaintenanceAccessRequest(Guid KioskId, Guid SlotId, string Operation, Guid SessionId,
    string? Action, Guid? ExpectedBouquetId, Guid? ProductId, string? Reason, Guid? RequestId = null);
internal sealed record ConfirmMaintenanceAccessRequest(string Code);
internal sealed record AcceptedMaintenanceAccess(string Status = "ACCEPTED");

internal static class MaintenanceAccessEndpoints
{
    public static void MapMaintenanceAccess(this WebApplication app)
    {
        app.MapPost("/api/me/maintenance-access", IssueAsync)
            .RequireAuthorization("Seller").RequireRateLimiting("maintenance-code-issue").WithTags("KioskOps")
            .Produces<MaintenanceAccessIssued>().ProducesProblem(400).Produces(404);
        app.MapPost("/api/me/maintenance-open", OpenFromWebAsync)
            .RequireAuthorization("Seller").RequireRateLimiting("maintenance-code-issue").WithTags("KioskOps")
            .Produces<SlotMaintenanceSessionResponse>().Produces<ManualUnlockRetryResponse>()
            .ProducesProblem(400).Produces(404).ProducesProblem(409);

        app.MapGet("/api/kiosks/{kioskId:guid}/maintenance-access/pending", async (
            Guid kioskId, HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!IsAuthenticatedKiosk(http, kioskId)) return Results.Forbid();
            return Results.Ok(await MaintenanceAccessStore.PendingAsync(data, kioskId, ct));
        }).RequireAuthorization("Kiosk").WithTags("KioskOps").Produces<List<MaintenanceAccessPending>>();

        app.MapPost("/api/kiosks/{kioskId:guid}/maintenance-access/{id:guid}/confirm", ConfirmAsync)
            .RequireAuthorization("Kiosk").RequireRateLimiting("maintenance-code-verify").WithTags("KioskOps")
            .Produces<AcceptedMaintenanceAccess>().ProducesProblem(400).Produces(404).ProducesProblem(409);
    }

    private static async Task<IResult> OpenFromWebAsync(MaintenanceAccessRequest input, HttpContext http,
        SlotMaintenanceService service, ManualUnlockRetryService retry, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId) ||
            !Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId))
            return Results.Forbid();
        if (!IsValid(input))
            return Results.Problem(statusCode: 400, detail: "Yêu cầu mở ô không hợp lệ.");
        return input.Operation == "RETRY"
            ? await retry.RequestSellerRetryAsync(input.KioskId, input.SlotId, input.SessionId,
                new(input.RequestId!.Value), actorId, http, ct)
            : await OpenWalkInAsync(input, actorId, sellerId, service, ct);
    }

    internal static Task<IResult> OpenWalkInAsync(MaintenanceAccessRequest input, Guid actorId, Guid sellerId,
        SlotMaintenanceService service, CancellationToken ct,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<MaintenanceAccessVerification>>? verify = null) =>
        input.Operation switch
        {
            "START" => service.StartSessionAsync(input.KioskId, input.SlotId, actorId, sellerId,
                new(input.SessionId, input.Action!, input.ExpectedBouquetId, input.ProductId, input.Reason!), ct, verify),
            "OPEN_NEXT" => service.OpenNextAsync(input.KioskId, input.SlotId, input.SessionId, actorId, sellerId, ct, verify),
            "OPEN_FOR_CHECK" => service.OpenForCheckAsync(input.KioskId, input.SlotId, input.SessionId,
                actorId, sellerId, new(input.Reason!), ct, verify),
            _ => Task.FromResult<IResult>(Results.NotFound())
        };

    private static bool IsValid(MaintenanceAccessRequest input) =>
        input.KioskId != Guid.Empty && input.SlotId != Guid.Empty && input.SessionId != Guid.Empty
        && input.Operation is ("START" or "OPEN_NEXT" or "OPEN_FOR_CHECK" or "RETRY")
        && (input.Operation != "START" || input.Action is ("LOAD" or "REMOVE" or "REPLACE"))
        && (input.Operation is not ("START" or "OPEN_FOR_CHECK") || input.Reason?.Trim().Length is >= 5 and <= 500)
        && (input.Operation != "RETRY" || (input.RequestId is { } requestId && requestId != Guid.Empty));

    private static async Task<IResult> IssueAsync(MaintenanceAccessRequest input, HttpContext http,
        NpgsqlDataSource data, MaintenanceAccessStore store, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId) ||
            !Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId))
            return Results.Forbid();

        if (!IsValid(input))
            return Results.Problem(statusCode: 400, detail: "Yêu cầu xác nhận mở ô không hợp lệ.");

        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var scope = new NpgsqlCommand("""
            SELECT 1 FROM kiosk_ops.kiosks k
            JOIN kiosk_ops.slots s ON s.kiosk_id = k.id
            JOIN identity.users u ON u.seller_id = k.seller_id
            WHERE k.id = @kiosk AND s.id = @slot AND k.seller_id = @seller AND s.current_seller_id = @seller
              AND s.zone = 'WALK_IN' AND k.status = 'ONLINE'
              AND u.id = @actor AND u.role = 'SELLER' AND u.status = 'ACTIVE'
              AND (@operation = 'START' OR EXISTS (
                  SELECT 1 FROM kiosk_ops.slot_maintenance_sessions ms
                  WHERE ms.id = @session AND ms.kiosk_id = k.id AND ms.slot_id = s.id
                    AND ms.seller_id = @seller AND ms.actor_id = @actor AND ms.expires_at > CURRENT_TIMESTAMP))
            """, connection, transaction);
        scope.Parameters.AddWithValue("kiosk", input.KioskId);
        scope.Parameters.AddWithValue("slot", input.SlotId);
        scope.Parameters.AddWithValue("seller", sellerId);
        scope.Parameters.AddWithValue("actor", actorId);
        scope.Parameters.AddWithValue("operation", input.Operation);
        scope.Parameters.AddWithValue("session", input.SessionId);
        if (await scope.ExecuteScalarAsync(ct) is null) return Results.NotFound();

        var issued = await store.IssueAsync(connection, transaction,
            new(input.KioskId, input.SlotId, sellerId, actorId, "SELLER", null, input.Operation,
                JsonSerializer.Serialize(input)), ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(issued);
    }

    private static async Task<IResult> ConfirmAsync(Guid kioskId, Guid id, ConfirmMaintenanceAccessRequest input,
        HttpContext http, NpgsqlDataSource data, MaintenanceAccessStore store, SlotMaintenanceService service,
        ManualUnlockRetryService retry, ICapPublisher publisher, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!IsAuthenticatedKiosk(http, kioskId)) return Results.Forbid();
        var logger = loggerFactory.CreateLogger(typeof(MaintenanceAccessEndpoints).FullName!);
        var binding = await MaintenanceAccessStore.ReadAsync(data, id, kioskId, ct);
        if (binding is null)
        {
            logger.LogInformation("Maintenance access challenge {ChallengeId} unavailable at kiosk {KioskId}", id, kioskId);
            return Results.NotFound();
        }
        if (binding.ActorRole == "TECHNICIAN" && binding.Operation == "REPAIR")
            return await TechnicalRepairAccess.ConfirmAsync(binding, id, input.Code, data, publisher, store, ct);
        if (binding.ActorRole is not ("SELLER" or "SELLER_STAFF")) return Results.NotFound();
        if (binding.Operation == PreorderLoadingAccess.Operation)
            return await PreorderLoadingAccess.ConfirmAsync(binding, id, input.Code, data, store, publisher, ct);

        var payload = JsonSerializer.Deserialize<MaintenanceAccessRequest>(binding.Payload);
        if (payload is null || payload.Operation != binding.Operation || payload.KioskId != kioskId
            || payload.SlotId != binding.SlotId || (binding.ActorRole == "SELLER_STAFF" && binding.TaskId != payload.SessionId)) return Results.NotFound();

        async Task<MaintenanceAccessVerification> Verify(
            NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        {
            var verification = await store.VerifyAsync(connection, transaction, id, kioskId, input.Code, token);
            logger.LogInformation(
                "Maintenance code verification {Verification} for session {SessionId} kiosk {KioskId} slot {SlotId} operation {Operation}",
                verification, payload.SessionId, kioskId, binding.SlotId, payload.Operation);
            return verification;
        }

        var response = payload.Operation == "RETRY"
            ? await retry.RequestSellerRetryAsync(kioskId, binding.SlotId, payload.SessionId,
                new(payload.RequestId!.Value), binding.ActorId, http, ct, Verify)
            : await OpenWalkInAsync(payload, binding.ActorId, binding.SellerId, service, ct, Verify);
        logger.LogInformation(
            "Maintenance access confirmation for session {SessionId} kiosk {KioskId} slot {SlotId} operation {Operation} returned HTTP {StatusCode}",
            payload.SessionId, kioskId, binding.SlotId, payload.Operation, (response as IStatusCodeHttpResult)?.StatusCode);
        return response is IStatusCodeHttpResult { StatusCode: 200 }
            ? Results.Ok(new AcceptedMaintenanceAccess())
            : response;
    }

    private static bool IsAuthenticatedKiosk(HttpContext http, Guid kioskId) =>
        Guid.TryParse(http.User.FindFirstValue("kiosk_id"), out var authenticatedId) && kioskId == authenticatedId;
}
