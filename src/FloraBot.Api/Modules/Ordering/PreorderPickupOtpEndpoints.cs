using System.Security.Claims;
using Npgsql;
using StackExchange.Redis;
using Microsoft.AspNetCore.Mvc;

namespace FloraBot.Api.Modules.Ordering;

public static class PreorderPickupOtpEndpoints
{
    public static void MapPreorderPickupOtp(this WebApplication app)
    {
        app.MapPost("/api/me/preorders/{requestId:guid}/pickup-otp", (
            Guid requestId, HttpContext http, PreorderPickupOtp service, CancellationToken ct) =>
            Execute(http, () => service.Send(requestId, Guid.Parse(http.User.FindFirstValue("sub")!), ct)))
            .RequireAuthorization("Seller").RequireRateLimiting("contact-email")
            .WithTags("Ordering").WithSummary("Seller gửi OTP nhận preorder tới email đăng nhập của khách")
            .Produces<PreorderPickupOtpSent>();

        app.MapPost("/api/kiosks/{kioskId:guid}/preorders/pickup-otp/open", (
            Guid kioskId, PreorderPickupOtpOpenRequest input, HttpContext http, PreorderPickupOtp service, CancellationToken ct) =>
        {
            if (!DeviceMatches(http, kioskId)) return Task.FromResult<IResult>(Results.Forbid());
            if (input.AttemptId == Guid.Empty || input.Code is null || input.Code.Length != 8 || input.Code.Any(c => c < '0' || c > '9'))
                return Task.FromResult<IResult>(Results.Problem(statusCode: 400, detail: "Nhập đúng 8 chữ số và mã lượt nhận hợp lệ."));
            return Execute(http, () => service.Open(kioskId, input.Code, input.AttemptId, ct));
        }).RequireAuthorization("Kiosk").RequireRateLimiting("pickup-otp")
            .WithTags("Ordering").WithSummary("Nhập OTP email tại đúng kiosk và yêu cầu mở ô preorder")
            .Produces<PreorderPickupOtpOpened>();

        app.MapGet("/api/kiosks/{kioskId:guid}/preorders/pickup-otp/attempts/{attemptId:guid}", (
            Guid kioskId, Guid attemptId, [FromHeader(Name = "X-Pickup-Token")] string? watchToken, HttpContext http, PreorderPickupOtp service, CancellationToken ct) =>
        {
            if (!DeviceMatches(http, kioskId)) return Task.FromResult<IResult>(Results.Forbid());
            return Execute(http, () => service.Status(kioskId, attemptId, watchToken ?? "", ct));
        }).RequireAuthorization("Kiosk")
            .WithTags("Ordering").WithSummary("Theo dõi lệnh nhận hoa; gửi X-Pickup-Token từ kết quả mở ô")
            .Produces<PreorderPickupOtpStatus>();
    }

    private static bool DeviceMatches(HttpContext http, Guid kiosk) =>
        Guid.TryParse(http.User.FindFirstValue("kiosk_id"), out var authenticated) && authenticated == kiosk;

    private static async Task<IResult> Execute<T>(HttpContext http, Func<Task<T>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return Results.Ok(await action()); }
        catch (PickupOtpFailure ex) { return Results.Problem(statusCode: ex.Status, detail: ex.Message); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Results.Problem(statusCode: 409, detail: "Lượt nhận đã được xử lý. Kiểm tra trạng thái trước khi thử lại."); }
        catch (Exception ex) when (ex is NpgsqlException or RedisException or TimeoutException)
        { return Results.Problem(statusCode: 503, detail: "Chưa xác nhận được thao tác. Kiểm tra trạng thái trước khi thử lại."); }
    }
}
