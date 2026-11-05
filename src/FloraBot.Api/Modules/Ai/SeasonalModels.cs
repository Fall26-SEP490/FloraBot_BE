using System.Text;
using System.Text.Json.Serialization;

namespace FloraBot.Api.Modules.Ai;

public sealed record HolidayInfo(string Code, string Name, DateOnly Day);

public static class SeasonalCalendar
{
    public const string Spring = "SPRING";
    public const string Summer = "SUMMER";
    public const string Autumn = "AUTUMN";
    public const string Winter = "WINTER";

    public static TimeZoneInfo VnTimeZone { get; } = GetVnTimeZone();

    private static TimeZoneInfo GetVnTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
    }

    public static DateOnly TodayVn()
    {
        var nowVn = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, VnTimeZone);
        return DateOnly.FromDateTime(nowVn);
    }

    public static string GetSeason(int month) => month switch
    {
        3 or 4 or 5 => Spring,
        6 or 7 or 8 => Summer,
        9 or 10 or 11 => Autumn,
        12 or 1 or 2 => Winter,
        _ => throw new ArgumentOutOfRangeException(nameof(month))
    };

    public static string? NormalizeSeason(string? season)
    {
        if (string.IsNullOrWhiteSpace(season)) return null;
        var s = season.Trim().ToUpperInvariant();
        return s is Spring or Summer or Autumn or Winter ? s : null;
    }

    public static string? NormalizeHoliday(string? holiday)
    {
        if (string.IsNullOrWhiteSpace(holiday)) return null;
        var h = holiday.Trim().ToUpperInvariant();
        return h switch
        {
            "NEW_YEAR" or "TET_DUONG_LICH" => "NEW_YEAR",
            "VALENTINE" => "VALENTINE",
            "WOMENS_DAY" or "WOMEN_DAY" => "WOMENS_DAY",
            "VN_WOMENS_DAY" or "VIETNAM_WOMENS_DAY" => "VN_WOMENS_DAY",
            "TEACHERS_DAY" or "TEACHER_DAY" => "TEACHERS_DAY",
            "CHRISTMAS" or "XMAS" => "CHRISTMAS",
            _ => null
        };
    }

    public static bool IsDateInHolidayWindow(DateOnly date, string holidayCode)
    {
        var norm = NormalizeHoliday(holidayCode);
        if (norm is null) return false;

        return norm switch
        {
            "NEW_YEAR" => (date.Month == 12 && date.Day >= 25) || (date.Month == 1 && date.Day == 1),
            "VALENTINE" => date.Month == 2 && date.Day >= 7 && date.Day <= 14,
            "WOMENS_DAY" => date.Month == 3 && date.Day >= 1 && date.Day <= 8,
            "VN_WOMENS_DAY" => date.Month == 10 && date.Day >= 13 && date.Day <= 20,
            "TEACHERS_DAY" => date.Month == 11 && date.Day >= 13 && date.Day <= 20,
            "CHRISTMAS" => date.Month == 12 && date.Day >= 18 && date.Day <= 25,
            _ => false
        };
    }

    public static List<HolidayInfo> GetHolidaysForDate(DateOnly date)
    {
        var list = new List<HolidayInfo>();

        if (IsDateInHolidayWindow(date, "CHRISTMAS"))
            list.Add(new HolidayInfo("CHRISTMAS", "Giáng sinh", new DateOnly(date.Year, 12, 25)));

        if (IsDateInHolidayWindow(date, "NEW_YEAR"))
        {
            var targetYear = date.Month == 12 ? date.Year + 1 : date.Year;
            list.Add(new HolidayInfo("NEW_YEAR", "Tết Dương lịch", new DateOnly(targetYear, 1, 1)));
        }

        if (IsDateInHolidayWindow(date, "VALENTINE"))
            list.Add(new HolidayInfo("VALENTINE", "Lễ tình nhân", new DateOnly(date.Year, 2, 14)));

        if (IsDateInHolidayWindow(date, "WOMENS_DAY"))
            list.Add(new HolidayInfo("WOMENS_DAY", "Quốc tế Phụ nữ", new DateOnly(date.Year, 3, 8)));

        if (IsDateInHolidayWindow(date, "VN_WOMENS_DAY"))
            list.Add(new HolidayInfo("VN_WOMENS_DAY", "Phụ nữ Việt Nam", new DateOnly(date.Year, 10, 20)));

        if (IsDateInHolidayWindow(date, "TEACHERS_DAY"))
            list.Add(new HolidayInfo("TEACHERS_DAY", "Nhà giáo Việt Nam", new DateOnly(date.Year, 11, 20)));

        return list;
    }

    public static List<string> GetHolidayCodesForDate(DateOnly date) =>
        GetHolidaysForDate(date).Select(h => h.Code).ToList();

    public static List<HolidayInfo> GetHolidaysInMonth(int year, int month)
    {
        var holidays = new List<HolidayInfo>();
        switch (month)
        {
            case 1:
                holidays.Add(new HolidayInfo("NEW_YEAR", "Tết Dương lịch", new DateOnly(year, 1, 1)));
                break;
            case 2:
                holidays.Add(new HolidayInfo("VALENTINE", "Lễ tình nhân", new DateOnly(year, 2, 14)));
                break;
            case 3:
                holidays.Add(new HolidayInfo("WOMENS_DAY", "Quốc tế Phụ nữ", new DateOnly(year, 3, 8)));
                break;
            case 10:
                holidays.Add(new HolidayInfo("VN_WOMENS_DAY", "Phụ nữ Việt Nam", new DateOnly(year, 10, 20)));
                break;
            case 11:
                holidays.Add(new HolidayInfo("TEACHERS_DAY", "Nhà giáo Việt Nam", new DateOnly(year, 11, 20)));
                break;
            case 12:
                holidays.Add(new HolidayInfo("CHRISTMAS", "Giáng sinh", new DateOnly(year, 12, 25)));
                break;
        }
        return holidays;
    }

    public static (DateOnly From, DateOnly To)? GetHolidayWindow(int year, string holidayCode)
    {
        return holidayCode switch
        {
            "NEW_YEAR" => (new DateOnly(year - 1, 12, 25), new DateOnly(year, 1, 1)),
            "VALENTINE" => (new DateOnly(year, 2, 7), new DateOnly(year, 2, 14)),
            "WOMENS_DAY" => (new DateOnly(year, 3, 1), new DateOnly(year, 3, 8)),
            "VN_WOMENS_DAY" => (new DateOnly(year, 10, 13), new DateOnly(year, 10, 20)),
            "TEACHERS_DAY" => (new DateOnly(year, 11, 13), new DateOnly(year, 11, 20)),
            "CHRISTMAS" => (new DateOnly(year, 12, 18), new DateOnly(year, 12, 25)),
            _ => null
        };
    }
}

