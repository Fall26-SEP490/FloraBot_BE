using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace FloraBot.Api.Modules.Ai;

public sealed class AdvisorCache(IConnectionMultiplexer redis, IConfiguration config)
{
    public string? Key(Guid kiosk, AiSuggestRequest input, AdvisorCandidate[] candidates)
    {
        var model = config["GEMINI_MODEL"];
        if (candidates.Length == 0 || string.IsNullOrWhiteSpace(model)) return null;
        var value = JsonSerializer.Serialize(new
        {
            kiosk,
            model,
            version = config["AI_CACHE_VERSION"] ?? "1",
            endpoint = config["AI_SERVICE_URL"],
            input.p_recipient,
            input.p_age,
            input.p_occasion,
            input.p_tone,
            input.p_budget,
            candidates = candidates.OrderBy(c => c.BouquetId)
        });
        return "advisor:v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public async Task<AdvisorAnswer?> ReadAsync(string? key, AdvisorCandidate[] candidates, CancellationToken ct)
    {
        if (key is null) return null;
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key).WaitAsync(TimeSpan.FromMilliseconds(75), ct);
            if (value.IsNullOrEmpty || value.Length() > 16384) return null;
            var answer = JsonSerializer.Deserialize<AdvisorAnswer>(value.ToString());
            return AdvisorClient.Valid(answer, candidates) && answer!.Model == config["GEMINI_MODEL"] ? answer : null;
        }
        catch (RedisException) { return null; }
        catch (TimeoutException) { return null; }
        catch (JsonException) { return null; }
    }

    public async Task WriteAsync(string? key, AdvisorAnswer answer, CancellationToken ct)
    {
        if (key is null || answer.Model != config["GEMINI_MODEL"]) return;
        try
        {
            await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(answer), TimeSpan.FromSeconds(60))
                .WaitAsync(TimeSpan.FromMilliseconds(75), ct);
        }
        catch (RedisException) { }
        catch (TimeoutException) { }
    }
}
