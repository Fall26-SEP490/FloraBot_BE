using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.ReadModels;

public sealed record AdminRevenuePage(List<JsonElement> Items, JsonElement Totals, long TotalCount, int Page, int PageSize);
public sealed record HistoricalReportKiosk(Guid Id, string Code, string Name);

public static class AdminRevenueReports
{
    public static void MapAdminRevenueReports(this WebApplication app)
    {
        app.MapGet("/api/admin/revenue/{kind}", ReadAsync)
            .RequireAuthorization("Admin").Produces<AdminRevenuePage>();
        app.MapGet("/api/admin/sellers/{sellerId:guid}/report-kiosks", async (
            Guid sellerId, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            await using var command = data.CreateCommand("""
                SELECT k.id,k.code,k.name FROM kiosk_ops.kiosks k
                WHERE EXISTS (SELECT 1 FROM ordering.orders o WHERE o.kiosk_id=k.id AND o.seller_id=@seller)
                   OR EXISTS (SELECT 1 FROM identity.subscriptions s WHERE s.kiosk_id=k.id AND s.seller_id=@seller)
                ORDER BY k.code,k.id
                """);
            command.Parameters.AddWithValue("seller", sellerId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<HistoricalReportKiosk>();
            while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
            return Results.Ok(rows);
        }).RequireAuthorization("Admin").Produces<List<HistoricalReportKiosk>>();
    }

    private static async Task<IResult> ReadAsync(string kind, DateOnly? from, DateOnly? to,
        Guid? sellerId, Guid? kioskId, int? page, int? pageSize,
        NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        var number = page ?? 1;
        var size = pageSize ?? 20;
        if (kind is not ("flowers" or "rental") || number is < 1 or > 100000 || size is < 1 or > 100
            || from > to || (kind == "rental" && (sellerId.HasValue || kioskId.HasValue))) return Results.BadRequest();
        http.Response.Headers.CacheControl = "no-store";
        // Both page and full-period totals are evaluated by one PostgreSQL statement/snapshot.
        var source = kind == "flowers" ? """
            SELECT (coalesce(a.paid_at,a.created_at) AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS day,
                   a.order_id,a.kiosk_id,a.seller_id,a.kind::text AS kind,a.amount
            FROM payment.v_order_payment_allocations a
            WHERE a.payment_status='SUCCEEDED'
              AND (@seller IS NULL OR a.seller_id=@seller) AND (@kiosk IS NULL OR a.kiosk_id=@kiosk)
              AND (@from IS NULL OR coalesce(a.paid_at,a.created_at)>=(@from::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh'))
              AND (@to IS NULL OR coalesce(a.paid_at,a.created_at)<((@to::date+1)::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh'))
            """ : """
            SELECT (l.created_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS day,
                   NULL::uuid AS order_id,NULL::uuid AS kiosk_id,NULL::uuid AS seller_id,
                   'RENTAL'::text AS kind,-l.amount AS amount
            FROM payment.ledger_entries l WHERE l.account='PLATFORM_SUBSCRIPTION'
              AND (@from IS NULL OR l.created_at>=(@from::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh'))
              AND (@to IS NULL OR l.created_at<((@to::date+1)::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh'))
            """;
        await using var command = data.CreateCommand($$"""
            WITH source AS ({{source}}), grouped AS (
                SELECT day,kiosk_id,seller_id,
                  coalesce(sum(amount) FILTER(WHERE kind='CHARGE'),0)::bigint AS gross,
                  coalesce(sum(amount) FILTER(WHERE kind='REFUND'),0)::bigint AS refunded,
                  coalesce(sum(amount) FILTER(WHERE kind='RENTAL'),0)::bigint AS rental,
                  count(DISTINCT order_id) FILTER(WHERE kind='CHARGE') AS orders
                FROM source GROUP BY day,kiosk_id,seller_id
            ), selected AS (
                SELECT g.*,k.code AS kiosk FROM grouped g LEFT JOIN kiosk_ops.kiosks k ON k.id=g.kiosk_id
                ORDER BY day DESC,kiosk_id NULLS FIRST,seller_id NULLS FIRST LIMIT @size OFFSET @offset
            )
            SELECT jsonb_build_object(
              'items',coalesce((SELECT jsonb_agg(jsonb_build_object('day',day,'kioskId',kiosk_id,'kiosk',kiosk,
                'sellerId',seller_id,'grossPaid',gross::text,'totalRefunded',refunded::text,
                'netReceipts',(gross-refunded)::text,'rentalRevenue',rental::text,'paidOrders',orders)
                ORDER BY day DESC,kiosk_id NULLS FIRST,seller_id NULLS FIRST) FROM selected),'[]'::jsonb),
              'totals',(SELECT jsonb_build_object(
                'grossPaid',coalesce(sum(amount) FILTER(WHERE kind='CHARGE'),0)::text,
                'totalRefunded',coalesce(sum(amount) FILTER(WHERE kind='REFUND'),0)::text,
                'netReceipts',(coalesce(sum(amount) FILTER(WHERE kind='CHARGE'),0)-coalesce(sum(amount) FILTER(WHERE kind='REFUND'),0))::text,
                'rentalRevenue',coalesce(sum(amount) FILTER(WHERE kind='RENTAL'),0)::text,
                'paidOrders',count(DISTINCT order_id) FILTER(WHERE kind='CHARGE')) FROM source),
              'totalCount',(SELECT count(*) FROM grouped),'page',@page,'pageSize',@size)::text
            """);
        command.Parameters.AddWithValue("from", NpgsqlDbType.Date, (object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue("to", NpgsqlDbType.Date, (object?)to ?? DBNull.Value);
        command.Parameters.AddWithValue("seller", NpgsqlDbType.Uuid, (object?)sellerId ?? DBNull.Value);
        command.Parameters.AddWithValue("kiosk", NpgsqlDbType.Uuid, (object?)kioskId ?? DBNull.Value);
        command.Parameters.AddWithValue("page", number);
        command.Parameters.AddWithValue("size", size);
        command.Parameters.AddWithValue("offset", (number - 1) * size);
        var json = JsonSerializer.Deserialize<AdminRevenuePage>((string)(await command.ExecuteScalarAsync(ct))!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return Results.Ok(json);
    }
}
