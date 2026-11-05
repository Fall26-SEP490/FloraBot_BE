using System.Security.Claims;
using Npgsql;

namespace FloraBot.Api.Modules.Ai;

public static class SeasonalSalesEndpoints
{
    private const int MaxQueryDays = 1096; // 3 years (inclusive)
    private const int MaxRowLimit = 50000;
    private const int MaxForecastProducts = 100;

    public static void MapSeasonalSales(this IEndpointRouteBuilder app)
    {
        // 1. GET /api/sellers/{sellerId}/ai/seasonal-sales
        app.MapGet("/api/sellers/{sellerId:guid}/ai/seasonal-sales", GetSeasonalSalesAsync)
            .RequireAuthorization("Merchant", "SameSeller")
            .WithTags("Ai")
            .WithSummary("Thống kê doanh số bán hàng theo mùa và ngày lễ")
            .Produces<SeasonalSalesResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // 2. GET /api/sellers/{sellerId}/ai/sales.csv
        app.MapGet("/api/sellers/{sellerId:guid}/ai/sales.csv", GetSalesCsvAsync)
            .RequireAuthorization("Merchant", "SameSeller")
            .WithTags("Ai")
            .WithSummary("Tải dữ liệu bán hàng định dạng CSV (bảo vệ công thức và loại bỏ PII)")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // 3. POST /api/sellers/{sellerId}/ai/seasonal-forecast
        app.MapPost("/api/sellers/{sellerId:guid}/ai/seasonal-forecast", PostSeasonalForecastAsync)
            .RequireAuthorization("Merchant", "SameSeller")
            .RequireRateLimiting("advisor")
            .WithTags("Ai")
            .WithSummary("Dự báo nhu cầu bán hàng theo mùa bằng mô hình Poisson và giải thích Gemini")
            .Produces<SeasonalForecastResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<(bool Ok, IResult? Error)> CheckScopeAsync(NpgsqlConnection conn, Guid actorId, Guid sellerId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT authorized, seller_exists FROM flow.seasonal_sales_check_scope(@actor, @seller)", conn);
        cmd.Parameters.AddWithValue("actor", actorId);
        cmd.Parameters.AddWithValue("seller", sellerId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (false, Results.NotFound());

        var authorized = reader.GetBoolean(0);
        var sellerExists = reader.GetBoolean(1);

        if (!sellerExists) return (false, Results.NotFound());
        if (!authorized) return (false, Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Quyền truy cập bị từ chối hoặc tài khoản đã bị khóa."));

        return (true, null);
    }

    private static async Task<IResult> GetSeasonalSalesAsync(
        Guid sellerId,
        DateOnly? from,
        DateOnly? to,
        int? year,
        int? month,
        string? season,
        string? holiday,
        HttpContext http,
        NpgsqlDataSource data,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";

        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId))
            return Results.Unauthorized();

        await using var conn = await data.OpenConnectionAsync(ct);
        var (scopeOk, scopeError) = await CheckScopeAsync(conn, actorId, sellerId, ct);
        if (!scopeOk) return scopeError!;

        var (dateOk, dateError, resolvedFrom, resolvedTo) = ResolveDateRange(from, to, year, month, season, holiday);
        if (!dateOk) return dateError!;

        var products = await LoadProductsAsync(conn, actorId, sellerId, activeOnly: false, ct);
        if (products.Count == 0)
        {
            return Results.Ok(new SeasonalSalesResponse(
                sellerId, resolvedFrom, resolvedTo,
                SeasonalCalendar.NormalizeSeason(season),
                SeasonalCalendar.NormalizeHoliday(holiday),
                0, 0, []));
        }

        var (invOk, invError, invoiceRows) = await LoadInvoicesAsync(conn, actorId, sellerId, resolvedFrom, resolvedTo, ct);
        if (!invOk) return invError!;

        var normSeason = SeasonalCalendar.NormalizeSeason(season);
        var normHoliday = SeasonalCalendar.NormalizeHoliday(holiday);

        var filteredRows = invoiceRows.Where(r =>
        {
            if (normSeason is not null && SeasonalCalendar.GetSeason(r.PaidDate.Month) != normSeason)
                return false;
            if (normHoliday is not null && !SeasonalCalendar.IsDateInHolidayWindow(r.PaidDate, normHoliday))
                return false;
            return true;
        }).ToList();

        // Authoritative distinct order count across products (prevents double-counting multi-product invoices)
        var totalInvoices = filteredRows.Select(r => r.OrderId).Distinct().Count();
        var totalUnits = filteredRows.Sum(r => r.Quantity);

        var rowsByProduct = filteredRows
            .GroupBy(r => r.ProductId)
            .ToDictionary(g => g.Key, g => new
            {
                Invoices = g.Select(x => x.OrderId).Distinct().Count(),
                Units = g.Sum(x => x.Quantity)
            });

        var productItems = new List<SeasonalSalesProduct>();
        foreach (var p in products)
        {
            rowsByProduct.TryGetValue(p.Id, out var stats);
            var invoices = stats?.Invoices ?? 0;
            var units = stats?.Units ?? 0;

            productItems.Add(new SeasonalSalesProduct(
                p.Id, p.Name, p.Status, p.Price, p.Tags, p.AvailableSince, p.Photos, invoices, units
            ));
        }

        productItems = productItems.OrderByDescending(p => p.TotalUnits).ThenBy(p => p.Name).ToList();

        return Results.Ok(new SeasonalSalesResponse(
            sellerId, resolvedFrom, resolvedTo, normSeason, normHoliday, totalInvoices, totalUnits, productItems
        ));
    }

    private static async Task<IResult> GetSalesCsvAsync(
        Guid sellerId,
        DateOnly? from,
        DateOnly? to,
        HttpContext http,
        NpgsqlDataSource data,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";

        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId))
            return Results.Unauthorized();

