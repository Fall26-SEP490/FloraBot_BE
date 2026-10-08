using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FloraBot.Api.Modules.Ai;

public sealed record AdvisorCandidate(Guid BouquetId, Guid ProductId, string Name, long Price, string[] Tags, string Description, string BrandTone);
public sealed record AdvisorSuggestion(Guid BouquetId, string Reason, string CardMessage);
public sealed record AdvisorAnswer(AdvisorSuggestion[] Suggestions, string Model, int LatencyMs);

public sealed class AdvisorClient(HttpClient client, IConfiguration config)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Regex Unsafe = new(@"https?://|www\.|ignore|bỏ qua hướng dẫn|system prompt|<[a-z/]|[\x00-\x08\x0b\x0c\x0e-\x1f]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static bool SafeText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !Unsafe.IsMatch(value);

    public static bool Valid(AdvisorAnswer? answer, AdvisorCandidate[] candidates) =>
        answer?.Suggestions is { Length: >= 1 and <= 3 } &&
        answer.Suggestions.All(s => s is not null && candidates.Any(c => c.BouquetId == s.BouquetId) && SafeText(s.Reason, 300) && SafeText(s.CardMessage, 200)) &&
        answer.Suggestions.Select(s => s.BouquetId).Distinct().Count() == answer.Suggestions.Length &&
        SafeText(answer.Model, 100) && answer.LatencyMs is >= 0 and <= 2000;

    public async Task<AdvisorAnswer?> SuggestAsync(AiSuggestRequest input, AdvisorCandidate[] candidates, CancellationToken ct)
    {
        var token = config["AI_SERVICE_TOKEN"];
        if (candidates.Length == 0 || token?.Length is not >= 32 ||
            !Uri.TryCreate(config["AI_SERVICE_URL"], UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1800));
        var elapsed = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "/suggest"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new
            {
                survey = new { recipient = input.p_recipient, age = input.p_age, occasion = input.p_occasion, tone = input.p_tone, budget = input.p_budget },
                candidates = candidates.Select(c => new { bouquet_id = c.BouquetId, name = c.Name, price = c.Price, tags = c.Tags, description = c.Description, brand_tone = c.BrandTone })
            });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            {
                if (buffer.Length + read > 16384) return null;
                buffer.Write(bytes, 0, read);
            }
            var answer = JsonSerializer.Deserialize<AdvisorAnswer>(buffer.ToArray(), Json);
            if (!Valid(answer, candidates)) return null;
            return answer! with { LatencyMs = (int)elapsed.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }
}
