using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FloraBot.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record KioskCoordinates(double Latitude, double Longitude);

public sealed record KioskLocationItem(
    Guid Id,
    string Code,
    string Name,
    string Address,
    string Region,
    string Status,
    double Latitude,
    double Longitude,
    KioskCoordinates Coordinates,
    bool LocationComplete,
    double? DistanceKm,
    string? DirectionsUrl);

public sealed record SellerKioskDto(
    Guid Id,
    string Code,
    string Name,
    string Address,
    string Region,
    string Status,
    double? Latitude,
    double? Longitude,
    KioskCoordinates? Coordinates,
    bool LocationComplete,
    bool CanEditLocation);

public sealed record UpdateKioskLocationRequest(
    string? Address,
    double? Latitude,
    double? Longitude,
    string? Reason = null);

public static class KioskLocations
{
    public static void MapKioskLocations(this WebApplication app)
    {
        // Public endpoint: complete, non-DISABLED physical kiosk locations with optional distance & directions
        app.MapGet("/api/kiosk-locations", async (
            double? latitude,
            double? longitude,
            double? radiusKm,
            int? limit,
            FloraDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (latitude.HasValue != longitude.HasValue)
            {
                return Results.Problem(statusCode: 400, title: "Dữ liệu không hợp lệ", detail: "Vĩ độ (latitude) và kinh độ (longitude) phải được cung cấp cùng nhau theo cặp.");
            }

            if (latitude.HasValue)
            {
                if (!double.IsFinite(latitude.Value) || !double.IsFinite(longitude!.Value))
                {
                    return Results.Problem(statusCode: 400, title: "Dữ liệu không hợp lệ", detail: "Tọa độ phải là số thực hữu hạn.");
                }

                if (latitude.Value is < -90.0 or > 90.0 || longitude.Value is < -180.0 or > 180.0)
                {
                    return Results.Problem(statusCode: 400, title: "Dữ liệu không hợp lệ", detail: "Tọa độ nằm ngoài phạm vi hợp lệ (-90 đến 90 cho vĩ độ, -180 đến 180 cho kinh độ).");
                }
            }

            if (radiusKm.HasValue)
            {
                if (!latitude.HasValue)
                {
                    return Results.Problem(statusCode: 400, title: "Dữ liệu không hợp lệ", detail: "Bán kính (radiusKm) chỉ có thể áp dụng khi cung cấp tọa độ người dùng.");
                }

                if (!double.IsFinite(radiusKm.Value) || radiusKm.Value <= 0.0)
                {
                    return Results.Problem(statusCode: 400, title: "Dữ liệu không hợp lệ", detail: "Bán kính (radiusKm) phải là số thực dương hữu hạn.");
                }
            }

            var takeLimit = limit ?? 50;
            if (takeLimit is < 1 or > 100)
            {
                return Results.Problem(statusCode: 400, title: "Dữ liệu không hợp lệ", detail: "Giới hạn kết quả (limit) phải từ 1 đến 100.");
            }

            var candidates = await db.Kiosks.AsNoTracking()
                .Where(k => k.Status != "DISABLED" &&
                            k.Latitude != null &&
                            k.Longitude != null &&
                            k.Address != null &&
                            k.Address.Trim() != "")
                .Select(k => new
                {
                    k.Id,
                    k.Code,
                    k.Name,
                    k.Address,
                    k.Region,
                    k.Status,
                    Latitude = k.Latitude!.Value,
                    Longitude = k.Longitude!.Value
                })
                .ToListAsync(ct);

            IEnumerable<KioskLocationItem> items;
            if (latitude.HasValue)
            {
                var userLat = latitude.Value;
                var userLon = longitude!.Value;
                var query = candidates.Select(k => new
                {
                    Kiosk = k,
                    RawDistance = HaversineDistanceKm(userLat, userLon, k.Latitude, k.Longitude)
                });

                if (radiusKm.HasValue)
                {
                    query = query.Where(x => x.RawDistance <= radiusKm.Value);
                }

                items = query
                    .OrderBy(x => x.RawDistance)
                    .ThenBy(x => x.Kiosk.Code, StringComparer.Ordinal)
                    .ThenBy(x => x.Kiosk.Id)
                    .Take(takeLimit)
                    .Select(x => new KioskLocationItem(
                        x.Kiosk.Id,
                        x.Kiosk.Code,
                        x.Kiosk.Name,
                        x.Kiosk.Address,
                        x.Kiosk.Region,
                        x.Kiosk.Status,
                        x.Kiosk.Latitude,
                        x.Kiosk.Longitude,
                        new KioskCoordinates(x.Kiosk.Latitude, x.Kiosk.Longitude),
                        true,
                        Math.Round(x.RawDistance, 2),
                        BuildDirectionsUrl(x.Kiosk.Latitude, x.Kiosk.Longitude)));
            }
            else
            {
                items = candidates
                    .Select(k => new KioskLocationItem(
                        k.Id,
                        k.Code,
                        k.Name,
                        k.Address,
                        k.Region,
                        k.Status,
                        k.Latitude,
                        k.Longitude,
                        new KioskCoordinates(k.Latitude, k.Longitude),
                        true,
                        null,
                        BuildDirectionsUrl(k.Latitude, k.Longitude)))
                    .OrderBy(x => x.Name, StringComparer.Ordinal)
                    .ThenBy(x => x.Code, StringComparer.Ordinal)
                    .ThenBy(x => x.Id)
                    .Take(takeLimit);
            }

            return Results.Ok(items.ToList());
        })
        .AllowAnonymous()
        .WithTags("KioskOps")
        .WithSummary("Danh sách điểm kiosk hoàn chỉnh cho khách hàng tra cứu và chỉ đường")
        .Produces<List<KioskLocationItem>>()
        .ProducesProblem(400);

        // Seller self-only: derive the shop from the authenticated token.
        app.MapGet("/api/me/kiosks", async (
            FloraDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var sellerClaim = http.User.FindFirstValue("seller_id");
            if (!Guid.TryParse(sellerClaim, out var sellerId) || !IsAuthorizedSeller(http, sellerId))
                return Results.Forbid();

            return await ReadSellerKiosksAsync(sellerId, db, http, ct);
        })
        .RequireAuthorization("Seller")
        .WithTags("KioskOps")
        .WithSummary("Danh sách kiosk của seller đang đăng nhập (seller_id lấy từ token)")
        .Produces<List<SellerKioskDto>>();

        // Keep the path-parameter route for existing clients; new clients should use /api/me/kiosks.
        app.MapGet("/api/sellers/{sellerId:guid}/kiosks", async (
            Guid sellerId,
            FloraDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!IsAuthorizedSeller(http, sellerId)) return Results.NotFound();
            return await ReadSellerKiosksAsync(sellerId, db, http, ct);
        })
        .RequireAuthorization("Seller", "SameSeller")
        .WithTags("KioskOps")
        .WithSummary("Tương thích cũ: danh sách kiosk thuộc seller; dùng GET /api/me/kiosks cho route tự nhận seller từ token")
        .Produces<List<SellerKioskDto>>();

