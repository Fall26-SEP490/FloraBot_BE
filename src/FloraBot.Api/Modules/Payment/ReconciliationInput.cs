using System.Globalization;
using System.Text.Json;

namespace FloraBot.Api.Modules.Payment;

public static class ReconciliationInput
{
    public static IResult? Normalize(string flow, Dictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("p_date", out var date) || date.ValueKind != JsonValueKind.String ||
            !DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day == DateOnly.MinValue)
            return Invalid("p_date", "Chọn ngày cần đối soát.");
        if (flow != "reconcile_gateway") return null;
        if (!args.TryGetValue("p_statement", out var statement) || statement.ValueKind != JsonValueKind.Array || statement.GetArrayLength() > 5000)
            return Invalid("p_statement", "Sao kê phải là mảng JSON gồm tối đa 5.000 giao dịch {txn, amount}.");

        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in statement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("txn", out var txn) || txn.ValueKind != JsonValueKind.String ||
                !row.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number || !amount.TryGetInt64(out var value) || value <= 0)
                return Invalid("p_statement", "Mỗi giao dịch cần mã txn và số tiền amount là số nguyên dương.");
            var key = txn.GetString()!;
            if (key.Length is < 1 or > 200 || string.IsNullOrWhiteSpace(key) || key != key.Trim() || key.Any(char.IsControl))
                return Invalid("p_statement", "Mã giao dịch dài 1–200 ký tự, không có ký tự điều khiển hoặc khoảng trắng ở hai đầu.");
            // Preserve SQL's sum-by-transaction semantics without allowing bigint overflow.
            var current = totals.GetValueOrDefault(key);
            if (value > long.MaxValue - current)
                return Invalid("p_statement", "Tổng tiền của một mã giao dịch vượt giới hạn hỗ trợ.");
            totals[key] = current + value;
        }
        // Only reconciliation fields reach the database; discard unrelated bank statement data.
        args["p_statement"] = JsonSerializer.SerializeToElement(totals.Select(x => new { txn = x.Key, amount = x.Value }));
        return null;
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
