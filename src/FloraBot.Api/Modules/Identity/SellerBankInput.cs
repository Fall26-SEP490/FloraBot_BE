using System.Text.Json;

namespace FloraBot.Api.Modules.Identity;

public static class SellerBankInput
{
    public static bool ValidAccount(string value) => value.Length is >= 4 and <= 34 && value.All(char.IsAsciiLetterOrDigit);

    public static IResult? Normalize(Dictionary<string, JsonElement> args)
    {
        var errors = new Dictionary<string, string[]>();
        foreach (var (key, label, maximum) in new[] { ("p_bank", "Tên ngân hàng", 120), ("p_holder", "Tên chủ tài khoản", 120), ("p_account_enc", "Số tài khoản", 34) })
        {
            var value = args.TryGetValue(key, out var input) && input.ValueKind == JsonValueKind.String ? input.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
                errors[key] = [$"{label} không được để trống, chứa ký tự điều khiển hoặc dài quá {maximum} ký tự."];
            else if (key == "p_account_enc" && !ValidAccount(value))
                errors[key] = ["Số tài khoản gồm 4–34 chữ cái hoặc chữ số, không có khoảng trắng."];
            else args[key] = JsonSerializer.SerializeToElement(value);
        }
        return errors.Count == 0 ? null : Results.ValidationProblem(errors, detail: "Kiểm tra tên ngân hàng, tên chủ tài khoản và số tài khoản.");
    }
}
