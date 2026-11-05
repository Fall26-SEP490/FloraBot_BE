using System.Net;
using System.Text.Json;
using FloraBot.Api.Modules.Ai;
using Microsoft.Extensions.Configuration;

namespace FloraBot.Api.Tests;

public sealed class SeasonalSalesTests
{
    [Fact]
    public void CalendarResolvesSeasonsCorrectly()
    {
        Assert.Equal("WINTER", SeasonalCalendar.GetSeason(1));
        Assert.Equal("WINTER", SeasonalCalendar.GetSeason(2));
        Assert.Equal("SPRING", SeasonalCalendar.GetSeason(3));
        Assert.Equal("SPRING", SeasonalCalendar.GetSeason(4));
        Assert.Equal("SPRING", SeasonalCalendar.GetSeason(5));
        Assert.Equal("SUMMER", SeasonalCalendar.GetSeason(6));
        Assert.Equal("SUMMER", SeasonalCalendar.GetSeason(7));
        Assert.Equal("SUMMER", SeasonalCalendar.GetSeason(8));
        Assert.Equal("AUTUMN", SeasonalCalendar.GetSeason(9));
        Assert.Equal("AUTUMN", SeasonalCalendar.GetSeason(10));
        Assert.Equal("AUTUMN", SeasonalCalendar.GetSeason(11));
        Assert.Equal("WINTER", SeasonalCalendar.GetSeason(12));
    }