public sealed record SeasonalSalesProduct(
    Guid ProductId,
    string Name,
    string Status,
    long Price,
    string[] Tags,
    DateOnly AvailableSince,
    List<string> Photos,
    int TotalInvoices,
    int TotalUnits
);

public sealed record SeasonalSalesResponse(
    Guid SellerId,
    DateOnly From,
    DateOnly To,
    string? Season,
    string? Holiday,
    int TotalInvoices,
    int TotalUnits,
    List<SeasonalSalesProduct> Products,
    string AggregationLevel = "PRODUCT"
);

public sealed record SeasonalForecastRequest(
    int Year,
    int Month,
    DateOnly HistoryFrom,
    DateOnly? HistoryTo = null
);

public sealed record ProductForecastValidation(
    int TrainingDays,
    int HoldoutDays,
    double? Mae,
    double? BaselineMae,
    double? Wape,
    double? BaselineWape
);

public sealed record ForecastProductItem(
    Guid ProductId,
    string Name,
    string Status,
    long Price,
    string[] Tags,
    DateOnly AvailableSince,
    List<string> Photos,
    int ObservedInvoiceCount,
    int ObservedUnits,
    int SameSeasonInvoiceCount,
    double? ForecastInvoiceCount,
    double? ForecastUnits,
    string ModelStatus,
    bool AnnualSeasonalityValidated,
    ProductForecastValidation? Validation,
    string Explanation,
    string ExplanationSource
);

public sealed record SeasonalForecastResponse(
    Guid SellerId,
    string ContractVersion,
    string AggregationLevel,
    string DataSource,
    DateOnly HistoryStart,
    DateOnly HistoryEnd,
    DateOnly ForecastStart,
    DateOnly ForecastEnd,
    string Season,
    List<HolidayInfo> Holidays,
    List<string> Warnings,
    List<ForecastProductItem> Products
);

public sealed record SeasonalInvoiceRow(
    Guid OrderId,
    Guid ProductId,
    string ProductName,
    DateOnly PaidDate,
    int Quantity
);

