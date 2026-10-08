// Generated from the supplied SQL by scripts/generate-flows.mjs.
#nullable enable
using System.Text.Json;
using FloraBot.Api.Infrastructure;

namespace FloraBot.Api.Modules.Ordering;

public sealed record KioskCheckoutRequest(
    Guid[] p_bouquets,
    JsonElement? p_accessories = null,
    long? p_points = null,
    string? p_ecard = null);

public sealed record RequestPickupRequest(
    Guid p_order,
    string p_tracking);

public sealed record OpenDisputeRequest(
    Guid p_order,
    string p_tracking,
    string p_reason,
    string p_photo);

public sealed record SubmitRefundInfoRequest(
    Guid p_order,
    string p_tracking,
    string p_bank,
    string p_account_enc,
    string p_holder);

public sealed record ResolveDisputeRequest(
    Guid p_dispute,
    long p_refund,
    string p_decision);

public static class OrderingEndpoints
{
    public static void MapOrdering(this WebApplication app)
    {
        app.MapPost("/api/kiosks/{kioskId:guid}/flows/kiosk_checkout", async (KioskCheckoutRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("kiosk_checkout", "shopping", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("kiosk_checkout").WithTags("Ordering").Produces<FlowResult>().RequireAuthorization("Shopping");
        app.MapPost("/api/kiosks/{kioskId:guid}/flows/request_pickup", async (RequestPickupRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("request_pickup", "shopping", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("request_pickup").WithTags("Ordering").Produces<FlowResult>().RequireAuthorization("Shopping");
        app.MapPost("/api/receipts/flows/open_dispute", async (OpenDisputeRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("open_dispute", "receipt", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("open_dispute").WithTags("Ordering").Produces<FlowResult>().AllowAnonymous().RequireRateLimiting("receipt");
        app.MapPost("/api/receipts/flows/submit_refund_info", async (SubmitRefundInfoRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("submit_refund_info", "receipt", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("submit_refund_info").WithTags("Ordering").Produces<FlowResult>().AllowAnonymous().RequireRateLimiting("receipt");
        app.MapPost("/api/admin/flows/resolve_dispute", async (ResolveDisputeRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("resolve_dispute", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("resolve_dispute").WithTags("Ordering").Produces<FlowResult>().RequireAuthorization("Admin");
    }
}
