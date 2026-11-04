using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace FloraBot.Api.Modules.Ordering;

public sealed record KioskPreorderAvailabilityResponse(
    Guid KioskId,
    string? KioskCode,
    string? KioskName,
    bool Available,
    int TotalPreorderCapacity,
    int OccupiedPreorderCount,
    int AvailablePreorderCapacity,
    string Status,
    string Message,
    bool IsEstimateOnly = true
);

public static class PreorderCapacity
{
    public static IEndpointRouteBuilder MapPreorderCapacity(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/kiosks/{kioskId:guid}/preorder-availability", async (
            Guid kioskId,
            DateTimeOffset? pickupAt,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            // Business validation: pickup time must be in the future (no invented 30-day cap on discovery)
            if (pickupAt.HasValue)
            {
                var now = DateTimeOffset.UtcNow;
                if (pickupAt.Value <= now)
                {
                    return Results.Problem(
                        statusCode: 400,
                        detail: "Thời gian nhận hoa phải sau thời điểm hiện tại.");
                }
            }

            await using var connection = await data.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT
                    kiosk_id::uuid,
                    kiosk_code::text,
                    kiosk_name::text,
                    is_available::boolean,
                    total_capacity::integer,
                    occupied_count::integer,
                    available_capacity::integer,
                    status_code::text
                FROM flow.get_kiosk_preorder_availability(@kiosk)
                """, connection);
            cmd.Parameters.AddWithValue("kiosk", kioskId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return Results.NotFound();
            }

            var id = reader.GetGuid(0);
            var code = reader.IsDBNull(1) ? null : reader.GetString(1);
            var name = reader.IsDBNull(2) ? null : reader.GetString(2);
            var isAvailable = reader.GetBoolean(3);
            var totalCapacity = reader.GetInt32(4);
            var occupiedCount = reader.GetInt32(5);
            var availableCapacity = reader.GetInt32(6);
            var statusCode = reader.GetString(7);

            if (statusCode == "KIOSK_NOT_FOUND")
            {
                return Results.NotFound();
            }

            var message = statusCode switch
            {
                "AVAILABLE" => "Kiosk sẵn sàng nhận đơn đặt trước (ước tính khả dụng).",
                "CAPACITY_EXHAUSTED" => "Kiosk đã hết sức chứa đặt trước (ước tính khả dụng).",
                "NO_PREORDER_SLOTS" => "Kiosk chưa có ô đặt trước.",
                "KIOSK_TERMINATING" => "Kiosk đang trong quá trình chấm dứt hợp đồng, không nhận đơn đặt trước mới.",
                "NO_ACTIVE_LEASE" => "Kiosk chưa có gói thuê đang hoạt động.",
                "KIOSK_UNAVAILABLE" => "Kiosk hiện đang tạm ngưng hoặc bảo trì.",
                "KIOSK_LOCATION_INCOMPLETE" => "Kiosk chưa hoàn tất cài đặt vị trí.",
                _ => "Không khả dụng."
            };

            var response = new KioskPreorderAvailabilityResponse(
                KioskId: id,
                KioskCode: code,
                KioskName: name,
                Available: isAvailable,
                TotalPreorderCapacity: totalCapacity,
                OccupiedPreorderCount: occupiedCount,
                AvailablePreorderCapacity: availableCapacity,
                Status: statusCode,
                Message: message,
                IsEstimateOnly: true
            );

            return Results.Ok(response);
        })
        .AllowAnonymous()
        .WithTags("Preorders")
        .Produces<KioskPreorderAvailabilityResponse>()
        .ProducesProblem(400)
        .ProducesProblem(404);

        return app;
    }
}
