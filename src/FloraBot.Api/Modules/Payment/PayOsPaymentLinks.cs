using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Payment;

public static class PayOsPaymentLinks
{
    public static async Task<PaymentLinkResponse> CreateAsync(NpgsqlDataSource source, PayOsClient gateway,
        Guid paymentId, long orderCode, long amount, CancellationToken ct, long? expiredAt = null)
    {
        var link = await gateway.CreateAsync(orderCode, amount, ct, expiredAt);

        // GET /payment-requests/{id} does not include qrCode. Retain the verified
        // creation payload across repeated requests and application restarts.
        await using var save = source.CreateCommand("""
            UPDATE payment.gateway_orders
            SET qr_code=COALESCE(@qr,qr_code)
            WHERE payment_id=@id AND order_code=@code
            RETURNING qr_code
            """);
        save.Parameters.AddWithValue("qr", NpgsqlDbType.Text, (object?)link.QrCode ?? DBNull.Value);
        save.Parameters.AddWithValue("id", paymentId);
        save.Parameters.AddWithValue("code", orderCode);
        var stored = await save.ExecuteScalarAsync(ct);
        if (stored is null) throw new InvalidOperationException("Reserved payOS order was not found.");
        return link with { QrCode = stored is DBNull ? null : (string)stored };
    }
}
