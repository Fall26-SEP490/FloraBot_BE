using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace FloraBot.Api.Modules.Ai;

public sealed record FlowerChatInput(string Question, string? Mode = null, long? Budget = null, Guid? KioskId = null,
    double? Latitude = null, double? Longitude = null);
public sealed record FlowerChatSuggestion(Guid Id, Guid ProductId, Guid KioskId, string Name, long Price, string Mode, string Reason,
    string? Photo, decimal? ProductRating, long ProductReviews, decimal? ShopRating, long ShopReviews,
    string? ShopName, string? KioskName, double? DistanceKm);
public sealed record FlowerChatResponse(string Intent, string Message, IReadOnlyList<FlowerChatSuggestion> Suggestions);

public sealed class FlowerChat(HttpClient client, IConfiguration config, NpgsqlDataSource data)
{
    private const string Redirect = "FloraBot có thể giúp bạn chọn hoa, tìm mẫu theo dịp hoặc hướng dẫn đặt hoa. Bạn muốn mình gợi ý theo dịp hay ngân sách ạ?";
    private const string Unavailable = "FloraBot chưa thể tư vấn ngay lúc này. Bạn thử lại hoặc xem danh mục hoa nhé.";
    private const string KioskDistanceSql = """
        SELECT k.id, CASE WHEN @latitude IS NOT NULL AND k.latitude IS NOT NULL AND k.longitude IS NOT NULL
            THEN 6371.0 * acos(least(1.0, greatest(-1.0,
                sin(radians(@latitude)) * sin(radians(k.latitude::double precision)) +
                cos(radians(@latitude)) * cos(radians(k.latitude::double precision)) *
                cos(radians(k.longitude::double precision - @longitude))
            ))) END AS distance_km
        FROM kiosk_ops.kiosks k WHERE @mode='PREORDER'
        """;
    private static readonly Regex BudgetText = new(
        @"(?:dưới|tối đa|không quá|ngân sách)\s*:?\s*(?<amount>\d{1,9}(?:[.,]\d{3}){0,2})\s*(?<unit>nghìn|ngàn|k|triệu|tr|đồng|đ|vnd)?(?![\d.,])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private sealed record Candidate(Guid Id, Guid ProductId, Guid KioskId, string Name, string Description,
        long Price, string[] Tags, string Mode, string? Photo, decimal? ProductRating, long ProductReviews,
        decimal? ShopRating, long ShopReviews, string? ShopName, string? KioskName, double? DistanceKm);
    private sealed record ModelSuggestion(Guid Id, string? Reason);
    private sealed record ModelAnswer(string? Intent, string? Message, ModelSuggestion[]? Suggestions);
    private sealed record KioskContext(string Code, string Name, string? ShopName, string? Address, string Status,
        bool PreorderAvailable, double? DistanceKm);

    public static void Map(WebApplication app) =>
        app.MapPost("/api/shop/flower-chat", (FlowerChatInput input, HttpContext http, FlowerChat chat, CancellationToken ct) =>
            chat.ReadAsync(input, http, ct))
            .AllowAnonymous().RequireRateLimiting("flower-advisor").WithTags("Ai").Produces<FlowerChatResponse>().Produces(400);

