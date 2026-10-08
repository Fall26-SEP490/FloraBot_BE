// Generated from the supplied SQL by scripts/generate-flows.mjs.
#nullable enable
using System.Text.Json;
using FloraBot.Api.Infrastructure;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record ReleaseSlotRequest(
    Guid p_slot,
    string p_reason);

public sealed record StockBouquetRequest(
    Guid p_batch,
    Guid p_product,
    Guid p_slot,
    string p_qr);

public sealed record ReturnToSellerRequest(
    Guid p_slot,
    string p_photo_url,
    string p_reason,
    bool? p_damaged = null);

public sealed record ReportDeviceFaultRequest(
    Guid p_kiosk,
    Guid p_slot,
    string p_desc,
    string p_photo);

public sealed record AssignSlotRequest(
    Guid p_slot,
    Guid p_seller);

public sealed record AdminDisposeSlotRequest(
    Guid p_slot,
    string p_note);

public sealed record ResolveDeviceFaultRequest(
    Guid p_dispute,
    string p_note);

public sealed record SetKioskStatusRequest(
    Guid p_kiosk,
    string p_status);

public sealed record AdminCloseDoorRequest(
    Guid p_cmd,
    string p_note);

public sealed record SetCfgRequest(
    string p_key,
    decimal p_value);

public sealed record KioskHeartbeatRequest(
);

public sealed record DeviceEventRequest(
    Guid p_cmd,
    string p_event);

public static class KioskOpsEndpoints
{
    public static void MapKioskOps(this WebApplication app)
    {
        app.MapPost("/api/sellers/{sellerId:guid}/flows/release_slot", async (ReleaseSlotRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("release_slot", "seller", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("release_slot").WithTags("KioskOps").Produces<FlowResult>().RequireAuthorization("Merchant", "SameSeller");
        app.MapPost("/api/sellers/{sellerId:guid}/flows/stock_bouquet", async (StockBouquetRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("stock_bouquet", "seller", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("stock_bouquet").WithTags("KioskOps").Produces<FlowResult>().RequireAuthorization("Merchant", "SameSeller");
        app.MapPost("/api/sellers/{sellerId:guid}/flows/return_to_seller", async (ReturnToSellerRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("return_to_seller", "seller", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("return_to_seller").WithTags("KioskOps").Produces<FlowResult>().RequireAuthorization("Merchant", "SameSeller");
        app.MapPost("/api/sellers/{sellerId:guid}/flows/report_device_fault", async (ReportDeviceFaultRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("report_device_fault", "seller", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("report_device_fault").WithTags("KioskOps").Produces<FlowResult>().RequireAuthorization("Merchant", "SameSeller");
        app.MapPost("/api/admin/flows/assign_slot", async (AssignSlotRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("assign_slot", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("assign_slot").WithTags("KioskOps").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/admin_dispose_slot", async (AdminDisposeSlotRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("admin_dispose_slot", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("admin_dispose_slot").WithTags("KioskOps").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/resolve_device_fault", async (ResolveDeviceFaultRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("resolve_device_fault", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("resolve_device_fault").WithTags("KioskOps").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/set_kiosk_status", async (SetKioskStatusRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("set_kiosk_status", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("set_kiosk_status").WithTags("KioskOps").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/admin_close_door", async (AdminCloseDoorRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("admin_close_door", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("admin_close_door").WithTags("KioskOps").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/set_cfg", async (SetCfgRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("set_cfg", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("set_cfg").WithTags("KioskOps").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/kiosks/{kioskId:guid}/flows/kiosk_heartbeat", async (KioskHeartbeatRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("kiosk_heartbeat", "kiosk", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("kiosk_heartbeat").WithTags("KioskOps").Produces<FlowResult>().RequireAuthorization("Kiosk");
        app.MapPost("/api/kiosks/{kioskId:guid}/flows/device_event", async (DeviceEventRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("device_event", "kiosk", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("device_event").WithTags("KioskOps").Produces<FlowResult>().RequireAuthorization("Kiosk");
    }
}
