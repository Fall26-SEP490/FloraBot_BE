using System.Text.Json;
using FloraBot.Api.Modules.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FloraBot.Api.Tests;

public sealed class AdvisorCacheTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static IConfiguration Config(string model = "gemini-2.5-flash-lite", string version = "1") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["GEMINI_MODEL"] = model, ["AI_CACHE_VERSION"] = version, ["AI_SERVICE_URL"] = "http://ai:8000" }).Build();
    private static readonly AiSuggestRequest Input = new("private-session", "MOTHER", "41_60", "BIRTHDAY", "WARM", 300000);
    private static AdvisorCandidate[] Candidates() => [new(Guid.NewGuid(), Guid.NewGuid(), "Flower", 250000, ["MOTHER"], "Description", "Warm")];

    [Fact]
    public async Task CacheIsShortLivedAnonymousAndChangesWithInventoryAndModel()
    {
        using var scope = factory.Services.CreateScope();
        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
        var cache = new AdvisorCache(redis, Config());
        var candidates = Candidates(); var kiosk = Guid.NewGuid();
        var key = cache.Key(kiosk, Input, candidates)!;
        Assert.Equal(key, cache.Key(kiosk, Input with { p_session = "another-private-session" }, candidates));
        Assert.NotEqual(key, cache.Key(Guid.NewGuid(), Input, candidates));
        Assert.NotEqual(key, cache.Key(kiosk, Input with { p_budget = 299999 }, candidates));
        Assert.NotEqual(key, cache.Key(kiosk, Input, [candidates[0] with { Description = "Changed" }]));
        Assert.NotEqual(key, cache.Key(kiosk, Input, [candidates[0] with { BrandTone = "Formal" }]));
        Assert.NotEqual(key, cache.Key(kiosk, Input, [candidates[0] with { Price = 250001 }]));
        Assert.NotEqual(key, new AdvisorCache(redis, Config(version: "2")).Key(kiosk, Input, candidates));
        Assert.NotEqual(key, new AdvisorCache(redis, Config(model: "gemini-2.5-flash")).Key(kiosk, Input, candidates));
        Assert.Null(cache.Key(kiosk, Input, []));
        Assert.Null(new AdvisorCache(redis, Config(model: "")).Key(kiosk, Input, candidates));
        var answer = new AdvisorAnswer([new(candidates[0].BouquetId, "Reason", "Card")], "gemini-2.5-flash-lite", 20);
        await cache.WriteAsync(key, answer, CancellationToken.None);
        var restored = await cache.ReadAsync(key, candidates, CancellationToken.None);
        Assert.Equal(answer.Model, restored!.Model); Assert.Equal(answer.Suggestions, restored.Suggestions);
        Assert.InRange((await redis.GetDatabase().KeyTimeToLiveAsync(key))!.Value.TotalSeconds, 1, 60);
        var stored = (await redis.GetDatabase().StringGetAsync(key)).ToString();
        Assert.DoesNotContain(Input.p_session, stored);
        Assert.DoesNotContain("private-session", key);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("foreign")]
    [InlineData("unsafe")]
    [InlineData("model")]
    [InlineData("oversized")]
    public async Task CorruptCacheNeverBypassesResponseValidation(string defect)
    {
        using var scope = factory.Services.CreateScope();
        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
        var cache = new AdvisorCache(redis, Config()); var candidates = Candidates();
        var key = cache.Key(Guid.NewGuid(), Input, candidates)!;
        var value = defect switch
        {
            "malformed" => "not json",
            "oversized" => new string('x', 16385),
            _ => JsonSerializer.Serialize(new AdvisorAnswer([new(defect == "foreign" ? Guid.NewGuid() : candidates[0].BouquetId, defect == "unsafe" ? "https://untrusted.example" : "Reason", "Card")], defect == "model" ? "other-model" : "gemini-2.5-flash-lite", 20))
        };
        await redis.GetDatabase().StringSetAsync(key, value, TimeSpan.FromSeconds(60));
        Assert.Null(await cache.ReadAsync(key, candidates, CancellationToken.None));
    }

    [Fact]
    public async Task CacheOutageIsAMissAndDoesNotBlockTheAdvisor()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=50,asyncTimeout=50,connectRetry=0");
        var cache = new AdvisorCache(redis, Config()); var candidates = Candidates();
        var key = cache.Key(Guid.NewGuid(), Input, candidates)!;
        Assert.Null(await cache.ReadAsync(key, candidates, CancellationToken.None));
        await cache.WriteAsync(key, new([new(candidates[0].BouquetId, "Reason", "Card")], "gemini-2.5-flash-lite", 20), CancellationToken.None);
    }
}
