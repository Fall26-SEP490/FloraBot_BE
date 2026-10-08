// Generated from the supplied SQL by scripts/generate-flows.mjs.
#nullable enable
using System.Text.Json;
using FloraBot.Api.Infrastructure;

namespace FloraBot.Api.Modules.Notify;

public sealed record AttachRequest(
    string p_service,
    string p_owner_type,
    Guid p_owner,
    string p_url,
    string p_phase);

public static class NotifyEndpoints
{
    public static void MapNotify(this WebApplication app)
    {
        app.MapPost("/api/admin/flows/attach", async (AttachRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("attach", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("attach").WithTags("Notify").Produces<FlowResult>().RequireAuthorization("Admin");
    }
}
