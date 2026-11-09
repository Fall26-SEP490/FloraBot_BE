using System.Security.Claims;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public static class Esp32ProvisioningEndpoints
{
    public static void MapEsp32Provisioning(this WebApplication app)
    {
        Describe(app.MapPost("/api/operations/kiosks/{id:guid}/esp32-config/provision",
            (Guid id, IEsp32ConfigurationSessions sessions, Esp32ProvisioningStore store, NpgsqlDataSource data,
                HttpContext http, CancellationToken ct) => Handle(id, "OPERATIONS_MANAGER", sessions, store, data, http, ct)));
        Describe(app.MapPost("/api/staff/kiosk-installations/{installationId:guid}/esp32-config/provision",
            (Guid installationId, IEsp32ConfigurationSessions sessions, Esp32ProvisioningStore store, NpgsqlDataSource data,
                HttpContext http, CancellationToken ct) => Handle(installationId, "TECHNICIAN", sessions, store, data, http, ct)));
    }

    private static void Describe(RouteHandlerBuilder route) => route.RequireAuthorization("Account")
        .RequireRateLimiting("auth").WithTags("Esp32Configuration")
        .WithSummary("Queue or retry ESP32 provisioning for the currently assigned kiosk/installation")
        .WithDescription("No request body or secrets. Current Manager/assigned live Technician only. New enrollment queues automatically when enabled. Retry keeps the original device credentials; READY/in-flight work is not reset. Poll the existing esp32-config GET. No heartbeat or ONLINE status is fabricated.")
        .Produces<Esp32ProvisioningStatus>(202).ProducesProblem(401).ProducesProblem(403)
        .ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);

    private static async Task<IResult> Handle(Guid resource, string role, IEsp32ConfigurationSessions sessions,
        Esp32ProvisioningStore store, NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
        await using var session = await sessions.OpenAsync(ct);
        Guid? auditKiosk = role == "TECHNICIAN" ? null : resource;
        Guid? installation = role == "TECHNICIAN" ? resource : null;
        async Task<IResult> Deny(string code, int status)
        {
            await session.AuditAndCommitAsync(actor, auditKiosk, installation, false, code, ct);
            return Results.Problem(statusCode: status, title: code,
                extensions: new Dictionary<string, object?> { ["code"] = code });
        }
        if (http.User.Identity?.IsAuthenticated != true || !http.User.IsInRole(role)
            || http.User.HasClaim(c => c.Type == "kiosk_id"))
            return await Deny("ESP32_CONFIG_FORBIDDEN", 403);
        var access = await session.ResolveAsync(resource, actor, role, ct);
        auditKiosk = access.AuditKioskId;
        if (!access.ActorActive) return await Deny("ESP32_CONFIG_FORBIDDEN", 403);
        if (access.Kiosk is not { } kiosk) return await Deny("ESP32_CONFIG_NOT_FOUND", 404);
        if (!store.Enabled) return await Deny("ESP32_AUTO_PROVISIONING_DISABLED", 409);
        if (!DeviceProtocol.ValidHardwareId(kiosk.HardwareId))
            return await Deny("ESP32_CONFIG_NOT_PROVISIONED", 409);

        await using (var connection = await data.OpenConnectionAsync(ct))
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            await store.RequeueAsync(connection, transaction, kiosk.Id, actor, ct);
            await transaction.CommitAsync(ct);
        }
        var status = await store.GetStatusAsync(kiosk.Id, ct);
        await session.AuditAndCommitAsync(actor, kiosk.Id, installation, false, "PROVISIONING_QUEUED", ct);
        var location = role == "TECHNICIAN"
            ? $"/api/staff/kiosk-installations/{resource}/esp32-config"
            : $"/api/operations/kiosks/{resource}/esp32-config";
        return Results.Accepted(location, status);
    }
}
