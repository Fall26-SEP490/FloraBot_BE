using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloraBot.Api.Modules.Ai;

public sealed class SeasonalForecastClient(HttpClient client, IConfiguration config)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private const int MaxResponseBytes = 2 * 1024 * 1024; // 2 MiB

    private static bool SafeExplanation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > 1000) return false;
        if (text.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public static bool ValidateResponse(
        UpstreamForecastResponse? response,
        UpstreamForecastRequest request,
        HashSet<Guid> expectedProductIds,
        string expectedForecastStart,
        string expectedForecastEnd,
        string expectedSeason)
    {
        if (response is null) return false;
        if (response.ContractVersion != "seasonal-v1") return false;
        if (response.AggregationLevel != "PRODUCT") return false;
        if (response.DataSource != request.DataSource) return false;
        if (response.HistoryStart != request.HistoryStart) return false;
        if (response.HistoryEnd != request.HistoryEnd) return false;
        if (response.ForecastStart != expectedForecastStart) return false;
        if (response.ForecastEnd != expectedForecastEnd) return false;
        if (response.Season != expectedSeason) return false;
        if (response.Products is null) return false;
        if (response.Products.Count != expectedProductIds.Count) return false;

        var expectedCounts = expectedProductIds.ToDictionary(id => id, id => new
        {
            Invoices = request.Observations.Where(o => o.ProductId == id).Sum(o => o.InvoiceCount),
            Units = request.Observations.Where(o => o.ProductId == id).Sum(o => o.Units)
        });

        var seenIds = new HashSet<Guid>();
        foreach (var p in response.Products)
        {
            if (p is null) return false;
            if (!expectedProductIds.Contains(p.ProductId)) return false;
            if (!seenIds.Add(p.ProductId)) return false;

            if (!expectedCounts.TryGetValue(p.ProductId, out var expected)) return false;
            if (p.ObservedInvoiceCount != expected.Invoices) return false;
            if (p.ObservedUnits != expected.Units) return false;
            if (p.SameSeasonInvoiceCount < 0 || p.SameSeasonInvoiceCount > p.ObservedInvoiceCount) return false;

            if (p.ModelStatus == "INSUFFICIENT_HISTORY")
            {
                if (p.ForecastInvoiceCount.HasValue || p.ForecastUnits.HasValue) return false;
                if (p.AnnualSeasonalityValidated) return false;
            }
            else if (p.ModelStatus is "POISSON_REGRESSION" or "WEEKDAY_BASELINE")
            {
                if (!p.ForecastInvoiceCount.HasValue || !double.IsFinite(p.ForecastInvoiceCount.Value) || p.ForecastInvoiceCount.Value < 0) return false;
                if (!p.ForecastUnits.HasValue || !double.IsFinite(p.ForecastUnits.Value) || p.ForecastUnits.Value < 0) return false;
                if (p.Validation is null) return false;
                if (p.Validation.TrainingDays < 56 || p.Validation.HoldoutDays < 14) return false;
                if (p.ModelStatus == "POISSON_REGRESSION" && !p.Validation.Mae.HasValue) return false;
                if (p.Validation.Mae.HasValue && (!double.IsFinite(p.Validation.Mae.Value) || p.Validation.Mae.Value < 0)) return false;
                if (!p.Validation.BaselineMae.HasValue || !double.IsFinite(p.Validation.BaselineMae.Value) || p.Validation.BaselineMae.Value < 0) return false;
                if (p.Validation.Wape.HasValue && (!double.IsFinite(p.Validation.Wape.Value) || p.Validation.Wape.Value < 0)) return false;
                if (p.Validation.BaselineWape.HasValue && (!double.IsFinite(p.Validation.BaselineWape.Value) || p.Validation.BaselineWape.Value < 0)) return false;
            }
            else
            {
                return false;
            }

            if (p.AnnualSeasonalityValidated)
            {
                return false; // seasonal-v1 has no annual backtest evidence; history length alone is insufficient.
            }

            if (p.ExplanationSource is not null && p.ExplanationSource is not ("GEMINI" or "FALLBACK"))
                return false;

            if (!SafeExplanation(p.Explanation))
                return false;
        }

        return true;
    }

    public async Task<UpstreamForecastResponse?> ForecastAsync(
        UpstreamForecastRequest request,
        HashSet<Guid> expectedProductIds,
        string expectedForecastStart,
        string expectedForecastEnd,
        string expectedSeason,
        CancellationToken ct)
    {
        var token = config["AI_SERVICE_TOKEN"];
        if (token?.Length is not >= 32) return null;

        var urlStr = config["AI_SERVICE_URL"];
        if (string.IsNullOrWhiteSpace(urlStr) ||
            !Uri.TryCreate(urlStr, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0)
        {
            return null;
        }

        // Budget safe total 12s to cover Python CPU fitting + 8s Gemini bounded call
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        try
        {
            using var reqMessage = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "/seasonal/forecast"));
            reqMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            reqMessage.Content = JsonContent.Create(request, options: Json);

            using var response = await client.SendAsync(reqMessage, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes) return null;
                buffer.Write(bytes, 0, read);
            }

            var result = JsonSerializer.Deserialize<UpstreamForecastResponse>(buffer.ToArray(), Json);
            if (!ValidateResponse(result, request, expectedProductIds, expectedForecastStart, expectedForecastEnd, expectedSeason))
                return null;

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }
}
