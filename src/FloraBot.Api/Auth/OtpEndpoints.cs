using System.Security.Claims;
using System.Text.RegularExpressions;
using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Auth;

public sealed record OtpRequest(string Phone);
public sealed record OtpVerification(string Phone, string Code);
public sealed record CustomerSession(string AccessToken, int ExpiresInSeconds, bool CanForgetAccount = true);

public static partial class OtpEndpoints
{
    [GeneratedRegex("^(0[35789][0-9]{8}|\\+84[35789][0-9]{8})$")]
    private static partial Regex PhonePattern();
    public static void MapOtp(this WebApplication app)
    {
        app.MapPost("/api/kiosks/{kioskId:guid}/otp/request", async (Guid kioskId, OtpRequest request, HttpContext http, OtpService otp, IConfiguration config, IHostEnvironment environment, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
            if (request.Phone is null || !PhonePattern().IsMatch(request.Phone)) return Results.BadRequest();
            request = request with { Phone = NormalizePhone(request.Phone) };
            var development = environment.IsDevelopment() && config.GetValue<bool>("DEV_OTP");
            var deliveryUrl = config["SMS_WEBHOOK_URL"];
            if (!development && (!Uri.TryCreate(deliveryUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
                return Results.Problem(statusCode: 503, detail: "Dịch vụ gửi mã chưa được cấu hình. Bạn vẫn có thể mua không cần đăng nhập.");
            var code = await otp.IssueAsync(kioskId, request.Phone);
            if (code is null) return Results.StatusCode(429);
            if (!development)
            {
                using var delivery = new HttpRequestMessage(HttpMethod.Post, deliveryUrl);
                delivery.Headers.Authorization = new("Bearer", config["SMS_API_TOKEN"]);
                delivery.Content = JsonContent.Create(new { phone = request.Phone, message = $"Ma FloraBot: {code}. Hieu luc 3 phut. Khong chia se ma." });
                using var result = await clients.CreateClient("sms").SendAsync(delivery, ct);
                if (!result.IsSuccessStatusCode) return Results.Problem(statusCode: 503, detail: "Chưa gửi được mã. Vui lòng thử lại sau một phút.");
            }
            return Results.Ok(new { expiresInSeconds = 180, developmentCode = development ? code : null });
        }).RequireAuthorization("Kiosk").RequireRateLimiting("auth").WithTags("Auth");
        app.MapPost("/api/kiosks/{kioskId:guid}/otp/verify", async (Guid kioskId, OtpVerification request, HttpContext http, OtpService otp, NpgsqlDataSource data, FloraDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
            if (request.Phone is null || !PhonePattern().IsMatch(request.Phone) || request.Code is null) return Results.Unauthorized();
            request = request with { Phone = NormalizePhone(request.Phone) };
            if (!await otp.VerifyAsync(kioskId, request.Phone, request.Code)) return Results.Unauthorized();
            await using var command = data.CreateCommand("SELECT flow.customer_by_phone(@phone)");
            command.Parameters.AddWithValue("phone", request.Phone);
            Guid id;
            try { id = (Guid)(await command.ExecuteScalarAsync(ct))!; }
            catch (PostgresException ex) when (ex.SqlState == "42501") { return Results.Forbid(); }
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            return Results.Ok(new CustomerSession(tokens.Issue(user, kioskId: kioskId.ToString()), 600, user.Role == "CUSTOMER"));
        }).RequireAuthorization("Kiosk").RequireRateLimiting("auth").WithTags("Auth").Produces<CustomerSession>();
    }
    private static string NormalizePhone(string phone) => phone.StartsWith("+84", StringComparison.Ordinal) ? "0" + phone[3..] : phone;
}