    [Fact]
    public void CalendarResolvesGregorianHolidayWindowsCorrectly()
    {
        // Valentine 2026: Feb 7 - Feb 14
        Assert.False(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 2, 6), "VALENTINE"));
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 2, 7), "VALENTINE"));
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 2, 14), "VALENTINE"));
        Assert.False(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 2, 15), "VALENTINE"));

        // International Women's Day: Mar 1 - Mar 8
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 3, 1), "WOMENS_DAY"));
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 3, 8), "WOMENS_DAY"));
        Assert.False(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 3, 9), "WOMENS_DAY"));

        // Vietnam Women's Day: Oct 13 - Oct 20
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 10, 13), "VN_WOMENS_DAY"));
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 10, 20), "VN_WOMENS_DAY"));

        // Teacher's Day: Nov 13 - Nov 20
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 11, 13), "TEACHERS_DAY"));
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 11, 20), "TEACHERS_DAY"));

        // Christmas: Dec 18 - Dec 25
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 12, 18), "CHRISTMAS"));
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 12, 25), "CHRISTMAS"));

        // Dec 25 overlap: Both Christmas and New Year windows include Dec 25 without dropping either!
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 12, 25), "NEW_YEAR"));
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2026, 12, 25), "CHRISTMAS"));

        var dec25Holidays = SeasonalCalendar.GetHolidayCodesForDate(new DateOnly(2026, 12, 25));
        Assert.Contains("CHRISTMAS", dec25Holidays);
        Assert.Contains("NEW_YEAR", dec25Holidays);

        // New Year Jan 1
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(new DateOnly(2027, 1, 1), "NEW_YEAR"));
    }

    [Fact]
    public void CsvSerializerEscapesFormulasAndSpecialCharacters()
    {
        // Spreadsheet formula injection attacks
        Assert.Equal("\"'=sum(A1:A10)\"", SeasonalCsvSerializer.EscapeValue("=sum(A1:A10)"));
        Assert.Equal("\"'+cmd|' /C calc'!A0\"", SeasonalCsvSerializer.EscapeValue("+cmd|' /C calc'!A0"));
        Assert.Equal("\"'-10+20\"", SeasonalCsvSerializer.EscapeValue("-10+20"));
        Assert.Equal("\"'@SUM(1,2)\"", SeasonalCsvSerializer.EscapeValue("@SUM(1,2)"));
        Assert.Equal("\"'\tmalicious\"", SeasonalCsvSerializer.EscapeValue("\tmalicious"));
        Assert.Equal("\"'\rmalicious\"", SeasonalCsvSerializer.EscapeValue("\rmalicious"));

        // Quotes, commas, and newlines
        Assert.Equal("\"Hoa hồng \"\"Đỏ Thắm\"\", loại 1\"", SeasonalCsvSerializer.EscapeValue("Hoa hồng \"Đỏ Thắm\", loại 1"));
    }

    [Fact]
    public void CsvSerializerExportsZeroPiiAndIncludesCalendarContext()
    {
        var orderId = Guid.NewGuid();
        var prodId = Guid.NewGuid();
        var rows = new List<SeasonalInvoiceRow>
        {
            new(orderId, prodId, "=Hoa hồng Valentine", new DateOnly(2026, 2, 14), 3)
        };

        var csv = SeasonalCsvSerializer.Serialize(rows);
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal("invoice_id,product_id,product_name,paid_date,quantity,season,holiday", lines[0]);

        var dataLine = lines[1];
        Assert.Contains(orderId.ToString(), dataLine);
        Assert.Contains(prodId.ToString(), dataLine);
        Assert.Contains("\"'=Hoa hồng Valentine\"", dataLine);
        Assert.Contains("2026-02-14", dataLine);
        Assert.Contains(",3,", dataLine);
        Assert.Contains(",WINTER,", dataLine);
        Assert.Contains(",VALENTINE", dataLine);

        // Verification of Zero PII: no customer, phone, email, address, bank
        Assert.DoesNotContain("customer", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bank", csv, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpstreamResponseValidatorAcceptsValidSeasonalV1Contract()
    {
        var prodId1 = Guid.NewGuid();
        var prodId2 = Guid.NewGuid();
        var expected = new HashSet<Guid> { prodId1, prodId2 };

        var req = new UpstreamForecastRequest(
            ContractVersion: "seasonal-v1",
            HistoryStart: "2024-11-01",
            HistoryEnd: "2026-10-31", // 2 full annual cycles (730 days)
            TargetYear: 2026,
            TargetMonth: 11,
            DataSource: "DATABASE",
            Products: [new UpstreamProduct(prodId1, "Bó Hoa Tri Ân", "2024-01-01"), new UpstreamProduct(prodId2, "Bó Hướng Dương", "2024-01-01")],
            Observations:
            [
                new UpstreamObservation("2026-10-20", prodId1, 40, 45),
                new UpstreamObservation("2026-10-20", prodId2, 5, 5)
            ],
            IncludeExplanations: true
        );

        var response = new UpstreamForecastResponse(
            ContractVersion: "seasonal-v1",
            AggregationLevel: "PRODUCT",
            DataSource: "DATABASE",
            HistoryStart: "2024-11-01",
            HistoryEnd: "2026-10-31",
            ForecastStart: "2026-11-01",
            ForecastEnd: "2026-11-30",
            Season: "AUTUMN",
            Holidays: [new UpstreamHoliday("TEACHERS_DAY", "Nhà giáo Việt Nam", "2026-11-20")],
            Warnings: [],
            Products:
            [
                new UpstreamProductForecast(
                    ProductId: prodId1,
                    Name: "Bó Hoa Tri Ân",
                    ObservedInvoiceCount: 40,
                    ObservedUnits: 45,
                    SameSeasonInvoiceCount: 38,
                    ForecastInvoiceCount: 52.4,
                    ForecastUnits: 58.9,
                    ModelStatus: "POISSON_REGRESSION",
                    AnnualSeasonalityValidated: false, // Coverage alone does not validate annual seasonality.
                    Validation: new UpstreamValidation(70, 14, 1.2, 1.8, 0.11, 0.16),
                    Explanation: "Dự kiến tăng trưởng dịp 20/11.",
                    ExplanationSource: "GEMINI"
                ),
                new UpstreamProductForecast(
                    ProductId: prodId2,
                    Name: "Bó Hướng Dương",
                    ObservedInvoiceCount: 5,
                    ObservedUnits: 5,
                    SameSeasonInvoiceCount: 4,
                    ForecastInvoiceCount: null,
                    ForecastUnits: null,
                    ModelStatus: "INSUFFICIENT_HISTORY",
                    AnnualSeasonalityValidated: false,
                    Validation: null,
                    Explanation: "Chưa đủ dữ liệu.",
                    ExplanationSource: "FALLBACK"
                )
            ]
        );

        Assert.True(SeasonalForecastClient.ValidateResponse(response, req, expected, "2026-11-01", "2026-11-30", "AUTUMN"));
    }

    [Fact]
    public void UpstreamResponseValidatorRejectsPoissonWithNullForecastOrValidation()
    {
        var prodId = Guid.NewGuid();
        var req = new UpstreamForecastRequest(
            ContractVersion: "seasonal-v1",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            TargetYear: 2026,
            TargetMonth: 11,
            DataSource: "DATABASE",
            Products: [new UpstreamProduct(prodId, "Test", "2025-01-01")],
            Observations: [new UpstreamObservation("2026-10-20", prodId, 1, 1)]
        );

        // POISSON_REGRESSION but ForecastInvoiceCount is null
        var respNullForecast = new UpstreamForecastResponse(
            ContractVersion: "seasonal-v1",
            AggregationLevel: "PRODUCT",
            DataSource: "DATABASE",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            ForecastStart: "2026-11-01",
            ForecastEnd: "2026-11-30",
            Season: "AUTUMN",
            Holidays: [],
            Warnings: [],
            Products:
            [
                new UpstreamProductForecast(
                    ProductId: prodId,
                    Name: "Test",
                    ObservedInvoiceCount: 1,
                    ObservedUnits: 1,
                    SameSeasonInvoiceCount: 1,
                    ForecastInvoiceCount: null, // Invalid: POISSON must have non-null forecast
                    ForecastUnits: 2.0,
                    ModelStatus: "POISSON_REGRESSION",
                    AnnualSeasonalityValidated: false,
                    Validation: new UpstreamValidation(60, 14, 1.0, 1.5, 0.1, 0.2),
                    Explanation: "Test",
                    ExplanationSource: "FALLBACK"
                )
            ]
        );

        Assert.False(SeasonalForecastClient.ValidateResponse(respNullForecast, req, [prodId], "2026-11-01", "2026-11-30", "AUTUMN"));
    }

    [Fact]
    public void UpstreamResponseValidatorRejectsAnnualSeasonalityWithoutTwoYearsHistory()
    {
        var prodId = Guid.NewGuid();
        var req = new UpstreamForecastRequest(
            ContractVersion: "seasonal-v1",
            HistoryStart: "2026-01-01",
            HistoryEnd: "2026-10-31", // Only 10 months history (< 730 days)
            TargetYear: 2026,
            TargetMonth: 11,
            DataSource: "DATABASE",
            Products: [new UpstreamProduct(prodId, "Test", "2026-01-01")],
            Observations: [new UpstreamObservation("2026-10-20", prodId, 1, 1)]
        );

        var respInvalidAnnual = new UpstreamForecastResponse(
            ContractVersion: "seasonal-v1",
            AggregationLevel: "PRODUCT",
            DataSource: "DATABASE",
            HistoryStart: "2026-01-01",
            HistoryEnd: "2026-10-31",
            ForecastStart: "2026-11-01",
            ForecastEnd: "2026-11-30",
            Season: "AUTUMN",
            Holidays: [],
            Warnings: [],
            Products:
            [
                new UpstreamProductForecast(
                    ProductId: prodId,
                    Name: "Test",
                    ObservedInvoiceCount: 1,
                    ObservedUnits: 1,
                    SameSeasonInvoiceCount: 1,
                    ForecastInvoiceCount: 5.0,
                    ForecastUnits: 6.0,
                    ModelStatus: "POISSON_REGRESSION",
                    AnnualSeasonalityValidated: true, // Invalid: Cannot validate without 2 full years!
                    Validation: new UpstreamValidation(60, 14, 1.0, 1.5, 0.1, 0.2),
                    Explanation: "Test",
                    ExplanationSource: "FALLBACK"
                )
            ]
        );

        Assert.False(SeasonalForecastClient.ValidateResponse(respInvalidAnnual, req, [prodId], "2026-11-01", "2026-11-30", "AUTUMN"));
    }

    [Fact]
    public void UpstreamResponseValidatorRejectsInconsistentObservedCounts()
    {
        var prodId = Guid.NewGuid();
        var req = new UpstreamForecastRequest(
            ContractVersion: "seasonal-v1",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            TargetYear: 2026,
            TargetMonth: 11,
            DataSource: "DATABASE",
            Products: [new UpstreamProduct(prodId, "Test", "2025-01-01")],
            Observations: [new UpstreamObservation("2026-10-20", prodId, 10, 12)]
        );

        var respWrongCount = new UpstreamForecastResponse(
            ContractVersion: "seasonal-v1",
            AggregationLevel: "PRODUCT",
            DataSource: "DATABASE",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            ForecastStart: "2026-11-01",
            ForecastEnd: "2026-11-30",
            Season: "AUTUMN",
            Holidays: [],
            Warnings: [],
            Products:
            [
                new UpstreamProductForecast(
                    ProductId: prodId,
                    Name: "Test",
                    ObservedInvoiceCount: 999, // Mismatched count!
                    ObservedUnits: 12,
                    SameSeasonInvoiceCount: 10,
                    ForecastInvoiceCount: 5.0,
                    ForecastUnits: 6.0,
                    ModelStatus: "POISSON_REGRESSION",
                    AnnualSeasonalityValidated: false,
                    Validation: new UpstreamValidation(60, 14, 1.0, 1.5, 0.1, 0.2),
                    Explanation: "Test",
                    ExplanationSource: "FALLBACK"
                )
            ]
        );

        Assert.False(SeasonalForecastClient.ValidateResponse(respWrongCount, req, [prodId], "2026-11-01", "2026-11-30", "AUTUMN"));
    }

    [Theory]
    [InlineData("wrong-version", "PRODUCT", "DATABASE", "2026-11-01", "2026-11-30", "AUTUMN")]
    [InlineData("seasonal-v1", "CATEGORY", "DATABASE", "2026-11-01", "2026-11-30", "AUTUMN")]
    [InlineData("seasonal-v1", "PRODUCT", "SYNTHETIC", "2026-11-01", "2026-11-30", "AUTUMN")]
    [InlineData("seasonal-v1", "PRODUCT", "DATABASE", "2026-11-02", "2026-11-30", "AUTUMN")]
    [InlineData("seasonal-v1", "PRODUCT", "DATABASE", "2026-11-01", "2026-11-29", "AUTUMN")]
    [InlineData("seasonal-v1", "PRODUCT", "DATABASE", "2026-11-01", "2026-11-30", "WINTER")]
    public void UpstreamResponseValidatorRejectsMismatchedRequestAttributes(
        string version, string aggLevel, string dataSource, string forecastStart, string forecastEnd, string season)
    {
        var prodId = Guid.NewGuid();
        var req = new UpstreamForecastRequest(
            ContractVersion: "seasonal-v1",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            TargetYear: 2026,
            TargetMonth: 11,
            DataSource: "DATABASE",
            Products: [new UpstreamProduct(prodId, "Test", "2025-01-01")],
            Observations: []
        );

        var response = new UpstreamForecastResponse(
            ContractVersion: version,
            AggregationLevel: aggLevel,
            DataSource: dataSource,
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            ForecastStart: forecastStart,
            ForecastEnd: forecastEnd,
            Season: season,
            Holidays: [],
            Warnings: [],
            Products:
            [
                new UpstreamProductForecast(
                    ProductId: prodId,
                    Name: "Test",
                    ObservedInvoiceCount: 0,
                    ObservedUnits: 0,
                    SameSeasonInvoiceCount: 0,
                    ForecastInvoiceCount: null,
                    ForecastUnits: null,
                    ModelStatus: "INSUFFICIENT_HISTORY",
                    AnnualSeasonalityValidated: false,
                    Validation: null,
                    Explanation: "Test",
                    ExplanationSource: "FALLBACK"
                )
            ]
        );

        Assert.False(SeasonalForecastClient.ValidateResponse(response, req, [prodId], "2026-11-01", "2026-11-30", "AUTUMN"));
    }

    [Fact]
    public void UpstreamResponseValidatorRejectsInsufficientHistoryWithNonNullForecasts()
    {
        var prodId = Guid.NewGuid();
        var req = new UpstreamForecastRequest(
            ContractVersion: "seasonal-v1",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            TargetYear: 2026,
            TargetMonth: 11,
            DataSource: "DATABASE",
            Products: [new UpstreamProduct(prodId, "Test", "2025-01-01")],
            Observations: []
        );

        var response = new UpstreamForecastResponse(
            ContractVersion: "seasonal-v1",
            AggregationLevel: "PRODUCT",
            DataSource: "DATABASE",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            ForecastStart: "2026-11-01",
            ForecastEnd: "2026-11-30",
            Season: "AUTUMN",
            Holidays: [],
            Warnings: [],
            Products:
            [
                new UpstreamProductForecast(
                    ProductId: prodId,
                    Name: "Test",
                    ObservedInvoiceCount: 0,
                    ObservedUnits: 0,
                    SameSeasonInvoiceCount: 0,
                    ForecastInvoiceCount: 10.0, // Invalid: must be null for INSUFFICIENT_HISTORY
                    ForecastUnits: null,
                    ModelStatus: "INSUFFICIENT_HISTORY",
                    AnnualSeasonalityValidated: false,
                    Validation: null,
                    Explanation: "Test",
                    ExplanationSource: "FALLBACK"
                )
            ]
        );

        Assert.False(SeasonalForecastClient.ValidateResponse(response, req, [prodId], "2026-11-01", "2026-11-30", "AUTUMN"));
    }

    [Fact]
    public void UpstreamResponseValidatorRejectsUnknownOrMissingProductIds()
    {
        var knownProd = Guid.NewGuid();
        var foreignProd = Guid.NewGuid();

        var req = new UpstreamForecastRequest(
            ContractVersion: "seasonal-v1",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            TargetYear: 2026,
            TargetMonth: 11,
            DataSource: "DATABASE",
            Products: [new UpstreamProduct(knownProd, "Known", "2025-01-01")],
            Observations: []
        );

        var responseWithForeign = new UpstreamForecastResponse(
            ContractVersion: "seasonal-v1",
            AggregationLevel: "PRODUCT",
            DataSource: "DATABASE",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            ForecastStart: "2026-11-01",
            ForecastEnd: "2026-11-30",
            Season: "AUTUMN",
            Holidays: [],
            Warnings: [],
            Products:
            [
                new UpstreamProductForecast(
                    ProductId: foreignProd, // Foreign ID!
                    Name: "Foreign flower",
                    ObservedInvoiceCount: 0,
                    ObservedUnits: 0,
                    SameSeasonInvoiceCount: 0,
                    ForecastInvoiceCount: null,
                    ForecastUnits: null,
                    ModelStatus: "INSUFFICIENT_HISTORY",
                    AnnualSeasonalityValidated: false,
                    Validation: null,
                    Explanation: "Foreign",
                    ExplanationSource: "FALLBACK"
                )
            ]
        );

        Assert.False(SeasonalForecastClient.ValidateResponse(responseWithForeign, req, [knownProd], "2026-11-01", "2026-11-30", "AUTUMN"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.5)]
    public void UpstreamResponseValidatorRejectsNonFiniteOrNegativeForecasts(double invalidVal)
    {
        var prodId = Guid.NewGuid();
        var req = new UpstreamForecastRequest(
            ContractVersion: "seasonal-v1",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            TargetYear: 2026,
            TargetMonth: 11,
            DataSource: "DATABASE",
            Products: [new UpstreamProduct(prodId, "Test", "2025-01-01")],
            Observations: [new UpstreamObservation("2026-10-20", prodId, 1, 1)]
        );

        var response = new UpstreamForecastResponse(
            ContractVersion: "seasonal-v1",
            AggregationLevel: "PRODUCT",
            DataSource: "DATABASE",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            ForecastStart: "2026-11-01",
            ForecastEnd: "2026-11-30",
            Season: "AUTUMN",
            Holidays: [],
            Warnings: [],
            Products:
            [
                new UpstreamProductForecast(
                    ProductId: prodId,
                    Name: "Test",
                    ObservedInvoiceCount: 1,
                    ObservedUnits: 1,
                    SameSeasonInvoiceCount: 1,
                    ForecastInvoiceCount: invalidVal,
                    ForecastUnits: 1.0,
                    ModelStatus: "POISSON_REGRESSION",
                    AnnualSeasonalityValidated: false,
                    Validation: new UpstreamValidation(60, 14, 1.0, 1.5, 0.1, 0.2),
                    Explanation: "Test",
                    ExplanationSource: "FALLBACK"
                )
            ]
        );

        Assert.False(SeasonalForecastClient.ValidateResponse(response, req, [prodId], "2026-11-01", "2026-11-30", "AUTUMN"));
    }

    [Fact]
    public void UpstreamResponseValidatorRejectsDuplicateProductIdsInResponse()
    {
        var prodId = Guid.NewGuid();
        var req = new UpstreamForecastRequest(
            ContractVersion: "seasonal-v1",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            TargetYear: 2026,
            TargetMonth: 11,
            DataSource: "DATABASE",
            Products: [new UpstreamProduct(prodId, "Test", "2025-01-01")],
            Observations: []
        );

        var item = new UpstreamProductForecast(
            ProductId: prodId,
            Name: "Test",
            ObservedInvoiceCount: 0,
            ObservedUnits: 0,
            SameSeasonInvoiceCount: 0,
            ForecastInvoiceCount: null,
            ForecastUnits: null,
            ModelStatus: "INSUFFICIENT_HISTORY",
            AnnualSeasonalityValidated: false,
            Validation: null,
            Explanation: "Test",
            ExplanationSource: "FALLBACK"
        );

        var response = new UpstreamForecastResponse(
            ContractVersion: "seasonal-v1",
            AggregationLevel: "PRODUCT",
            DataSource: "DATABASE",
            HistoryStart: "2025-11-01",
            HistoryEnd: "2026-10-31",
            ForecastStart: "2026-11-01",
            ForecastEnd: "2026-11-30",
            Season: "AUTUMN",
            Holidays: [],
            Warnings: [],
            Products: [item, item] // Duplicated ID
        );

        Assert.False(SeasonalForecastClient.ValidateResponse(response, req, [prodId], "2026-11-01", "2026-11-30", "AUTUMN"));
    }

    [Fact]
    public async Task SeasonalForecastClientRejectsMalformedConfigAndUrls()
    {
        var handler = new TestFakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);

        // Missing or short token (< 32 chars)
        var shortTokenConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI_SERVICE_URL"] = "http://ai-service.local:8000",
            ["AI_SERVICE_TOKEN"] = "short-token"
        }).Build();

        var clientShortToken = new SeasonalForecastClient(httpClient, shortTokenConfig);
        var req = new UpstreamForecastRequest("seasonal-v1", "2026-01-01", "2026-10-31", 2026, 11, "DATABASE", [], []);
        var result1 = await clientShortToken.ForecastAsync(req, [], "2026-11-01", "2026-11-30", "AUTUMN", CancellationToken.None);
        Assert.Null(result1);

        // Untrusted URL with query parameters or credentials
        var badUrlConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI_SERVICE_URL"] = "http://user:pass@ai-service.local:8000/path?query=1",
            ["AI_SERVICE_TOKEN"] = "test-service-token-at-least-32-chars-length-long"
        }).Build();

        var clientBadUrl = new SeasonalForecastClient(httpClient, badUrlConfig);
        var result2 = await clientBadUrl.ForecastAsync(req, [], "2026-11-01", "2026-11-30", "AUTUMN", CancellationToken.None);
        Assert.Null(result2);
    }

    [Fact]
    public async Task SeasonalForecastClientFallsBackOnHttpFailureOrTimeout()
    {
        // 503 Service Unavailable response
        var failHandler = new TestFakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var httpClientFail = new HttpClient(failHandler);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI_SERVICE_URL"] = "http://ai-service.local:8000",
            ["AI_SERVICE_TOKEN"] = "test-service-token-at-least-32-chars-length-long"
        }).Build();

        var client = new SeasonalForecastClient(httpClientFail, config);
        var req = new UpstreamForecastRequest("seasonal-v1", "2026-01-01", "2026-10-31", 2026, 11, "DATABASE", [], []);
        var res = await client.ForecastAsync(req, [], "2026-11-01", "2026-11-30", "AUTUMN", CancellationToken.None);
        Assert.Null(res); // Falls back to null so caller delivers actual stats
    }

    [Fact]
    public void TwoSellersDifferentKiosksSameProductNameKeepsDistinctIdsAndPhotos()
    {
        var seller1Id = Guid.NewGuid();
        var seller2Id = Guid.NewGuid();

        var prod1Id = Guid.NewGuid();
        var prod2Id = Guid.NewGuid();

        var photo1Url = "https://res.cloudinary.com/florabot/image/upload/v1/florabot/prod1.jpg";
        var photo2Url = "https://res.cloudinary.com/florabot/image/upload/v1/florabot/prod2.jpg";

        var p1 = new SeasonalSalesProduct(
            prod1Id, "Bó Hoa Hồng Đỏ", "ACTIVE", 350000, ["ROSE", "RED"], new DateOnly(2026, 1, 1), [photo1Url], 10, 12
        );

        var p2 = new SeasonalSalesProduct(
            prod2Id, "Bó Hoa Hồng Đỏ", "ACTIVE", 450000, ["ROSE", "PREMIUM"], new DateOnly(2026, 2, 1), [photo2Url], 8, 8
        );

        Assert.NotEqual(p1.ProductId, p2.ProductId);
        Assert.Equal(p1.Name, p2.Name);
        Assert.NotEqual(p1.Price, p2.Price);
        Assert.NotEqual(p1.Photos[0], p2.Photos[0]);
    }

    [Fact]
    public void UtcToVnTimezoneBoundaryMapsPaidDateCorrectly()
    {
        // 2026-10-19 18:30:00 UTC = 2026-10-20 01:30:00 Asia/Ho_Chi_Minh (UTC+7)
        var utcTime = new DateTime(2026, 10, 19, 18, 30, 0, DateTimeKind.Utc);
        var vnTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, SeasonalCalendar.VnTimeZone);
        var vnDate = DateOnly.FromDateTime(vnTime);

        Assert.Equal(new DateOnly(2026, 10, 20), vnDate);
        Assert.True(SeasonalCalendar.IsDateInHolidayWindow(vnDate, "VN_WOMENS_DAY"));
    }

    [Fact]
    public void ZeroHistoryProductYieldsActualStatsWithoutClaimedPrediction()
    {
        var prodId = Guid.NewGuid();
        var item = new ForecastProductItem(
            ProductId: prodId,
            Name: "Bó Ly Trắng Mới",
            Status: "ACTIVE",
            Price: 200000,
            Tags: ["LILY", "WHITE"],
            AvailableSince: new DateOnly(2026, 10, 1),
            Photos: ["https://res.cloudinary.com/florabot/image/upload/v1/florabot/lily.jpg"],
            ObservedInvoiceCount: 0,
            ObservedUnits: 0,
            SameSeasonInvoiceCount: 0,
            ForecastInvoiceCount: null,
            ForecastUnits: null,
            ModelStatus: "INSUFFICIENT_HISTORY",
            AnnualSeasonalityValidated: false,
            Validation: null,
            Explanation: "Sản phẩm chưa có đủ lịch sử để dự báo.",
            ExplanationSource: "FALLBACK"
        );

        Assert.Equal(0, item.ObservedInvoiceCount);
        Assert.Equal(0, item.ObservedUnits);
        Assert.Null(item.ForecastInvoiceCount);
        Assert.Null(item.ForecastUnits);
        Assert.Equal("INSUFFICIENT_HISTORY", item.ModelStatus);
        Assert.False(item.AnnualSeasonalityValidated);
        Assert.Equal("FALLBACK", item.ExplanationSource);
    }

    [Fact]
    public void AuthoritativeMetadataBindingOverridesUpstreamImaginedValues()
    {
        var prodId = Guid.NewGuid();
        var authoritativePhoto = "https://res.cloudinary.com/florabot/image/upload/v1/florabot/real.jpg";

        var upstreamItem = new UpstreamProductForecast(
            ProductId: prodId,
            Name: "Model Imagined Name",
            ObservedInvoiceCount: 20,
            ObservedUnits: 25,
            SameSeasonInvoiceCount: 15,
            ForecastInvoiceCount: 30.5,
            ForecastUnits: 35.0,
            ModelStatus: "POISSON_REGRESSION",
            AnnualSeasonalityValidated: false, // Python will mark false unless validated
            Validation: new UpstreamValidation(90, 14, 1.1, 1.9, 0.08, 0.15),
            Explanation: "Mùa thu có nhu cầu tăng.",
            ExplanationSource: "GEMINI"
        );

        // BE binds authoritative properties: Name, Status, Price, Tags, Photos from catalog
        var finalItem = new ForecastProductItem(
            ProductId: prodId,
            Name: "Bó Hướng Dương Rực Rỡ", // Authoritative name from DB
            Status: "ACTIVE",
            Price: 300000,
            Tags: ["SUNFLOWER", "YELLOW"],
            AvailableSince: new DateOnly(2025, 5, 1),
            Photos: [authoritativePhoto], // Authoritative photo from notify.attachments
            ObservedInvoiceCount: upstreamItem.ObservedInvoiceCount,
            ObservedUnits: upstreamItem.ObservedUnits,
            SameSeasonInvoiceCount: upstreamItem.SameSeasonInvoiceCount,
            ForecastInvoiceCount: upstreamItem.ForecastInvoiceCount,
            ForecastUnits: upstreamItem.ForecastUnits,
            ModelStatus: upstreamItem.ModelStatus,
            AnnualSeasonalityValidated: upstreamItem.AnnualSeasonalityValidated,
            Validation: new ProductForecastValidation(
                upstreamItem.Validation!.TrainingDays,
                upstreamItem.Validation.HoldoutDays,
                upstreamItem.Validation.Mae,
                upstreamItem.Validation.BaselineMae,
                upstreamItem.Validation.Wape,
                upstreamItem.Validation.BaselineWape
            ),
            Explanation: upstreamItem.Explanation!,
            ExplanationSource: upstreamItem.ExplanationSource!
        );

        Assert.Equal("Bó Hướng Dương Rực Rỡ", finalItem.Name);
        Assert.NotEqual("Model Imagined Name", finalItem.Name);
        Assert.Single(finalItem.Photos);
        Assert.Equal(authoritativePhoto, finalItem.Photos[0]);
        Assert.Equal(30.5, finalItem.ForecastInvoiceCount);
        Assert.False(finalItem.AnnualSeasonalityValidated);
    }

    private sealed class TestFakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(response);
        }
    }
}
