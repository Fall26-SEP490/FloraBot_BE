using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace FloraBot.Api.Modules.Ai;

public sealed record FlowerChatInput(string Question, string? Mode = null, long? Budget = null, Guid? KioskId = null);
public sealed record FlowerChatSuggestion(Guid Id, Guid ProductId, Guid KioskId, string Name, long Price, string Mode, string Reason,
    string? Photo, decimal? ProductRating, long ProductReviews, decimal? ShopRating, long ShopReviews);
public sealed record FlowerChatResponse(string Intent, string Message, IReadOnlyList<FlowerChatSuggestion> Suggestions);

public sealed class FlowerChat(HttpClient client, IConfiguration config, NpgsqlDataSource data)
{
    private const string Redirect = "FloraBot có thể giúp bạn chọn hoa, tìm mẫu theo dịp hoặc hướng dẫn đặt hoa. Bạn muốn mình gợi ý theo dịp hay ngân sách ạ?";
    private const string Unavailable = "FloraBot chưa thể tư vấn ngay lúc này. Bạn thử lại hoặc xem danh mục hoa nhé.";
    private static readonly Regex BudgetText = new(
        @"(?:dưới|tối đa|không quá|ngân sách)\s*:?\s*(?<amount>\d{1,9}(?:[.,]\d{3}){0,2})\s*(?<unit>nghìn|ngàn|k|triệu|tr|đồng|đ|vnd)?(?![\d.,])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private sealed record Candidate(Guid Id, Guid ProductId, Guid KioskId, string Name, string Description,
        long Price, string[] Tags, string Mode, string? Photo, decimal? ProductRating, long ProductReviews,
        decimal? ShopRating, long ShopReviews);
    private sealed record ModelSuggestion(Guid Id, string? Reason);
    private sealed record ModelAnswer(string? Intent, string? Message, ModelSuggestion[]? Suggestions);

    public static void Map(WebApplication app) =>
        app.MapPost("/api/shop/flower-chat", (FlowerChatInput input, HttpContext http, FlowerChat chat, CancellationToken ct) =>
            chat.ReadAsync(input, http, ct))
            .AllowAnonymous().RequireRateLimiting("flower-advisor").WithTags("Ai").Produces<FlowerChatResponse>().Produces(400);

    private async Task<IResult> ReadAsync(FlowerChatInput input, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var question = input.Question?.Trim();
        if (question is null || question.Length is 0 or > 500 || input.Mode is not (null or "WALK_IN" or "PREORDER")
            || input.Budget is < 1 or > 1_000_000_000)
            return Results.BadRequest();

        var seller = http.User.IsInRole("SELLER");
        input = ApplyQuestionConstraints(input, question);
        if (!seller && input.Mode == "WALK_IN" && input.KioskId is null)
            return Results.Ok(new FlowerChatResponse("FLOWERS", "Bạn chọn kiosk để FloraBot xem hoa đang có tại tủ nhé.", []));
        var candidates = seller ? [] : await CandidatesAsync(input, question, ct);
        var answer = await AskAsync(question, seller, candidates, ct);
        if (answer?.Intent == "OFF_TOPIC")
            return Results.Ok(new FlowerChatResponse("OFF_TOPIC", Redirect, []));
        if (answer?.Intent != "FLOWERS" || !AdvisorClient.SafeText(answer.Message, 1000)
            || answer.Suggestions is not { Length: <= 3 } matches
            || matches.Any(m => m is null || !AdvisorClient.SafeText(m.Reason, 300))
            || matches.Select(m => m.Id).Distinct().Count() != matches.Length)
            return Results.Ok(new FlowerChatResponse("UNAVAILABLE", Unavailable, []));

        var byId = candidates.ToDictionary(c => c.Id);
        if (matches.Any(m => !byId.ContainsKey(m.Id)))
            return Results.Ok(new FlowerChatResponse("UNAVAILABLE", Unavailable, []));
        if (matches.Length > 0)
            byId = (await CandidatesAsync(input, question, ct)).ToDictionary(c => c.Id);
        var suggestions = matches.Where(m => byId.ContainsKey(m.Id)).Select(m =>
        {
            var c = byId[m.Id];
            return new FlowerChatSuggestion(c.Id, c.ProductId, c.KioskId, c.Name, c.Price, c.Mode, m.Reason!,
                c.Photo, c.ProductRating, c.ProductReviews, c.ShopRating, c.ShopReviews);
        }).ToArray();
        return Results.Ok(new FlowerChatResponse("FLOWERS", answer.Message!, suggestions));
    }

