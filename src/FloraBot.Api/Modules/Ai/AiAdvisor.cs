using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using FloraBot.Api.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Ai;

public sealed record GiftSuggestionResponse(Guid BouquetId, Guid ProductId, string Name, long Price, string Reason, string? CardMessage);
public sealed record GiftSurveyResponse(Guid Id, string Source, IReadOnlyList<GiftSuggestionResponse> Suggestions);

public sealed class AiAdvisor(NpgsqlDataSource data, AdvisorClient advisor, AdvisorCache cache)
{
    public async Task<IResult> SuggestAsync(AiSuggestRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrWhiteSpace(input.p_session) || input.p_session.Length > 128 || input.p_budget is < 1 or > 1_000_000_000 ||
            new[] { input.p_recipient, input.p_age, input.p_occasion, input.p_tone }.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 40))
            return Results.Problem(statusCode: 400, detail: "Vui lòng kiểm tra năm câu trả lời và ngân sách.");
        // Commit the authoritative rule-based result before any external network call.
        var result = await executor.ExecuteAsync("ai_suggest", "shopping", JsonSerializer.SerializeToElement(input), http, ct);
        if (result is not Ok<FlowResult> { Value.Result: { } value } || !value.TryGetGuid(out var survey)) return result;
        var kiosk = Guid.Parse(http.Request.RouteValues["kioskId"]!.ToString()!);
        var candidates = await CandidatesAsync(kiosk, input.p_budget, input.p_occasion, ct);
        var key = cache.Key(kiosk, input, candidates);
        var cacheTime = Stopwatch.StartNew();
        var answer = await cache.ReadAsync(key, candidates, ct);
        var cached = answer is not null;
        answer ??= await advisor.SuggestAsync(input, candidates, ct);
        if (answer is not null)
        {
            var results = answer.Suggestions.Select((s, index) => new
            {
                bouquet_id = s.BouquetId,
                product_id = candidates.Single(c => c.BouquetId == s.BouquetId).ProductId,
                rank = index + 1,
                reason = s.Reason,
                card_message = s.CardMessage
            });
            await using var connection = await data.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var record = new NpgsqlCommand("SELECT flow.ai_record_llm(@survey,@results,@model,@latency)", connection, transaction);
            record.Parameters.AddWithValue("survey", survey);
            record.Parameters.AddWithValue("results", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(results));
            record.Parameters.AddWithValue("model", answer.Model);
            record.Parameters.AddWithValue("latency", cached ? (int)cacheTime.ElapsedMilliseconds : answer.LatencyMs);
            // SQL rechecks stock and budget after the provider call; rejection retains FALLBACK.
            var accepted = (bool)(await record.ExecuteScalarAsync(ct))!;
            if (accepted && cached)
            {
                await using var source = new NpgsqlCommand("UPDATE ai.gift_surveys SET source='CACHE' WHERE id=@survey", connection, transaction);
                source.Parameters.AddWithValue("survey", survey);
                await source.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            if (accepted && !cached) await cache.WriteAsync(key, answer, ct);
        }
        return result;
    }

    private async Task<AdvisorCandidate[]> CandidatesAsync(Guid kiosk, long budget, string occasion, CancellationToken ct)
    {
        await using var command = data.CreateCommand("SELECT * FROM flow.ai_candidates(@kiosk,@budget,@occasion) ORDER BY price DESC,bouquet_id LIMIT 30");
        command.Parameters.AddWithValue("kiosk", kiosk);
        command.Parameters.AddWithValue("budget", budget);
        command.Parameters.AddWithValue("occasion", occasion);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AdvisorCandidate>();
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(3), reader.GetInt64(4), reader.GetFieldValue<string[]>(5),
                reader.IsDBNull(6) ? "" : reader.GetString(6), reader.IsDBNull(7) ? "" : reader.GetString(7)));
        return result.ToArray();
    }

    public static void MapReads(WebApplication app)
    {
        app.MapGet("/api/kiosks/{kioskId:guid}/surveys/{surveyId:guid}", async (Guid kioskId, Guid surveyId, HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
            var customer = http.User.IsInRole("CUSTOMER") ? Guid.Parse(http.User.FindFirstValue("sub")!) : (Guid?)null;
            await using var command = data.CreateCommand("""
                SELECT s.source, coalesce(jsonb_agg(jsonb_build_object(
                    'bouquetId',c.bouquet_id,'productId',c.product_id,'name',c.name,'price',c.price,
                    'reason',e.value->>'reason','cardMessage',e.value->>'card_message') ORDER BY e.ordinality)
                    FILTER (WHERE c.bouquet_id IS NOT NULL),'[]'::jsonb)::text
                FROM ai.gift_surveys s
                LEFT JOIN LATERAL jsonb_array_elements(s.results) WITH ORDINALITY e ON true
                LEFT JOIN LATERAL flow.ai_candidates(s.kiosk_id,s.budget_max,s.occasion) c ON c.bouquet_id=(e.value->>'bouquet_id')::uuid
                WHERE s.id=@survey AND s.kiosk_id=@kiosk AND s.customer_id IS NOT DISTINCT FROM @customer
                GROUP BY s.id,s.source
                """);
            command.Parameters.AddWithValue("survey", surveyId);
            command.Parameters.AddWithValue("kiosk", kioskId);
            command.Parameters.AddWithValue("customer", NpgsqlDbType.Uuid, customer.HasValue ? customer.Value : DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            var rows = JsonSerializer.Deserialize<List<GiftSuggestionResponse>>(reader.GetString(1), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var safe = rows.Select(row => row with
            {
                Reason = AdvisorClient.SafeText(row.Reason, 300) ? row.Reason : "Một gợi ý trong ngân sách và phù hợp với dịp bạn chọn.",
                CardMessage = AdvisorClient.SafeText(row.CardMessage, 200) ? row.CardMessage : null
            }).ToArray();
            return Results.Ok(new GiftSurveyResponse(surveyId, reader.GetString(0), safe));
        }).WithName("read_gift_survey").WithTags("Ai").Produces<GiftSurveyResponse>().RequireAuthorization("Shopping");
    }
}
