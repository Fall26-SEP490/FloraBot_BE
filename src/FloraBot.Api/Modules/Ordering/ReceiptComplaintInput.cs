using System.Text.Json;

namespace FloraBot.Api.Modules.Ordering;

public static class ReceiptComplaintInput
{
    public static IResult? Normalize(Dictionary<string, JsonElement> args)
    {
        var reason = args.TryGetValue("p_reason", out var reasonValue) && reasonValue.ValueKind == JsonValueKind.String ? reasonValue.GetString()?.Trim() : null;
        var photo = args.TryGetValue("p_photo", out var photoValue) && photoValue.ValueKind == JsonValueKind.String ? photoValue.GetString()?.Trim() : null;
        var errors = new Dictionary<string, string[]>();
        if (reason is null || reason.Length is < 5 or > 2000 || reason.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            errors["p_reason"] = ["Mô tả vấn đề từ 5 đến 2.000 ký tự, không chứa ký tự điều khiển."];
        if (photo is null || photo.Length > 2048 || photo.Any(char.IsWhiteSpace) || !Uri.TryCreate(photo, UriKind.Absolute, out var url) || url.Scheme != "https" || string.IsNullOrEmpty(url.Host) || !string.IsNullOrEmpty(url.UserInfo))
            errors["p_photo"] = ["Nhập liên kết HTTPS tới ảnh minh chứng, không chứa tài khoản hoặc mật khẩu."];
        if (errors.Count > 0) return Results.ValidationProblem(errors, detail: "Kiểm tra nội dung phản ánh và đường dẫn ảnh minh chứng.");
        args["p_reason"] = JsonSerializer.SerializeToElement(reason);
        args["p_photo"] = JsonSerializer.SerializeToElement(photo);
        return null;
    }
}