    private static FlowerChatInput ApplyQuestionConstraints(FlowerChatInput input, string question)
    {
        var text = question.ToLowerInvariant();
        var walkIn = text.Contains("lấy ngay") || text.Contains("lấy tại tủ") || text.Contains("mua tại tủ")
            || text.Contains("walk-in") || text.Contains("walk in");
        var preorder = text.Contains("đặt trước") || text.Contains("pre-order") || text.Contains("preorder");
        var mode = input.Mode ?? (walkIn && !preorder ? "WALK_IN" : "PREORDER");
        var budget = input.Budget;
        var amount = BudgetText.Match(text);
        if (!budget.HasValue && amount.Success &&
            long.TryParse(amount.Groups["amount"].Value.Replace(".", "").Replace(",", ""), out var number))
        {
            var multiplier = amount.Groups["unit"].Value switch
            {
                "k" or "nghìn" or "ngàn" => 1000L,
                "tr" or "triệu" => 1_000_000L,
                _ => 1L
            };
            if (number > 0 && number <= 1_000_000_000L / multiplier)
                budget = number * multiplier;
        }
        return input with { Mode = mode, Budget = budget };
    }

    private async Task<Candidate[]> CandidatesAsync(FlowerChatInput input, string question, CancellationToken ct)
    {
        await using var command = data.CreateCommand("""
            WITH product_reviews AS (
                SELECT p.seller_id, r.product_id, sum(r.stars) AS total, count(*) AS votes
                FROM catalog.flower_product_reviews r JOIN catalog.flower_products p ON p.id=r.product_id
                WHERE @mode='PREORDER'
                GROUP BY p.seller_id,r.product_id
            ), shop_reviews AS (
                SELECT seller_id,sum(total)/sum(votes) AS rating,sum(votes)::bigint AS votes
                FROM product_reviews GROUP BY seller_id
            ), offers AS (
                SELECT c.bouquet_id AS id,c.product_id,c.kiosk_id,c.name,coalesce(p.description,'') AS description,
                       c.gia AS price,p.tags,'WALK_IN' AS mode,NULL::text AS photo,
                       NULL::numeric AS product_rating,0::bigint AS product_votes,
                       NULL::numeric AS shop_rating,0::bigint AS shop_votes
                FROM screen.v_kiosk_catalog c JOIN catalog.flower_products p ON p.id=c.product_id
                JOIN kiosk_ops.kiosks k ON k.id=c.kiosk_id
                WHERE @mode='WALK_IN' AND c.kiosk_id=@kiosk AND k.status='ONLINE' AND p.inventory_kind='ARTIFICIAL'
                  AND p.status='ACTIVE' AND c.gia<=@budget
                UNION ALL
                (SELECT DISTINCT ON (v.product_id) v.product_id,v.product_id,v.kiosk_id,v.name,
                       coalesce(v.description,''),v.price,p.tags,'PREORDER',v.photo,
                       r.total::numeric/r.votes,coalesce(r.votes,0),s.rating,coalesce(s.votes,0)
                FROM screen.v_web_catalog v JOIN catalog.flower_products p ON p.id=v.product_id
                LEFT JOIN product_reviews r ON r.product_id=v.product_id
                LEFT JOIN shop_reviews s ON s.seller_id=v.seller_id
                WHERE @mode='PREORDER' AND p.inventory_kind='ARTIFICIAL' AND p.quantity>0 AND v.price<=@budget
                  AND EXISTS (SELECT 1 FROM flow.get_kiosk_preorder_availability(v.kiosk_id) a WHERE a.is_available)
                 ORDER BY v.product_id,v.kiosk_id)
            )
            SELECT id,product_id,kiosk_id,name,description,price,tags,mode,photo,
                   round(product_rating,2),product_votes,round(shop_rating,2),shop_votes
            FROM offers
            ORDER BY (SELECT count(*) FROM unnest(@terms) AS token
                      WHERE strpos(lower(name || ' ' || description || ' ' || array_to_string(tags,' ')),token)>0) DESC,
                     (coalesce(product_rating*product_votes/(product_votes+5),0)
                      + coalesce(shop_rating*shop_votes/(shop_votes+10),0)) DESC,
                     price,id
            LIMIT 30
            """);
        command.Parameters.AddWithValue("mode", input.Mode ?? "");
        command.Parameters.AddWithValue("kiosk", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)input.KioskId ?? DBNull.Value);
        command.Parameters.AddWithValue("budget", input.Budget ?? 1_000_000_000L);
        command.Parameters.AddWithValue("terms", question.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3).Distinct().Take(20).ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var candidates = new List<Candidate>();
        while (await reader.ReadAsync(ct))
            candidates.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5), reader.GetFieldValue<string[]>(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetDecimal(11), reader.GetInt64(12)));
        return candidates.ToArray();
    }

    private async Task<ModelAnswer?> AskAsync(string question, bool seller, Candidate[] candidates, CancellationToken ct)
    {
        if (config["AI_SERVICE_TOKEN"] is not { Length: >= 32 } token
            || !Uri.TryCreate(config["AI_SERVICE_URL"], UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "/flower-chat"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            question,
            audience = seller ? "seller" : "customer",
            candidates = candidates.Select(c => new { c.Id, c.Name,
                Description = c.Description[..Math.Min(600, c.Description.Length)],
                c.Price, Tags = c.Tags.Where(t => t.Length <= 64).Take(20), c.Mode,
                c.ProductRating, c.ProductReviews, c.ShopRating, c.ShopReviews })
        });
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 16384) return null;
            await response.Content.LoadIntoBufferAsync(16384, timeout.Token);
            return await response.Content.ReadFromJsonAsync<ModelAnswer>(cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}
