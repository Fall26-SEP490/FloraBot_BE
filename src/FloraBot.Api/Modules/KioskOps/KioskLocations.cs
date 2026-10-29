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
    double? Longitude);

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

        // Seller self-only: Update location for wholly owned kiosk with row locking and audit
        app.MapPut("/api/sellers/{sellerId:guid}/kiosks/{kioskId:guid}/location", async (
            Guid sellerId,
            Guid kioskId,
            UpdateKioskLocationRequest input,
            NpgsqlDataSource source,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!IsAuthorizedSeller(http, sellerId)) return Results.NotFound();

            if (string.IsNullOrWhiteSpace(input.Address) || input.Address.Trim().Length > 300)
            {
                return Results.Problem(statusCode: 400, title: "Dữ liệu không hợp lệ", detail: "Địa chỉ kiosk là bắt buộc và tối đa 300 ký tự.");
            }

            if (!input.Latitude.HasValue || !input.Longitude.HasValue)
            {
                return Results.Problem(statusCode: 400, title: "Dữ liệu không hợp lệ", detail: "Vĩ độ (latitude) và kinh độ (longitude) là bắt buộc và phải được cung cấp cùng nhau.");
            }

            if (!double.IsFinite(input.Latitude.Value) || !double.IsFinite(input.Longitude.Value))
            {
                return Results.Problem(statusCode: 400, title: "Dữ liệu không hợp lệ", detail: "Tọa độ phải là số thực hữu hạn.");
            }

            if (input.Latitude.Value is < -90.0 or > 90.0 || input.Longitude.Value is < -180.0 or > 180.0)
            {
                return Results.Problem(statusCode: 400, title: "Dữ liệu không hợp lệ", detail: "Tọa độ nằm ngoài phạm vi hợp lệ (-90 đến 90 cho vĩ độ, -180 đến 180 cho kinh độ).");
            }

            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            await using var lockCmd = new NpgsqlCommand(
                "SELECT id, code, name, status, region, seller_id FROM kiosk_ops.kiosks WHERE id = @id FOR UPDATE",
                connection, transaction);
            lockCmd.Parameters.AddWithValue("id", kioskId);
            await using var reader = await lockCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound(new ProblemDetails { Title = "Không tìm thấy", Detail = "Kiosk không tồn tại." });
            }

            var code = reader.GetString(1);
            var name = reader.GetString(2);
            var status = reader.GetString(3);
            var region = reader.GetString(4);
            var kioskSellerId = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5);
            await reader.CloseAsync();

            if (status == "DISABLED")
            {
                await transaction.RollbackAsync(ct);
                return Results.Problem(statusCode: 400, title: "Kiosk bị vô hiệu hóa", detail: "Không thể chỉnh sửa vị trí cho kiosk đang bị vô hiệu hóa.");
            }

            if (kioskSellerId != sellerId)
            {
                await transaction.RollbackAsync(ct);
                return Results.Problem(statusCode: 403, title: "Không có quyền chỉnh sửa",
                    detail: "Seller chỉ được chỉnh sửa vị trí của kiosk đã được admin gán cho mình.");
            }

            var trimmedAddress = input.Address.Trim();
            await using var updateCmd = new NpgsqlCommand(
                @"UPDATE kiosk_ops.kiosks
                  SET address = @address,
                      latitude = @latitude,
                      longitude = @longitude,
                      updated_at = public.app_now()
                  WHERE id = @id",
                connection, transaction);
            updateCmd.Parameters.AddWithValue("id", kioskId);
            updateCmd.Parameters.AddWithValue("address", trimmedAddress);
            updateCmd.Parameters.AddWithValue("latitude", input.Latitude.Value);
            updateCmd.Parameters.AddWithValue("longitude", input.Longitude.Value);
            await updateCmd.ExecuteNonQueryAsync(ct);

            var actorId = GetActorId(http.User);
            var auditPayload = JsonSerializer.Serialize(new
            {
                address = trimmedAddress,
                latitude = input.Latitude.Value,
                longitude = input.Longitude.Value,
                seller_id = sellerId
            });

            await using var auditCmd = new NpgsqlCommand(
                "SELECT flow.audit(@actor, 'KIOSK_LOCATION_UPDATED', 'kiosks', @id, @payload::jsonb)",
                connection, transaction);
            auditCmd.Parameters.AddWithValue("actor", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)actorId ?? DBNull.Value);
            auditCmd.Parameters.AddWithValue("id", kioskId);
            auditCmd.Parameters.AddWithValue("payload", auditPayload);
            await auditCmd.ExecuteNonQueryAsync(ct);

            await transaction.CommitAsync(ct);

            var coords = new KioskCoordinates(input.Latitude.Value, input.Longitude.Value);
            var response = new SellerKioskDto(
                kioskId,
                code,
                name,
                trimmedAddress,
                region,
                status,
                input.Latitude.Value,
                input.Longitude.Value,
                coords,
                true,
                true);

            return Results.Ok(response);
        })
        .RequireAuthorization("Seller", "SameSeller")
        .WithTags("KioskOps")
        .WithSummary("Cập nhật địa chỉ và tọa độ ghim bản đồ cho kiosk do shop quản lý toàn quyền")
        .Produces<SellerKioskDto>()
        .ProducesProblem(400)
        .ProducesProblem(401)
        .ProducesProblem(403)
        .ProducesProblem(404);
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
            var canEditLocation = k.SellerId == sellerId && k.Status != "DISABLED";
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
