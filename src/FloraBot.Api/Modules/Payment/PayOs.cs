using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using StackExchange.Redis;
using DotNetCore.CAP;

namespace FloraBot.Api.Modules.Payment;

public sealed record PayOsWebhook(JsonElement Data, string Signature, string? Code = null, string? Desc = null, bool? Success = null);
public static class PayOsChecksum
{
    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Sign(JsonElement data, string key) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key),
        Encoding.UTF8.GetBytes(string.Join("&", data.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name + "=" + Value(x.Value)))))).ToLowerInvariant();
    private static string Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => value.GetString() is "null" or "undefined" ? "" : value.GetString()!,
        JsonValueKind.Array => JsonSerializer.Serialize(SortJson(value), CompactJson),
        _ => value.GetRawText()
    };
    private static object? SortJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => SortJson(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(SortJson).ToArray(),
        _ => value
    };
    public static bool Verify(JsonElement data, string signature, string key)
    {
        if (data.ValueKind != JsonValueKind.Object || signature?.Length != 64 || string.IsNullOrEmpty(key)) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Sign(data, key)), Convert.FromHexString(signature)); }
        catch (FormatException) { return false; }
    }
}

public static class PayOsEndpoints
{
    public static void MapPayOs(this WebApplication app)
    {
        app.MapPost("/api/payments/webhook", async (PayOsWebhook body, IConfiguration config, NpgsqlDataSource source, ICapPublisher publisher, IConnectionMultiplexer redis, ILogger<PayOsWebhook> logger, CancellationToken ct) =>
        {
            if (!PayOsChecksum.Verify(body.Data, body.Signature, config["PAYOS_CHECKSUM_KEY"] ?? ""))
            {
                await using var audit = source.CreateCommand("SELECT flow.audit(NULL,'WEBHOOK_INVALID_CHECKSUM','payments',NULL)");
                await audit.ExecuteNonQueryAsync(ct);
                return Results.BadRequest(new { message = "Checksum không hợp lệ." });
            }
            if (!body.Data.TryGetProperty("orderCode", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var orderCode) ||
                !body.Data.TryGetProperty("amount", out var amountValue) || amountValue.ValueKind != JsonValueKind.Number || !amountValue.TryGetInt64(out var amount) ||
                !body.Data.TryGetProperty("reference", out var referenceValue) || referenceValue.ValueKind != JsonValueKind.String ||
                !body.Data.TryGetProperty("code", out var statusValue) || statusValue.ValueKind != JsonValueKind.String) return Results.BadRequest();
            var reference = referenceValue.GetString();
            if (string.IsNullOrWhiteSpace(reference) || reference.Length > 200) return Results.BadRequest();
            await using var connection = await source.OpenConnectionAsync(ct);
            using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
            var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
            // Serialize duplicate callbacks before reading transition state, without changing flow row-lock order.
            await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('payos:' || @code::text,0))", connection, transaction);
            gate.Parameters.AddWithValue("code", orderCode);
            await gate.ExecuteNonQueryAsync(ct);
            // The flow acquires business locks in its own order; do not invert them here.
            await using var find = new NpgsqlCommand("SELECT p.id,p.purpose,p.checkout_id,p.subscription_id,p.amount,p.status FROM payment.payments p JOIN payment.gateway_orders g ON g.payment_id=p.id WHERE p.kind='CHARGE' AND p.gateway='PAYOS' AND g.order_code=@code", connection, transaction);
            find.Parameters.AddWithValue("code", orderCode);
            Guid paymentId; string purpose; Guid target; long expected; bool alreadySucceeded;
            await using (var reader = await find.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return Results.BadRequest(new { message = "Không tìm thấy khoản thanh toán." });
                paymentId = reader.GetGuid(0); purpose = reader.GetString(1); target = reader.GetGuid(purpose == "SUBSCRIPTION" ? 3 : 2); expected = reader.GetInt64(4);
                alreadySucceeded = reader.GetString(5) == "SUCCEEDED";
                if (await reader.ReadAsync(ct)) throw new InvalidOperationException("Duplicate gateway order code.");
            }
            if (amount != expected) return Results.BadRequest(new { message = "Số tiền thanh toán không khớp." });
            var success = statusValue.GetString() == "00";
            if (purpose == "SUBSCRIPTION" && !success) return Results.BadRequest();
            await using var command = new NpgsqlCommand(purpose == "SUBSCRIPTION" ? "SELECT flow.subscription_paid(@id,@txn)" : "SELECT flow.checkout_paid(@id,@txn,@success,@amount)", connection, transaction);
            command.Parameters.AddWithValue("id", target);
            command.Parameters.AddWithValue("txn", reference);
            if (purpose != "SUBSCRIPTION") { command.Parameters.AddWithValue("success", success); command.Parameters.AddWithValue("amount", amount); }
            await command.ExecuteNonQueryAsync(ct);
            if (!alreadySucceeded) await PaymentEvents.PublishSucceededAsync(connection, transaction, publisher, paymentId, ct);
            await outbox.CommitAsync(ct);
            await connection.CloseAsync();
            // This is only an optimization marker. Durable idempotency remains in the SQL flow.
            try
            {
                await redis.GetDatabase().StringSetAsync($"webhook:{paymentId}:{reference}", "committed", TimeSpan.FromDays(1));
            }
            catch (RedisException)
            {
                logger.LogWarning("Payment {PaymentId} committed; optional webhook cache marker unavailable", paymentId);
            }
            return Results.Ok(new { success = true });
        }).AllowAnonymous().WithTags("Payment");
    }
}
