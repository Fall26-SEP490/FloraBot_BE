using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNetCore.CAP;
using Npgsql;
using StackExchange.Redis;

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
    public static bool IsWebhookConfirmationSample(PayOsWebhook body, string checksumKey)
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

    public static void MapPayOs(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/payments/webhook", async (PayOsWebhook body, IConfiguration config, IPayOsClientResolver resolver, HttpContext http, ILogger<PayOsWebhook> logger, CancellationToken ct) =>
        {
            // payOS posts this documented sample when confirming a webhook URL for platform
            if (IsWebhookConfirmationSample(body, config["PAYOS_CHECKSUM_KEY"] ?? ""))
            {
                logger.LogInformation("Acknowledged payOS webhook URL verification sample for platform");
                return Results.Ok(new { success = true });
            }

            var services = http.RequestServices;
            var source = services.GetRequiredService<NpgsqlDataSource>();
            var publisher = services.GetRequiredService<ICapPublisher>();
            var redis = services.GetRequiredService<IConnectionMultiplexer>();

            // Delegate to shared payment webhook processor (NO try-all-keys scan!)
            return await PayOsWebhookProcessor.ProcessPaymentWebhookAsync(
                body, null, source, resolver, config, publisher, redis, logger, ct);
        }).AllowAnonymous().WithTags("Payment");
    }
}
