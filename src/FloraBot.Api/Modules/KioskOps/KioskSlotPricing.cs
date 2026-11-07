using System.Security.Claims;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record KioskSlotPricingResponse(
    long? AnnualSlotPrice,
    int Version,
    bool Configured,
    DateTime? UpdatedAt,
    int TermMonths = 12,
    int MinSlots = 1,
    int MaxSlots = 50);

public sealed record UpdateKioskSlotPricingRequest(
    long AnnualSlotPrice,
    int ExpectedVersion);

public static class KioskSlotPricing
{
    private const long MaximumPrice = 9_007_199_254_740_991;

    public static void MapKioskSlotPricing(this WebApplication app)
    {
        app.MapGet("/api/kiosk-packages", async (HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            await using var connection = await data.OpenConnectionAsync(ct);
            return Results.Ok(await ReadAsync(connection, null, ct));
        })
        .AllowAnonymous()
        .WithTags("Identity")
        .WithSummary("Xem đơn giá thuê một năm cho mỗi ô kiosk")
        .Produces<KioskSlotPricingResponse>();

        app.MapPut("/api/operations/kiosk-pricing", async (
            UpdateKioskSlotPricingRequest input,
            HttpContext http,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (input.AnnualSlotPrice is <= 0 or > MaximumPrice ||
                input.ExpectedVersion < 0)
            {
                return Results.Problem(statusCode: 400,
                    detail: "Đơn giá mỗi ô phải là số nguyên dương hợp lệ; phiên bản không được âm.");
            }

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor))
                return Results.Forbid();

            await using var connection = await data.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT flow.set_kiosk_slot_pricing(@actor,@walk,@pre,@version)", connection, transaction);
                command.Parameters.AddWithValue("actor", actor);
                command.Parameters.AddWithValue("walk", input.AnnualSlotPrice);
                command.Parameters.AddWithValue("pre", input.AnnualSlotPrice);
                command.Parameters.AddWithValue("version", input.ExpectedVersion);
                await command.ExecuteNonQueryAsync(ct);

                var pricing = await ReadAsync(connection, transaction, ct);
                await transaction.CommitAsync(ct);
                return Results.Ok(pricing);
            }
            catch (PostgresException ex) when (ex.SqlState is "22023" or "23514" or "42501" or "40001" or "23505")
            {
                return ex.SqlState switch
                {
                    "42501" => Results.Forbid(),
                    "40001" or "23505" => Results.Problem(statusCode: 409,
                        detail: "Bảng giá đã thay đổi. Vui lòng tải lại bảng giá trước khi cập nhật."),
                    _ => Results.Problem(statusCode: 400, detail: "Đơn giá hoặc phiên bản bảng giá không hợp lệ.")
                };
            }
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("KioskOps")
        .WithSummary("Operations Manager cập nhật đơn giá thuê một năm chung cho mọi ô")
        .Produces<KioskSlotPricingResponse>()
        .ProducesProblem(400)
        .Produces(403)
        .ProducesProblem(409);
    }

    private static async Task<KioskSlotPricingResponse> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT annual_slot_price, version, updated_at
            FROM kiosk_ops.kiosk_slot_pricing WHERE singleton
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new KioskSlotPricingResponse(reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.GetInt32(1), !reader.IsDBNull(0), reader.GetDateTime(2))
            : new KioskSlotPricingResponse(null, 0, false, null);
    }
}