        // Seller submits a location change request; only the assigned Operations Manager applies it.
        app.MapPut("/api/sellers/{sellerId:guid}/kiosks/{kioskId:guid}/location", KioskLocationChange.SubmitAsync)
            .RequireAuthorization("Seller", "SameSeller")
            .WithTags("KioskOps")
            .WithSummary("Gửi yêu cầu đổi địa chỉ và tọa độ kiosk tới Operations Manager")
            .Produces<KioskLocationChangeResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
    }

    public static async Task<bool> IsPreorderReadyAsync(FloraDbContext db, Guid kioskId, CancellationToken ct)
    {
        return await db.Kiosks.AsNoTracking()
            .AnyAsync(k => k.Id == kioskId &&
                           k.Status != "DISABLED" &&
                           k.Status != "MAINTENANCE" &&
                           k.Address != null &&
                           k.Address.Trim() != "" &&
                           k.Latitude != null &&
                           k.Longitude != null, ct);
    }

    public static double HaversineDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusKm = 6371.0;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var rLat1 = DegreesToRadians(lat1);
        var rLat2 = DegreesToRadians(lat2);

        var sinHalfLat = Math.Sin(dLat / 2.0);
        var sinHalfLon = Math.Sin(dLon / 2.0);

        var a = sinHalfLat * sinHalfLat +
                Math.Cos(rLat1) * Math.Cos(rLat2) * sinHalfLon * sinHalfLon;
        a = Math.Clamp(a, 0.0, 1.0);
        var c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
        return EarthRadiusKm * c;
    }

    public static string BuildDirectionsUrl(double latitude, double longitude)
    {
        var latStr = latitude.ToString(CultureInfo.InvariantCulture);
        var lonStr = longitude.ToString(CultureInfo.InvariantCulture);
        var destination = Uri.EscapeDataString($"{latStr},{lonStr}");
        return $"https://www.google.com/maps/dir/?api=1&destination={destination}";
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);

    private static async Task<IResult> ReadSellerKiosksAsync(
        Guid sellerId,
        FloraDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var kiosks = await db.Kiosks.AsNoTracking()
            .Where(k => k.SellerId == sellerId)
            .OrderBy(k => k.Code)
            .ThenBy(k => k.Id)
            .ToListAsync(ct);

        var result = kiosks.Select(k =>
        {
            var canEditLocation = false;
            var isComplete = !string.IsNullOrWhiteSpace(k.Address) && k.Latitude.HasValue && k.Longitude.HasValue;
            var coords = k.Latitude.HasValue && k.Longitude.HasValue
                ? new KioskCoordinates(k.Latitude.Value, k.Longitude.Value)
                : null;

            return new SellerKioskDto(
                k.Id,
                k.Code,
                k.Name,
                k.Address,
                k.Region,
                k.Status,
                k.Latitude,
                k.Longitude,
                coords,
                isComplete,
                canEditLocation);
        }).ToList();

        return Results.Ok(result);
    }

    private static bool IsAuthorizedSeller(HttpContext http, Guid sellerId)
    {
        if (http.User.HasClaim(c => c.Type == "kiosk_id")) return false;
        if (!http.User.IsInRole("SELLER")) return false;
        var sellerIdClaim = http.User.FindFirstValue("seller_id");
        return Guid.TryParse(sellerIdClaim, out var claimId) && claimId == sellerId;
    }

    private static Guid? GetActorId(ClaimsPrincipal user)
    {
        var idStr = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(idStr, out var id) ? id : null;
    }
}
