// Generated from the supplied SQL by scripts/generate-flows.mjs.
#nullable enable
using System.Text.Json;
using FloraBot.Api.Infrastructure;

namespace FloraBot.Api.Modules.Ai;

public sealed record AiSuggestRequest(
    string p_session,
    string p_recipient,
    string p_age,
    string p_occasion,
    string p_tone,
    long p_budget);

public static class AiEndpoints
{
    public static void MapAi(this WebApplication app)
    {
        app.MapPost("/api/kiosks/{kioskId:guid}/flows/ai_suggest", async (AiSuggestRequest input, HttpContext http, FlowExecutor executor, AiAdvisor advisor, CancellationToken ct) =>
            await advisor.SuggestAsync(input, http, executor, ct))
            .WithName("ai_suggest").WithTags("Ai").Produces<FlowResult>().RequireAuthorization("Shopping").RequireRateLimiting("advisor");
    }
}
