using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.ReadModels;

public sealed record AdminSellerOrderRow(Guid Id, string OrderCode, string Status, long TotalAmount,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, Guid KioskId, string KioskCode, string KioskName, string Mode);
public sealed record AdminSellerOrderPage(List<AdminSellerOrderRow> Items, int Page, bool HasMore);
public sealed record AdminSellerOrderLine(string Name, int Quantity, long UnitPrice, long LineTotal, string Status);
public sealed record AdminSellerOrderPayment(Guid Id, string Kind, string Status, long Amount,
    DateTimeOffset? PaidAt, DateTimeOffset CreatedAt, string Gateway, string? GatewayTxnId);
public sealed record AdminSellerOrderDetail(Guid Id, string OrderCode, string Status, long TotalAmount,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, Guid KioskId, string KioskCode, string KioskName, string Mode,
    List<AdminSellerOrderLine> Items, List<AdminSellerOrderPayment> Payments);

public static class AdminSellerOrders
{
    private const string Orders = """
        SELECT o.id, o.order_code AS "orderCode", o.status, o.total_amount AS "totalAmount",
               o.created_at AS "createdAt", o.completed_at AS "completedAt", o.kiosk_id AS "kioskId",
               k.code AS "kioskCode", k.name AS "kioskName",
               CASE WHEN EXISTS (SELECT 1 FROM ordering.web_requests w WHERE w.order_id=o.id)
                    THEN 'PREORDER' ELSE 'WALK_IN' END AS mode
        FROM ordering.orders o JOIN kiosk_ops.kiosks k ON k.id=o.kiosk_id
        WHERE o.seller_id=@seller
        """;

    public static void MapAdminSellerOrders(this WebApplication app)
    {
        var routes = app.MapGroup("/api/admin/sellers/{sellerId:guid}/orders")
            .RequireAuthorization("Admin").WithTags("Admin reports");
        routes.MapGet("", ReadAsync).Produces<AdminSellerOrderPage>();
        routes.MapGet("/{orderId:guid}", DetailAsync).Produces<AdminSellerOrderDetail>().Produces(404);
    }

    private static async Task<IResult> ReadAsync(Guid sellerId, Guid? kioskId, DateOnly? from, DateOnly? to,
        string? status, int? page, HttpContext http, NpgsqlDataSource data, CancellationToken ct)
    {
        var number = page ?? 1;
        if (number is < 1 or > 100000 || from > to || status?.Length > 40) return Results.BadRequest();
        http.Response.Headers.CacheControl = "no-store";
        await using var command = data.CreateCommand($"""
            WITH selected AS (
                {Orders}
                AND (@kiosk IS NULL OR o.kiosk_id=@kiosk)
                AND (@status IS NULL OR o.status=@status)
                AND (@from IS NULL OR o.created_at >= (@from::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh'))
                AND (@to IS NULL OR o.created_at < ((@to::date + 1)::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh'))
                ORDER BY o.created_at DESC,o.id DESC LIMIT 26 OFFSET @offset
            )
            SELECT jsonb_build_object('items',COALESCE(
                (SELECT jsonb_agg(to_jsonb(r) ORDER BY r."createdAt" DESC,r.id DESC)
                 FROM (SELECT * FROM selected ORDER BY "createdAt" DESC,id DESC LIMIT 25) r),'[]'::jsonb),
                'page',@page,'hasMore',(SELECT count(*)>25 FROM selected))::text
            """);
        command.Parameters.AddWithValue("seller", sellerId);
        command.Parameters.AddWithValue("kiosk", NpgsqlDbType.Uuid, (object?)kioskId ?? DBNull.Value);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("from", NpgsqlDbType.Date, (object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue("to", NpgsqlDbType.Date, (object?)to ?? DBNull.Value);
        command.Parameters.AddWithValue("offset", (number - 1) * 25);
        command.Parameters.AddWithValue("page", number);
        return Results.Content((string)(await command.ExecuteScalarAsync(ct))!, "application/json");
    }

    private static async Task<IResult> DetailAsync(Guid sellerId, Guid orderId, HttpContext http,
        NpgsqlDataSource data, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        await using var command = data.CreateCommand($"""
            SELECT (to_jsonb(r) || jsonb_build_object(
                'items', coalesce((SELECT jsonb_agg(jsonb_build_object('name',i.name_snapshot,'quantity',i.quantity,
                    'unitPrice',i.unit_price,'lineTotal',i.line_total,'status',i.line_status) ORDER BY i.created_at,i.id)
                    FROM ordering.order_items i WHERE i.order_id=r.id),'[]'::jsonb),
                'payments', coalesce((SELECT jsonb_agg(jsonb_build_object('id',a.payment_id,'kind',a.kind,
                    'status',a.payment_status,'amount',a.amount,'paidAt',a.paid_at,'createdAt',a.created_at,
                    'gateway',p.gateway,'gatewayTxnId',p.gateway_txn_id)
                    ORDER BY a.created_at,a.payment_id)
                    FROM payment.v_order_payment_allocations a JOIN payment.payments p ON p.id=a.payment_id
                    WHERE a.order_id=r.id),'[]'::jsonb)
            ))::text FROM ({Orders} AND o.id=@order) r
            """);
        command.Parameters.AddWithValue("seller", sellerId);
        command.Parameters.AddWithValue("order", orderId);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return json is null ? Results.NotFound() : Results.Content(json, "application/json");
    }
}
