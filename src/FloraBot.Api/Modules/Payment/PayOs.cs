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
    private static bool IsWebhookConfirmationSample(PayOsWebhook body, string checksumKey)
    {
        if (body.Code != "00" || body.Desc != "success") return false;

        var data = body.Data;
        if (data.ValueKind != JsonValueKind.Object) return false;

        return Number(data, "orderCode", 123)
            && Number(data, "amount", 3000)
            && Text(data, "description", "VQRIO123")
            && PayOsChecksum.Verify(data, body.Signature, checksumKey);

        static bool Number(JsonElement value, string name, long expected) =>
            value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var actual)
            && actual == expected;

        static bool Text(JsonElement value, string name, string expected) =>
            value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() == expected;

    }

    public static void MapPayOs(this WebApplication app)
    {
        app.MapPost("/api/payments/webhook", async (PayOsWebhook body, IConfiguration config, HttpContext http, ILogger<PayOsWebhook> logger, CancellationToken ct) =>
        {
            // payOS posts this documented, fixed sample when a merchant confirms a webhook URL.
            // Acknowledge that exact probe without touching payment state; real callbacks still
            // require the configured HMAC checksum below. Resolve payment infrastructure only
            // after this probe check so unavailable database/cache services cannot block it.
            if (IsWebhookConfirmationSample(body, config["PAYOS_CHECKSUM_KEY"] ?? ""))
            {
                logger.LogInformation("Acknowledged payOS webhook URL verification sample without changing payment state");
                return Results.Ok(new { success = true });
            }

            if (body.Data.ValueKind == JsonValueKind.Object
                && body.Data.TryGetProperty("orderCode", out var sampleOrderCode)
                && sampleOrderCode.ValueKind == JsonValueKind.Number
                && sampleOrderCode.TryGetInt64(out var sampleOrderCodeValue)
                && sampleOrderCodeValue == 123)
            {
                var amountMatches = body.Data.TryGetProperty("amount", out var sampleAmount)
                    && sampleAmount.ValueKind == JsonValueKind.Number
                    && sampleAmount.TryGetInt64(out var sampleAmountValue)
                    && sampleAmountValue == 3000;
                var descriptionMatches = body.Data.TryGetProperty("description", out var sampleDescription)
                    && sampleDescription.ValueKind == JsonValueKind.String
                    && sampleDescription.GetString() == "VQRIO123";

                logger.LogWarning(
                    "payOS orderCode 123 did not match URL verification sample: envelopeCode00={EnvelopeCodeMatches}, envelopeDescSuccess={EnvelopeDescMatches}, envelopeSuccessTrue={EnvelopeSuccessMatches}, amount3000={AmountMatches}, descriptionVQRIO123={DescriptionMatches}",
                    body.Code == "00", body.Desc == "success", body.Success is true, amountMatches, descriptionMatches);
            }

            var services = http.RequestServices;
            var source = services.GetRequiredService<NpgsqlDataSource>();
            var publisher = services.GetRequiredService<ICapPublisher>();
            var redis = services.GetRequiredService<IConnectionMultiplexer>();

            if (!PayOsChecksum.Verify(body.Data, body.Signature, config["PAYOS_CHECKSUM_KEY"] ?? ""))
            {
                logger.LogWarning("Rejected payOS webhook: checksum verification failed");
                await using var audit = source.CreateCommand("SELECT flow.audit(NULL,'WEBHOOK_INVALID_CHECKSUM','payments',NULL)");
                await audit.ExecuteNonQueryAsync(ct);
                return Results.BadRequest(new { message = "Checksum không hợp lệ." });
            }
            if (!body.Data.TryGetProperty("orderCode", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var orderCode) ||
                !body.Data.TryGetProperty("amount", out var amountValue) || amountValue.ValueKind != JsonValueKind.Number || !amountValue.TryGetInt64(out var amount) ||
                !body.Data.TryGetProperty("reference", out var referenceValue) || referenceValue.ValueKind != JsonValueKind.String ||
                !body.Data.TryGetProperty("code", out var statusValue) || statusValue.ValueKind != JsonValueKind.String)
            {
                logger.LogWarning("Rejected payOS webhook: required data fields are missing or have invalid types");
                return Results.BadRequest();
            }
            var reference = referenceValue.GetString();
            if (string.IsNullOrWhiteSpace(reference) || reference.Length > 200)
            {
                logger.LogWarning("Rejected payOS webhook: transaction reference is empty or too long");
                return Results.BadRequest();
            }
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
                if (!await reader.ReadAsync(ct))
                {
                    logger.LogWarning("Rejected payOS webhook: no payment maps to order code {OrderCode}", orderCode);
                    return Results.BadRequest(new { message = "Không tìm thấy khoản thanh toán." });
                }
                paymentId = reader.GetGuid(0);
                purpose = reader.GetString(1);
                var targetOrdinal = purpose == "SUBSCRIPTION" ? 3 : 2;
                target = !reader.IsDBNull(targetOrdinal) ? reader.GetGuid(targetOrdinal) : Guid.Empty;
                expected = reader.GetInt64(4);
                alreadySucceeded = reader.GetString(5) == "SUCCEEDED";
                if (await reader.ReadAsync(ct)) throw new InvalidOperationException("Duplicate gateway order code.");
            }
            if (amount != expected)
            {
                logger.LogWarning("Rejected payOS webhook for order code {OrderCode}: amount does not match the payment", orderCode);
                return Results.BadRequest(new { message = "Số tiền thanh toán không khớp." });
            }
            var success = statusValue.GetString() == "00";
            if ((purpose == "SUBSCRIPTION" || purpose == "KIOSK_APPLICATION") && !success)
            {
                logger.LogWarning("Rejected unsuccessful payOS webhook for payment purpose {Purpose}", purpose);
                return Results.BadRequest();
            }
            await using var command = new NpgsqlCommand(
                purpose == "SUBSCRIPTION" ? "SELECT flow.subscription_paid(@id,@txn)" :
                purpose == "KIOSK_APPLICATION" ? "SELECT flow.kiosk_application_paid(@id,@txn)" :
                "SELECT flow.checkout_paid(@id,@txn,@success,@amount)", connection, transaction);
            command.Parameters.AddWithValue("id", purpose == "KIOSK_APPLICATION" ? paymentId : target);
            command.Parameters.AddWithValue("txn", reference);
            if (purpose != "SUBSCRIPTION" && purpose != "KIOSK_APPLICATION") { command.Parameters.AddWithValue("success", success); command.Parameters.AddWithValue("amount", amount); }
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