public static class SeasonalCsvSerializer
{
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    public static string EscapeValue(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "\"\"";
        var val = text;
        if (FormulaTriggers.Contains(val[0]))
        {
            val = "'" + val;
        }

        if (val.Contains('"') || val.Contains(',') || val.Contains('\n') || val.Contains('\r'))
        {
            return "\"" + val.Replace("\"", "\"\"") + "\"";
        }

        return "\"" + val + "\"";
    }

    public static string Serialize(IEnumerable<SeasonalInvoiceRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("invoice_id,product_id,product_name,paid_date,quantity,season,holiday");

        foreach (var r in rows)
        {
            var season = SeasonalCalendar.GetSeason(r.PaidDate.Month);
            var holidayCodes = SeasonalCalendar.GetHolidayCodesForDate(r.PaidDate);
            var holiday = string.Join(";", holidayCodes);

            sb.Append(r.OrderId).Append(',')
              .Append(r.ProductId).Append(',')
              .Append(EscapeValue(r.ProductName)).Append(',')
              .Append(r.PaidDate.ToString("yyyy-MM-dd")).Append(',')
              .Append(r.Quantity).Append(',')
              .Append(season).Append(',')
              .Append(holiday).AppendLine();
        }

        return sb.ToString();
    }
}

// Frozen upstream wire contract seasonal-v1 records
public sealed record UpstreamProduct(
    [property: JsonPropertyName("product_id")] Guid ProductId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("available_since")] string AvailableSince
);

public sealed record UpstreamObservation(
    [property: JsonPropertyName("day")] string Day,
    [property: JsonPropertyName("product_id")] Guid ProductId,
    [property: JsonPropertyName("invoice_count")] int InvoiceCount,
    [property: JsonPropertyName("units")] int Units
);

public sealed record UpstreamForecastRequest(
    [property: JsonPropertyName("contract_version")] string ContractVersion,
    [property: JsonPropertyName("history_start")] string HistoryStart,
    [property: JsonPropertyName("history_end")] string HistoryEnd,
    [property: JsonPropertyName("target_year")] int TargetYear,
    [property: JsonPropertyName("target_month")] int TargetMonth,
    [property: JsonPropertyName("data_source")] string DataSource,
    [property: JsonPropertyName("products")] List<UpstreamProduct> Products,
    [property: JsonPropertyName("observations")] List<UpstreamObservation> Observations,
    [property: JsonPropertyName("include_explanations")] bool IncludeExplanations = true
);

public sealed record UpstreamHoliday(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("day")] string Day
);

public sealed record UpstreamValidation(
    [property: JsonPropertyName("training_days")] int TrainingDays,
    [property: JsonPropertyName("holdout_days")] int HoldoutDays,
    [property: JsonPropertyName("mae")] double? Mae,
    [property: JsonPropertyName("baseline_mae")] double? BaselineMae,
    [property: JsonPropertyName("wape")] double? Wape,
    [property: JsonPropertyName("baseline_wape")] double? BaselineWape
);

public sealed record UpstreamProductForecast(
    [property: JsonPropertyName("product_id")] Guid ProductId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("observed_invoice_count")] int ObservedInvoiceCount,
    [property: JsonPropertyName("observed_units")] int ObservedUnits,
    [property: JsonPropertyName("same_season_invoice_count")] int SameSeasonInvoiceCount,
    [property: JsonPropertyName("forecast_invoice_count")] double? ForecastInvoiceCount,
    [property: JsonPropertyName("forecast_units")] double? ForecastUnits,
    [property: JsonPropertyName("model_status")] string ModelStatus,
    [property: JsonPropertyName("annual_seasonality_validated")] bool AnnualSeasonalityValidated,
    [property: JsonPropertyName("validation")] UpstreamValidation? Validation,
    [property: JsonPropertyName("explanation")] string? Explanation,
    [property: JsonPropertyName("explanation_source")] string? ExplanationSource
);

public sealed record UpstreamForecastResponse(
    [property: JsonPropertyName("contract_version")] string ContractVersion,
    [property: JsonPropertyName("aggregation_level")] string AggregationLevel,
    [property: JsonPropertyName("data_source")] string DataSource,
    [property: JsonPropertyName("history_start")] string HistoryStart,
    [property: JsonPropertyName("history_end")] string HistoryEnd,
    [property: JsonPropertyName("forecast_start")] string ForecastStart,
    [property: JsonPropertyName("forecast_end")] string ForecastEnd,
    [property: JsonPropertyName("season")] string Season,
    [property: JsonPropertyName("holidays")] List<UpstreamHoliday>? Holidays,
    [property: JsonPropertyName("warnings")] List<string>? Warnings,
    [property: JsonPropertyName("products")] List<UpstreamProductForecast> Products
);
