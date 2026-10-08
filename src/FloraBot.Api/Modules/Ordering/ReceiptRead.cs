using System.Data;
using FloraBot.Api.Modules.Payment;
using Npgsql;

namespace FloraBot.Api.Modules.Ordering;

public sealed record ReceiptLookupRequest(Guid OrderId, string TrackingToken);
public sealed record ReceiptItem(string Name, int Quantity, long UnitPrice, long Total, string Status);
public sealed record ReceiptResponse(Guid OrderId, string OrderCode, string Status, DateTime CreatedAt,
    long Subtotal, long Discount, long Total, DateTime? CompletedAt, DateTime? DisputeDeadline,
    bool HasComplaint, bool CanComplain, bool RefundInformationProvided, ReceiptRefundSummary Refunds, List<ReceiptItem> Items);

public static class ReceiptRead
{
    public static void MapReceiptRead(this WebApplication app)
    {
        app.MapPost("/api/receipts/lookup", async (ReceiptLookupRequest input, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var token = ReceiptAccess.NormalizeToken(input.TrackingToken);
            if (input.OrderId == Guid.Empty || token is null)
                return Results.NotFound();
            await using var connection = await data.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            // Receipt possession grants access independently of any portal cookie or seller tenant.
            await using var command = new NpgsqlCommand("""
                SELECT o.order_code,o.status,o.created_at,o.subtotal,o.discount_amount,o.total_amount,o.completed_at,
                       o.completed_at+flow.cfg_hour('dispute_window_hours'),
                       EXISTS(SELECT 1 FROM ordering.disputes d WHERE d.order_id=o.id AND d.kind='COMPLAINT'),
                       o.refund_bank_name IS NOT NULL AND o.refund_bank_account_enc IS NOT NULL AND o.refund_bank_holder IS NOT NULL,
                       public.app_now()
                FROM ordering.orders o WHERE o.id=@id AND o.tracking_token=@token
                """, connection, transaction);
            command.Parameters.AddWithValue("id", input.OrderId); command.Parameters.AddWithValue("token", token);
            string code, status; DateTime created, now; DateTime? completed, deadline; long subtotal, discount, total; bool complaint, bankProvided;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return Results.NotFound();
                code = reader.GetString(0); status = reader.GetString(1); created = reader.GetDateTime(2);
                subtotal = reader.GetInt64(3); discount = reader.GetInt64(4); total = reader.GetInt64(5);
                completed = reader.IsDBNull(6) ? null : reader.GetDateTime(6); deadline = reader.IsDBNull(7) ? null : reader.GetDateTime(7);
                complaint = reader.GetBoolean(8); bankProvided = reader.GetBoolean(9); now = reader.GetDateTime(10);
            }
            var items = new List<ReceiptItem>();
            await using var lines = new NpgsqlCommand("SELECT name_snapshot,quantity,unit_price,line_total,line_status FROM ordering.order_items WHERE order_id=@id ORDER BY created_at,id", connection, transaction);
            lines.Parameters.AddWithValue("id", input.OrderId);
            await using (var reader = await lines.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) items.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4)));
            var refunds = await ReceiptRefunds.ReadAsync(connection, transaction, input.OrderId, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new ReceiptResponse(input.OrderId, code, status, created, subtotal, discount, total, completed, deadline,
                complaint, status == "COMPLETED" && !complaint && deadline.HasValue && now <= deadline.Value,
                bankProvided, refunds, items));
        }).AllowAnonymous().RequireRateLimiting("receipt").Produces<ReceiptResponse>();
    }
}