        await using var conn = await data.OpenConnectionAsync(ct);
        var (scopeOk, scopeError) = await CheckScopeAsync(conn, actorId, sellerId, ct);
        if (!scopeOk) return scopeError!;

        var todayVn = SeasonalCalendar.TodayVn();
        var startDate = from ?? todayVn.AddDays(-364);
        var endDate = to ?? todayVn;

        if (endDate < startDate)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Ngày kết thúc phải lớn hơn hoặc bằng ngày bắt đầu.");

        if (endDate.DayNumber - startDate.DayNumber + 1 > MaxQueryDays)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Khoảng thời gian vượt quá 3 năm (1096 ngày).");

        var rows = new List<SeasonalInvoiceRow>();
        await using (var cmd = new NpgsqlCommand("SELECT order_id, product_id, product_name, paid_date, quantity FROM flow.seasonal_sales_get_invoices(@actor, @seller, @from, @to, @limit)", conn))
        {
            cmd.Parameters.AddWithValue("actor", actorId);
            cmd.Parameters.AddWithValue("seller", sellerId);
            cmd.Parameters.AddWithValue("from", startDate);
            cmd.Parameters.AddWithValue("to", endDate);
            cmd.Parameters.AddWithValue("limit", MaxRowLimit + 1);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new SeasonalInvoiceRow(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    DateOnly.FromDateTime(reader.GetDateTime(3)),
                    reader.GetInt32(4)
                ));
            }
        }

        if (rows.Count > MaxRowLimit)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Dữ liệu xuất CSV vượt quá giới hạn 50,000 bản ghi.");

        var csv = SeasonalCsvSerializer.Serialize(rows);
        http.Response.Headers.ContentDisposition = "attachment; filename=\"sales.csv\"";
        return Results.Text(csv, "text/csv; charset=utf-8");
    }

    private static async Task<IResult> PostSeasonalForecastAsync(
        Guid sellerId,
        SeasonalForecastRequest request,
        HttpContext http,
        NpgsqlDataSource data,
        SeasonalForecastClient client,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";

        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actorId))
            return Results.Unauthorized();

        await using var conn = await data.OpenConnectionAsync(ct);
        var (scopeOk, scopeError) = await CheckScopeAsync(conn, actorId, sellerId, ct);
        if (!scopeOk) return scopeError!;

        if (request.Year is < 2000 or > 2100 || request.Month is < 1 or > 12)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Năm hoặc tháng dự báo không hợp lệ.");

        var forecastStart = new DateOnly(request.Year, request.Month, 1);
        var forecastEnd = new DateOnly(request.Year, request.Month, DateTime.DaysInMonth(request.Year, request.Month));

        var currentVn = SeasonalCalendar.TodayVn();
        var maxHistoryTo = forecastStart.AddDays(-1);
        if (maxHistoryTo > currentVn) maxHistoryTo = currentVn;

        var historyTo = request.HistoryTo ?? maxHistoryTo;
        var historyFrom = request.HistoryFrom;

        if (historyTo > maxHistoryTo)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Ngày kết thúc lịch sử (historyTo) phải nhỏ hơn ngày bắt đầu dự báo và không vượt quá ngày hiện tại.");

        if (historyTo < historyFrom)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Ngày kết thúc lịch sử phải lớn hơn hoặc bằng ngày bắt đầu.");

        if (historyTo.DayNumber - historyFrom.DayNumber + 1 > MaxQueryDays)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Thời gian lịch sử không được vượt quá 1096 ngày.");

        if (forecastStart.DayNumber - historyTo.DayNumber > 366)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Khoảng cách giữa lịch sử và dự báo không được vượt quá 366 ngày.");

        var allProducts = await LoadProductsAsync(conn, actorId, sellerId, activeOnly: false, ct);
        var activeProducts = allProducts.Where(p => p.Status == "ACTIVE").ToList();

        var targetSeason = SeasonalCalendar.GetSeason(request.Month);
        var targetHolidays = SeasonalCalendar.GetHolidaysInMonth(request.Year, request.Month);

        if (activeProducts.Count == 0)
        {
            return Results.Ok(new SeasonalForecastResponse(
                sellerId, "seasonal-v1", "PRODUCT", "DATABASE",
                historyFrom, historyTo, forecastStart, forecastEnd,
                targetSeason, targetHolidays,
                ["Không có sản phẩm nào đang hoạt động để dự báo."],
                []));
        }

        if (activeProducts.Count > MaxForecastProducts)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: $"Số lượng sản phẩm dự báo ({activeProducts.Count}) vượt quá giới hạn tối đa {MaxForecastProducts} sản phẩm.");

        var (obsOk, obsError, observations) = await LoadObservationsAsync(conn, actorId, sellerId, historyFrom, historyTo, ct);
        if (!obsOk) return obsError!;

        var activeDict = activeProducts.ToDictionary(p => p.Id);
        var activeIds = activeProducts.Select(p => p.Id).ToHashSet();

        // Server-authoritative computation of actual observations
        var obsByProduct = observations
            .GroupBy(o => o.ProductId)
            .ToDictionary(g => g.Key, g => new
            {
                Invoices = g.Sum(x => x.InvoiceCount),
                Units = g.Sum(x => x.Units),
                SameSeason = g.Where(x => SeasonalCalendar.GetSeason(x.Day.Month) == targetSeason).Sum(x => x.InvoiceCount)
            });

        var upstreamProducts = activeProducts.Select(p =>
            new UpstreamProduct(p.Id, p.Name, p.AvailableSince.ToString("yyyy-MM-dd"))).ToList();

        var upstreamObservations = observations
            .Where(o => activeIds.Contains(o.ProductId))
            .Select(o => new UpstreamObservation(o.Day.ToString("yyyy-MM-dd"), o.ProductId, o.InvoiceCount, o.Units))
            .ToList();

        var upstreamReq = new UpstreamForecastRequest(
            ContractVersion: "seasonal-v1",
            HistoryStart: historyFrom.ToString("yyyy-MM-dd"),
            HistoryEnd: historyTo.ToString("yyyy-MM-dd"),
            TargetYear: request.Year,
            TargetMonth: request.Month,
            DataSource: "DATABASE",
            Products: upstreamProducts,
            Observations: upstreamObservations,
            IncludeExplanations: true
        );

        var upstreamResp = await client.ForecastAsync(
            upstreamReq,
            activeIds,
            forecastStart.ToString("yyyy-MM-dd"),
            forecastEnd.ToString("yyyy-MM-dd"),
            targetSeason,
            ct);

        if (upstreamResp is null)
        {
            // Graceful fallback with BE-computed actual stats and null forecasts
            var fallbackItems = activeProducts.Select(p =>
            {
                obsByProduct.TryGetValue(p.Id, out var stats);
                var invoices = stats?.Invoices ?? 0;
                var units = stats?.Units ?? 0;
                var sameSeason = stats?.SameSeason ?? 0;

                return new ForecastProductItem(
                    p.Id, p.Name, p.Status, p.Price, p.Tags, p.AvailableSince, p.Photos,
                    invoices, units, sameSeason,
                    ForecastInvoiceCount: null,
                    ForecastUnits: null,
                    ModelStatus: "INSUFFICIENT_HISTORY",
                    AnnualSeasonalityValidated: false,
                    Validation: null,
                    Explanation: "Dịch vụ dự báo tạm thời không khả dụng; hệ thống hiển thị số liệu thực tế đo lường được.",
                    ExplanationSource: "FALLBACK"
                );
            }).OrderByDescending(f => f.ObservedUnits).ThenBy(f => f.Name).ToList();

            return Results.Ok(new SeasonalForecastResponse(
                sellerId, "seasonal-v1", "PRODUCT", "DATABASE",
                historyFrom, historyTo, forecastStart, forecastEnd,
                targetSeason, targetHolidays,
                ["Dịch vụ dự báo AI tạm thời không khả dụng; hệ thống chuyển sang dữ liệu thực tế dự phòng."],
                fallbackItems
            ));
        }

        // Map upstream predictions, but retain BE-authoritative metadata, photos, and observed counts
        var forecastItems = new List<ForecastProductItem>();
        var upstreamDict = upstreamResp.Products.ToDictionary(p => p.ProductId);

        foreach (var p in activeProducts)
        {
            obsByProduct.TryGetValue(p.Id, out var stats);
            var invoices = stats?.Invoices ?? 0;
            var units = stats?.Units ?? 0;
            var sameSeason = stats?.SameSeason ?? 0;

            if (upstreamDict.TryGetValue(p.Id, out var up))
            {
                ProductForecastValidation? val = null;
                if (up.Validation is not null)
                {
                    val = new ProductForecastValidation(
                        up.Validation.TrainingDays,
                        up.Validation.HoldoutDays,
                        up.Validation.Mae,
                        up.Validation.BaselineMae,
                        up.Validation.Wape,
                        up.Validation.BaselineWape
                    );
                }

                forecastItems.Add(new ForecastProductItem(
                    p.Id, p.Name, p.Status, p.Price, p.Tags, p.AvailableSince, p.Photos,
                    invoices,
                    units,
                    sameSeason,
                    up.ForecastInvoiceCount,
                    up.ForecastUnits,
                    up.ModelStatus,
                    up.AnnualSeasonalityValidated,
                    val,
                    string.IsNullOrWhiteSpace(up.Explanation) ? "Dự báo dựa trên dữ liệu bán hàng quá khứ." : up.Explanation,
                    string.IsNullOrWhiteSpace(up.ExplanationSource) ? "FALLBACK" : up.ExplanationSource
                ));
            }
            else
            {
                forecastItems.Add(new ForecastProductItem(
                    p.Id, p.Name, p.Status, p.Price, p.Tags, p.AvailableSince, p.Photos,
                    invoices, units, sameSeason,
                    ForecastInvoiceCount: null,
                    ForecastUnits: null,
                    ModelStatus: "INSUFFICIENT_HISTORY",
                    AnnualSeasonalityValidated: false,
                    Validation: null,
                    Explanation: "Sản phẩm chưa có đủ lịch sử để dự báo.",
                    ExplanationSource: "FALLBACK"
                ));
            }
        }

        forecastItems = forecastItems
            .OrderByDescending(f => f.ForecastUnits ?? f.ObservedUnits)
            .ThenBy(f => f.Name)
            .ToList();

        var warnings = upstreamResp.Warnings ?? [];

        return Results.Ok(new SeasonalForecastResponse(
            sellerId,
            upstreamResp.ContractVersion,
            upstreamResp.AggregationLevel,
            upstreamResp.DataSource,
            historyFrom,
            historyTo,
            forecastStart,
            forecastEnd,
            targetSeason,
            targetHolidays,
            warnings,
            forecastItems
        ));
    }

    private sealed record ProductMeta(
        Guid Id,
        string Name,
        string Status,
        long Price,
        string[] Tags,
        DateOnly AvailableSince,
        List<string> Photos
    );

    private sealed record ObservationRow(
        DateOnly Day,
        Guid ProductId,
        int InvoiceCount,
        int Units
    );

    private static (bool Ok, IResult? Error, DateOnly From, DateOnly To) ResolveDateRange(
        DateOnly? from, DateOnly? to, int? year, int? month, string? season, string? holiday)
    {
        // 1. Validate year
        if (year.HasValue && (year.Value < 2000 || year.Value > 2100))
            return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Năm không hợp lệ (phải từ 2000 đến 2100)."), default, default);

        // 2. Validate month
        if (month.HasValue && (month.Value < 1 || month.Value > 12))
            return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Tháng không hợp lệ (phải từ 1 đến 12)."), default, default);

        // 3. Validate season enum
        string? normSeason = null;
        if (!string.IsNullOrWhiteSpace(season))
        {
            normSeason = SeasonalCalendar.NormalizeSeason(season);
            if (normSeason is null)
                return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Mã mùa không hợp lệ (chỉ chấp nhận: SPRING, SUMMER, AUTUMN, WINTER)."), default, default);
        }

        // 4. Validate holiday enum
        string? normHoliday = null;
        if (!string.IsNullOrWhiteSpace(holiday))
        {
            normHoliday = SeasonalCalendar.NormalizeHoliday(holiday);
            if (normHoliday is null)
                return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Mã ngày lễ không hợp lệ (chỉ chấp nhận: NEW_YEAR, VALENTINE, WOMENS_DAY, VN_WOMENS_DAY, TEACHERS_DAY, CHRISTMAS)."), default, default);
        }

        // 5. Check conflicting month vs season
        if (month.HasValue && normSeason is not null)
        {
            if (SeasonalCalendar.GetSeason(month.Value) != normSeason)
                return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: $"Tháng {month.Value} không thuộc mùa {normSeason}."), default, default);
        }

        // 6. Check conflicting month vs holiday
        if (month.HasValue && normHoliday is not null)
        {
            bool holidayInMonth = normHoliday switch
            {
                "NEW_YEAR" => month.Value is 1 or 12,
                "VALENTINE" => month.Value == 2,
                "WOMENS_DAY" => month.Value == 3,
                "VN_WOMENS_DAY" => month.Value == 10,
                "TEACHERS_DAY" => month.Value == 11,
                "CHRISTMAS" => month.Value == 12,
                _ => false
            };
            if (!holidayInMonth)
                return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: $"Ngày lễ {normHoliday} không thuộc tháng {month.Value}."), default, default);
        }

        // 7. Validate from and to years if provided
        if (from.HasValue && (from.Value.Year < 2000 || from.Value.Year > 2100))
            return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Ngày bắt đầu (from) không hợp lệ (năm phải từ 2000 đến 2100)."), default, default);

        if (to.HasValue && (to.Value.Year < 2000 || to.Value.Year > 2100))
            return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Ngày kết thúc (to) không hợp lệ (năm phải từ 2000 đến 2100)."), default, default);

        if (from.HasValue && to.HasValue && to.Value < from.Value)
            return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Ngày kết thúc phải lớn hơn hoặc bằng ngày bắt đầu."), default, default);

        DateOnly resolvedFrom;
        DateOnly resolvedTo;

        if (from.HasValue && to.HasValue)
        {
            resolvedFrom = from.Value;
            resolvedTo = to.Value;

            if (year.HasValue && (resolvedFrom.Year > year.Value || resolvedTo.Year < year.Value))
                return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Khoảng ngày from/to không giao với năm được chọn."), default, default);
        }
        else if (from.HasValue || to.HasValue)
        {
            if (year.HasValue && month.HasValue)
            {
                var mStart = new DateOnly(year.Value, month.Value, 1);
                var mEnd = new DateOnly(year.Value, month.Value, DateTime.DaysInMonth(year.Value, month.Value));
                resolvedFrom = from ?? mStart;
                resolvedTo = to ?? mEnd;
            }
            else if (year.HasValue)
            {
                resolvedFrom = from ?? new DateOnly(year.Value, 1, 1);
                resolvedTo = to ?? new DateOnly(year.Value, 12, 31);
            }
            else
            {
                var todayVn = SeasonalCalendar.TodayVn();
                resolvedFrom = from ?? todayVn.AddDays(-89);
                resolvedTo = to ?? todayVn;
            }
        }
        else
        {
            if (year.HasValue && month.HasValue)
            {
                resolvedFrom = new DateOnly(year.Value, month.Value, 1);
                resolvedTo = new DateOnly(year.Value, month.Value, DateTime.DaysInMonth(year.Value, month.Value));
            }
            else if (normHoliday is not null)
            {
                var hYear = year ?? SeasonalCalendar.TodayVn().Year;
                var window = SeasonalCalendar.GetHolidayWindow(hYear, normHoliday);
                resolvedFrom = window!.Value.From;
                resolvedTo = window!.Value.To;
            }
            else if (normSeason is not null)
            {
                var sYear = year ?? SeasonalCalendar.TodayVn().Year;
                (resolvedFrom, resolvedTo) = normSeason switch
                {
                    SeasonalCalendar.Spring => (new DateOnly(sYear, 3, 1), new DateOnly(sYear, 5, 31)),
                    SeasonalCalendar.Summer => (new DateOnly(sYear, 6, 1), new DateOnly(sYear, 8, 31)),
                    SeasonalCalendar.Autumn => (new DateOnly(sYear, 9, 1), new DateOnly(sYear, 11, 30)),
                    SeasonalCalendar.Winter => (new DateOnly(sYear - 1, 12, 1), new DateOnly(sYear, 2, DateTime.IsLeapYear(sYear) ? 29 : 28)),
                    _ => (new DateOnly(sYear, 1, 1), new DateOnly(sYear, 12, 31))
                };
            }
            else if (year.HasValue)
            {
                resolvedFrom = new DateOnly(year.Value, 1, 1);
                resolvedTo = new DateOnly(year.Value, 12, 31);
            }
            else
            {
                var todayVn = SeasonalCalendar.TodayVn();
                resolvedFrom = todayVn.AddDays(-89);
                resolvedTo = todayVn;
            }
        }

        if (resolvedTo < resolvedFrom)
            return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Ngày kết thúc phải lớn hơn hoặc bằng ngày bắt đầu."), default, default);

        if (resolvedTo.DayNumber - resolvedFrom.DayNumber + 1 > MaxQueryDays)
            return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Khoảng thời gian truy vấn không được vượt quá 3 năm (1096 ngày)."), default, default);

        return (true, null, resolvedFrom, resolvedTo);
    }

    private static async Task<List<ProductMeta>> LoadProductsAsync(NpgsqlConnection conn, Guid actorId, Guid sellerId, bool activeOnly, CancellationToken ct)
    {
        var products = new List<ProductMeta>();
        await using var cmd = new NpgsqlCommand("SELECT id, name, status, price, tags, available_since, photos FROM flow.seasonal_sales_get_products(@actor, @seller, @activeOnly)", conn);
        cmd.Parameters.AddWithValue("actor", actorId);
        cmd.Parameters.AddWithValue("seller", sellerId);
        cmd.Parameters.AddWithValue("activeOnly", activeOnly);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var name = reader.GetString(1);
            var status = reader.GetString(2);
            var price = reader.GetInt64(3);
            var tags = reader.GetFieldValue<string[]>(4);
            var availSince = DateOnly.FromDateTime(reader.GetDateTime(5));
            var photos = reader.GetFieldValue<string[]>(6).ToList();

            products.Add(new ProductMeta(id, name, status, price, tags, availSince, photos));
        }

        return products;
    }

    private static async Task<(bool Ok, IResult? Error, List<SeasonalInvoiceRow> Invoices)> LoadInvoicesAsync(
        NpgsqlConnection conn, Guid actorId, Guid sellerId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var list = new List<SeasonalInvoiceRow>();
        await using var cmd = new NpgsqlCommand("SELECT order_id, product_id, product_name, paid_date, quantity FROM flow.seasonal_sales_get_invoices(@actor, @seller, @from, @to, @limit)", conn);
        cmd.Parameters.AddWithValue("actor", actorId);
        cmd.Parameters.AddWithValue("seller", sellerId);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("limit", MaxRowLimit + 1);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (list.Count >= MaxRowLimit)
                return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Dữ liệu thống kê bán hàng vượt quá giới hạn 50,000 bản ghi."), list);

            list.Add(new SeasonalInvoiceRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                DateOnly.FromDateTime(reader.GetDateTime(3)),
                reader.GetInt32(4)
            ));
        }

        return (true, null, list);
    }

    private static async Task<(bool Ok, IResult? Error, List<ObservationRow> Observations)> LoadObservationsAsync(
        NpgsqlConnection conn, Guid actorId, Guid sellerId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var list = new List<ObservationRow>();
        await using var cmd = new NpgsqlCommand("SELECT day, product_id, invoice_count, units FROM flow.seasonal_sales_get_observations(@actor, @seller, @from, @to)", conn);
        cmd.Parameters.AddWithValue("actor", actorId);
        cmd.Parameters.AddWithValue("seller", sellerId);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (list.Count >= MaxRowLimit)
                return (false, Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Dữ liệu quan sát vượt quá giới hạn 50,000 bản ghi."), list);

            list.Add(new ObservationRow(
                DateOnly.FromDateTime(reader.GetDateTime(0)),
                reader.GetGuid(1),
                reader.GetInt32(2),
                reader.GetInt32(3)
            ));
        }

        return (true, null, list);
    }
}
