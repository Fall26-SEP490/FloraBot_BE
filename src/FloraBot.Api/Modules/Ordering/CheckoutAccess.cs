using Npgsql;

namespace FloraBot.Api.Modules.Ordering;

public sealed record CheckoutOrderSnapshot(Guid Id, string OrderCode, string Status, long Amount, string TrackingToken, long PointsRedeemed, long DiscountAmount);
public sealed record CheckoutSnapshot(DateTime PayBefore, List<CheckoutOrderSnapshot> Orders)
{
    public bool AwaitingPayment => Orders.All(x => x.Status == "AWAITING_PAYMENT");
}

public static class CheckoutAccess
{
    public static async Task<CheckoutSnapshot?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid checkout, Guid kiosk, Guid? customer, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id,order_code,status,total_amount,tracking_token,points_redeemed,discount_amount,
                   kiosk_id,customer_id,created_at + flow.cfg_min('hold_minutes')
            FROM ordering.orders WHERE checkout_id=@id ORDER BY id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", checkout);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<CheckoutOrderSnapshot>();
        var deadline = DateTime.MaxValue;
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetGuid(7) != kiosk || customer is not null && (reader.IsDBNull(8) || reader.GetGuid(8) != customer)) return null;
            rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6)));
            var expires = reader.GetDateTime(9);
            if (expires < deadline) deadline = expires;
        }
        return rows.Count == 0 ? null : new(deadline, rows);
    }
}
