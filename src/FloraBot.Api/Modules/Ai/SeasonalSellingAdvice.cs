namespace FloraBot.Api.Modules.Ai;

public sealed record SeasonalSellingSuggestion(
    string Occasion,
    DateOnly? Date,
    string Offer,
    string NextStep,
    List<string> MatchingProducts
);

public static class SeasonalSellingAdvice
{
    private sealed record Idea(string Offer, string NextStep, string[] FlowerTypes);

    private static readonly Dictionary<string, Idea> HolidayIdeas = new(StringComparer.Ordinal)
    {
        ["NEW_YEAR"] = new(
            "Bó hoa chúc năm mới tông vàng hoặc cam, kèm thiệp chúc mừng.",
            "Đăng một mẫu gọn và một mẫu kèm thiệp để xem khách hỏi mẫu nào nhiều hơn.",
            ["sunflower", "gerbera", "chrysanthemum"]),
        ["VALENTINE"] = new(
            "Bó hồng đỏ hoặc hồng pastel, kèm lựa chọn viết thiệp.",
            "Mở đặt trước với hai mức giá và chốt giờ nhận tại kiosk.",
            ["rose", "tulip", "carnation"]),
        ["WOMENS_DAY"] = new(
            "Bó tulip, cẩm chướng hoặc hồng pastel để tặng 8/3.",
            "Đăng mẫu kèm ảnh và giá rõ ràng. Dùng lượt hỏi và đặt trước để chọn mẫu làm thêm.",
            ["tulip", "carnation", "rose"]),
        ["VN_WOMENS_DAY"] = new(
            "Bó hồng, cẩm chướng hoặc cát tường tông ấm để tặng 20/10.",
            "Thử mẫu kèm thiệp và ghi rõ giờ nhận. Chuẩn bị thêm theo số đơn đã chốt.",
            ["rose", "carnation", "lisianthus"]),
        ["TEACHERS_DAY"] = new(
            "Bó hướng dương hoặc cẩm chướng gọn, kèm thiệp tri ân thầy cô.",
            "Đăng mẫu dễ mang theo và nhận trước các đơn tặng theo lớp.",
            ["sunflower", "carnation", "gerbera"]),
        ["CHRISTMAS"] = new(
            "Bó hồng hoặc cẩm chướng phối đỏ, xanh lá và kem cho dịp Giáng sinh.",
            "Thử một mẫu gói lễ và một mẫu gói thường để xem khách chọn kiểu nào.",
            ["rose", "carnation", "babys-breath"]),
    };

    public static List<SeasonalSellingSuggestion> Build(
        DateOnly forecastStart,
        IReadOnlyList<HolidayInfo> holidays,
        IEnumerable<(string Name, string[] Tags, int RecentInvoices)> catalog)
    {
        var products = catalog
            .Select(p => (p.Name, Tags: p.Tags.Select(t => t.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase), p.RecentInvoices))
            .OrderByDescending(p => p.RecentInvoices)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> Match(Idea idea) => products
            .Where(p => idea.FlowerTypes.Any(type => p.Tags.Contains(type) || p.Tags.Contains("flower:" + type)))
            .Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        var suggestions = new List<SeasonalSellingSuggestion>();
        foreach (var holiday in holidays)
        {
            if (HolidayIdeas.TryGetValue(holiday.Code, out var idea))
                suggestions.Add(new(holiday.Name, holiday.Day, idea.Offer, idea.NextStep, Match(idea)));
        }

        if (suggestions.Count > 0) return suggestions;

        var seasonal = forecastStart.Month switch
        {
            5 or 6 or 7 => (
                "Quà tốt nghiệp",
                new Idea(
                    "Thử bó hướng dương hoặc đồng tiền cỡ nhỏ, kèm thiệp chúc mừng.",
                    "Xem lịch tốt nghiệp của các trường gần kiosk rồi mở đặt trước theo ngày.",
                    ["sunflower", "gerbera", "babys-breath"])),
            8 or 9 => (
                "Quà sinh nhật và chúc mừng",
                new Idea(
                    "Thử bó phối màu sáng từ hoa hồng, đồng tiền hoặc cát tường.",
                    "Đăng hai phối màu cùng một mức giá và theo dõi lượt hỏi trước khi làm thêm.",
                    ["rose", "gerbera", "lisianthus"])),
            _ => (
                "Quà tặng trong tháng",
                new Idea(
                    "Thử bó mini dễ nhận tại kiosk và bó kèm thiệp cho khách đặt trước.",
                    "Chọn một mẫu sẵn, thử hai cách gói và theo dõi lượt hỏi với số đơn đã chốt.",
                    ["rose", "sunflower", "carnation"])),
        };

        return [new(seasonal.Item1, null, seasonal.Item2.Offer, seasonal.Item2.NextStep, Match(seasonal.Item2))];
    }
}
