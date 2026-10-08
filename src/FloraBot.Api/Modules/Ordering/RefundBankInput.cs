using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloraBot.Api.Modules.Ordering;

public static partial class RefundBankInput
{
    [GeneratedRegex("^[A-Za-zÀ-ỹ ]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HolderPattern();

    public static IResult? Normalize(Dictionary<string, JsonElement> args)
    {
        var errors = new Dictionary<string, string[]>();
        foreach (var (key, label, maximum) in new[] { ("p_bank", "Tên ngân hàng", 120), ("p_holder", "Tên chủ tài khoản", 120), ("p_account_enc", "Số tài khoản", 34) })
        {
            var value = args.TryGetValue(key, out var input) && input.ValueKind == JsonValueKind.String ? input.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
                errors[key] = [$"{label} không được để trống, chứa ký tự điều khiển hoặc dài quá {maximum} ký tự."];
            else if (key == "p_account_enc" && (value.Length < 4 || !value.All(char.IsAsciiLetterOrDigit)))
                errors[key] = ["Số tài khoản gồm 4–34 chữ cái hoặc chữ số, không có khoảng trắng."];
            else if (key == "p_holder" && (value.Length < 2 || !HolderPattern().IsMatch(value)))
                errors[key] = ["Tên chủ tài khoản có ít nhất 2 ký tự, chỉ gồm chữ cái và khoảng trắng."];
            else args[key] = JsonSerializer.SerializeToElement(value);
        }
        return errors.Count == 0 ? null : Results.ValidationProblem(errors, detail: "Kiểm tra ngân hàng, số tài khoản và tên chủ tài khoản nhận hoàn.");
    }
}