    private async Task<IResult> ReadAsync(FlowerChatInput input, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var question = input.Question?.Trim();
        if (question is null || question.Length is 0 or > 500 || input.Mode is not (null or "WALK_IN" or "PREORDER")
            || input.Budget is < 1 or > 1_000_000_000
            || input.Latitude.HasValue != input.Longitude.HasValue
            || input.Latitude is < -90 or > 90 || input.Longitude is < -180 or > 180)
            return Results.BadRequest();

        var seller = http.User.IsInRole("SELLER");
        input = ApplyQuestionConstraints(input, question);
        if (!seller && input.Mode == "PREORDER" && IsPopularShopQuestion(question))
            return Results.Ok(await PopularPreorderShopAsync(ct));
        if (!seller && input.Mode == "PREORDER" && IsNearestKioskQuestion(question))
            return Results.Ok(await NearestPreorderKioskAsync(input, ct));
        if (!seller && input.Mode == "WALK_IN" && input.KioskId is null)
            return Results.Ok(new FlowerChatResponse("FLOWERS", "Bạn chọn kiosk để FloraBot xem hoa đang có tại tủ nhé.", []));
        var candidates = seller ? [] : await CandidatesAsync(input, question, ct);
        var kiosks = seller ? [] : await RelatedKiosksAsync(input, question, ct);
        var answer = await AskAsync(question, seller, candidates, kiosks, ct);
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
                c.Photo, c.ProductRating, c.ProductReviews, c.ShopRating, c.ShopReviews,
                c.ShopName, c.KioskName, c.DistanceKm);
        }).ToArray();
        if (input.Mode == "PREORDER" && input.Latitude.HasValue)
            suggestions = suggestions.OrderBy(s => s.DistanceKm ?? double.PositiveInfinity).ToArray();
        return Results.Ok(new FlowerChatResponse("FLOWERS", answer.Message!, suggestions));
    }

    private static bool IsPopularShopQuestion(string question) =>
        (question.Contains("shop", StringComparison.OrdinalIgnoreCase)
            || question.Contains("cửa hàng", StringComparison.OrdinalIgnoreCase))
        && (question.Contains("nhiều đơn", StringComparison.OrdinalIgnoreCase)
            || question.Contains("đơn nhiều nhất", StringComparison.OrdinalIgnoreCase)
            || question.Contains("bán chạy nhất", StringComparison.OrdinalIgnoreCase));

    private async Task<FlowerChatResponse> PopularPreorderShopAsync(CancellationToken ct)
    {
        await using var command = data.CreateCommand("""
            SELECT s.shop_name, count(*) AS completed_orders
            FROM ordering.orders o
            JOIN identity.sellers s ON s.id=o.seller_id
            WHERE s.status='ACTIVE' AND o.status='COMPLETED'
              AND EXISTS (
                  SELECT 1 FROM ordering.web_requests w
                  WHERE w.order_id=o.id AND w.seller_id=o.seller_id
                    AND w.customer_id=o.customer_id AND w.state='ORDERED'
              )
            GROUP BY s.id,s.shop_name
            ORDER BY completed_orders DESC,s.id
            LIMIT 2
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new("FLOWERS", "FloraBot chưa có đơn đặt trước đã hoàn tất để xếp hạng shop.", []);

        var name = reader.GetString(0);
        var completedOrders = reader.GetInt64(1);
        var tied = await reader.ReadAsync(ct) && reader.GetInt64(1) == completedOrders;
        var message = tied
            ? $"{name} là một trong các shop đồng hạng dẫn đầu về số đơn đặt trước đã hoàn tất trên FloraBot."
            : $"{name} đang dẫn đầu về số đơn đặt trước đã hoàn tất trên FloraBot.";
        return new("FLOWERS", message, []);
    }

    private static bool IsNearestKioskQuestion(string question) =>
        (question.Contains("kiosk", StringComparison.OrdinalIgnoreCase)
            || question.Contains("tủ", StringComparison.OrdinalIgnoreCase))
        && (question.Contains("gần nhất", StringComparison.OrdinalIgnoreCase)
            || question.Contains("gần tôi", StringComparison.OrdinalIgnoreCase)
            || question.Contains("gần tui", StringComparison.OrdinalIgnoreCase)
            || question.Contains("gần mình", StringComparison.OrdinalIgnoreCase));

    private async Task<FlowerChatResponse> NearestPreorderKioskAsync(FlowerChatInput input, CancellationToken ct)
    {
        if (input.Latitude is null)
            return new("FLOWERS", "Bạn bấm Dùng vị trí của tôi, cho phép truy cập vị trí rồi gửi lại câu hỏi để FloraBot tìm kiosk nhận hoa gần bạn nhé.", []);

        await using var command = data.CreateCommand($"""
            WITH kiosk_distances AS ({KioskDistanceSql}),
            eligible_kiosks AS (
                SELECT DISTINCT v.kiosk_id, v.kiosk_name, v.shop_name, v.address
                FROM screen.v_web_catalog v
                JOIN catalog.flower_products p ON p.id=v.product_id
                WHERE p.inventory_kind='ARTIFICIAL' AND p.quantity>0 AND p.price<=@budget
            )
            SELECT k.kiosk_name,k.shop_name,k.address,d.distance_km
            FROM eligible_kiosks k JOIN kiosk_distances d ON d.id=k.kiosk_id
            WHERE d.distance_km IS NOT NULL
              AND EXISTS (SELECT 1 FROM flow.get_kiosk_preorder_availability(k.kiosk_id) a WHERE a.is_available)
            ORDER BY d.distance_km,k.kiosk_id,k.shop_name
            LIMIT 1
            """);
        AddLocationParameters(command, input);
        command.Parameters.AddWithValue("budget", input.Budget ?? 1_000_000_000L);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new("FLOWERS", "Hiện FloraBot chưa tìm thấy kiosk còn nhận đặt trước phù hợp.", []);

        var distance = reader.GetDouble(3).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("vi-VN"));
        return new("FLOWERS",
            $"Kiosk nhận hoa đặt trước gần bạn nhất là {reader.GetString(0)} của {reader.GetString(1)}, cách khoảng {distance} km theo đường chim bay. Địa chỉ: {reader.GetString(2)}.", []);
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
        await using var command = data.CreateCommand($"""
            WITH product_reviews AS (
                SELECT p.seller_id, r.product_id, sum(r.stars) AS total, count(*) AS votes
                FROM catalog.flower_product_reviews r JOIN catalog.flower_products p ON p.id=r.product_id
                WHERE @mode='PREORDER'
                GROUP BY p.seller_id,r.product_id
            ), shop_reviews AS (
                SELECT seller_id,sum(total)/sum(votes) AS rating,sum(votes)::bigint AS votes
                FROM product_reviews GROUP BY seller_id
            ), kiosk_distances AS ({KioskDistanceSql}), offers AS (
                SELECT c.bouquet_id AS id,c.product_id,c.kiosk_id,c.name,coalesce(p.description,'') AS description,
                       c.gia AS price,p.tags,'WALK_IN' AS mode,NULL::text AS photo,
                       NULL::numeric AS product_rating,0::bigint AS product_votes,
                       NULL::numeric AS shop_rating,0::bigint AS shop_votes,
                       NULL::text AS shop_name,NULL::text AS kiosk_name,NULL::double precision AS distance_km
                FROM screen.v_kiosk_catalog c JOIN catalog.flower_products p ON p.id=c.product_id
                JOIN kiosk_ops.kiosks k ON k.id=c.kiosk_id
                WHERE @mode='WALK_IN' AND c.kiosk_id=@kiosk AND k.status='ONLINE' AND p.inventory_kind='ARTIFICIAL'
                  AND p.status='ACTIVE' AND c.gia<=@budget
                UNION ALL
                (SELECT DISTINCT ON (v.product_id) v.product_id,v.product_id,v.kiosk_id,v.name,
                       coalesce(v.description,''),v.price,p.tags,'PREORDER',v.photo,
                       r.total::numeric/r.votes,coalesce(r.votes,0),s.rating,coalesce(s.votes,0),
                       v.shop_name,v.kiosk_name,d.distance_km
                FROM screen.v_web_catalog v JOIN catalog.flower_products p ON p.id=v.product_id
                JOIN kiosk_distances d ON d.id=v.kiosk_id
                LEFT JOIN product_reviews r ON r.product_id=v.product_id
                LEFT JOIN shop_reviews s ON s.seller_id=v.seller_id
                WHERE @mode='PREORDER' AND p.inventory_kind='ARTIFICIAL' AND p.quantity>0 AND v.price<=@budget
                  AND EXISTS (SELECT 1 FROM flow.get_kiosk_preorder_availability(v.kiosk_id) a WHERE a.is_available)
                 ORDER BY v.product_id,d.distance_km NULLS LAST,v.kiosk_id)
            )
            SELECT id,product_id,kiosk_id,name,description,price,tags,mode,photo,
                   round(product_rating,2),product_votes,round(shop_rating,2),shop_votes,
                   shop_name,kiosk_name,distance_km
            FROM offers
            ORDER BY (SELECT count(*) FROM unnest(@terms) AS token
                      WHERE strpos(lower(concat_ws(' ',name,description,array_to_string(tags,' '),shop_name,kiosk_name)),token)>0) DESC,
                     distance_km NULLS LAST,
                     (coalesce(product_rating*product_votes/(product_votes+5),0)
                      + coalesce(shop_rating*shop_votes/(shop_votes+10),0)) DESC,
                     price,id
            LIMIT 30
            """);
        AddLocationParameters(command, input);
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
                reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetDecimal(11), reader.GetInt64(12),
                reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetDouble(15)));
        return candidates.ToArray();
    }

    private static void AddLocationParameters(NpgsqlCommand command, FlowerChatInput input)
    {
        command.Parameters.AddWithValue("mode", input.Mode ?? "");
        command.Parameters.AddWithValue("latitude", NpgsqlTypes.NpgsqlDbType.Double, (object?)input.Latitude ?? DBNull.Value);
        command.Parameters.AddWithValue("longitude", NpgsqlTypes.NpgsqlDbType.Double, (object?)input.Longitude ?? DBNull.Value);
    }

    private async Task<KioskContext[]> RelatedKiosksAsync(FlowerChatInput input, string question, CancellationToken ct)
    {
        await using var command = data.CreateCommand($"""
            WITH kiosk_distances AS ({KioskDistanceSql}), relevant AS (
                SELECT k.id,k.code,k.name,s.shop_name,k.address,k.status,d.distance_km
                FROM kiosk_ops.kiosks k
                LEFT JOIN identity.sellers s ON s.id=k.seller_id AND s.status='ACTIVE'
                LEFT JOIN kiosk_distances d ON d.id=k.id
                WHERE k.status<>'DISABLED' AND (@mode='PREORDER' OR k.id=@kiosk)
                ORDER BY (k.id IS NOT DISTINCT FROM @kiosk) DESC,
                         (SELECT count(*) FROM unnest(@terms) AS token
                          WHERE strpos(lower(concat_ws(' ',k.code,k.name,k.address,k.region,s.shop_name)),token)>0) DESC,
                         d.distance_km NULLS LAST,k.id
                LIMIT 8
            )
            SELECT k.code,k.name,k.shop_name,k.address,k.status,
                   EXISTS(SELECT 1 FROM flow.get_kiosk_preorder_availability(k.id) a WHERE a.is_available),
                   k.distance_km
            FROM relevant k
            """);
        AddLocationParameters(command, input);
        command.Parameters.AddWithValue("kiosk", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)input.KioskId ?? DBNull.Value);
        command.Parameters.AddWithValue("terms", Regex.Split(question.ToLowerInvariant(), @"[^\p{L}\p{N}-]+")
            .Where(term => term.Length >= 3).Distinct().Take(20).ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<KioskContext>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : Math.Round(reader.GetDouble(6), 1)));
        }
        return rows.ToArray();
    }

    private async Task<ModelAnswer?> AskAsync(string question, bool seller, Candidate[] candidates,
        KioskContext[] kiosks, CancellationToken ct)
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
            kiosks = kiosks.Select(k => new
            {
                Code = LimitText(k.Code, 64), Name = LimitText(k.Name, 160),
                ShopName = LimitText(k.ShopName, 160), Address = LimitText(k.Address, 300),
                k.Status, k.PreorderAvailable, k.DistanceKm
            }),
            candidates = candidates.Select(c => new { c.Id, c.Name,
                Description = c.Description[..Math.Min(600, c.Description.Length)],
                c.Price, Tags = c.Tags.Where(t => t.Length <= 64).Take(20), c.Mode,
                c.ProductRating, c.ProductReviews, c.ShopRating, c.ShopReviews,
                ShopName = LimitText(c.ShopName, 160), KioskName = LimitText(c.KioskName, 160),
                DistanceKm = c.DistanceKm is { } km ? Math.Round(km, 1) : (double?)null })
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

    private static string? LimitText(string? value, int max) =>
        value is null ? null : value[..Math.Min(value.Length, max)];
}
