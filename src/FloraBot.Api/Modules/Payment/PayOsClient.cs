using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloraBot.Api.Modules.Payment;

public sealed record PaymentLinkResponse(long OrderCode, long Amount, string CheckoutUrl);
public sealed class PaymentGatewayException(string failureCode) : Exception
{
    // A short, non-sensitive diagnostic label for server logs. Never include credentials,
    // signatures, callback values, or raw provider response bodies here.
    public string FailureCode { get; } = failureCode;
}

public sealed class PayOsClient(HttpClient client, IConfiguration config)
{
    public bool IsConfigured => new[] { "PAYOS_CLIENT_ID", "PAYOS_API_KEY", "PAYOS_CHECKSUM_KEY", "PAYOS_RETURN_URL", "PAYOS_CANCEL_URL" }
        .All(key => !string.IsNullOrWhiteSpace(config[key]));

    public async Task<PaymentLinkResponse> CreateAsync(long orderCode, long amount, CancellationToken ct, long? expiredAt = null)
    {
        if (!IsConfigured) throw new PaymentGatewayException("configuration_missing");
        if (amount <= 0 || orderCode is <= 0 or > 9007199254740991) throw new PaymentGatewayException("request_values_invalid");
        var returnUrl = Callback("PAYOS_RETURN_URL");
        var cancelUrl = Callback("PAYOS_CANCEL_URL");
        var description = "FloraBot " + orderCode.ToString(CultureInfo.InvariantCulture);
        var signed = JsonSerializer.SerializeToElement(new { amount, cancelUrl, description, orderCode, returnUrl });
        using var request = Request(HttpMethod.Post, "v2/payment-requests");
        var payload = new Dictionary<string, object>
        {
            ["orderCode"] = orderCode,
            ["amount"] = amount,
            ["description"] = description,
            ["cancelUrl"] = cancelUrl,
            ["returnUrl"] = returnUrl,
            ["signature"] = PayOsChecksum.Sign(signed, config["PAYOS_CHECKSUM_KEY"]!)
        };
        if (expiredAt is not null) payload["expiredAt"] = expiredAt.Value;
        request.Content = JsonContent.Create(payload);
        var envelope = await SendAsync(request, ct);
        if (envelope.GetProperty("code").GetString() != "00")
        {
            // A previous attempt may have reached payOS before its response was lost.
            using var lookup = Request(HttpMethod.Get, "v2/payment-requests/" + orderCode.ToString(CultureInfo.InvariantCulture));
            envelope = await SendAsync(lookup, ct);
        }
        var data = VerifiedData(envelope);
        if (data.GetProperty("orderCode").GetInt64() != orderCode || data.GetProperty("amount").GetInt64() != amount ||
            data.GetProperty("status").GetString() != "PENDING") throw new PaymentGatewayException("provider_data_mismatch");
        string url;
        if (data.TryGetProperty("checkoutUrl", out var checkout)) url = checkout.GetString()!;
        else
        {
            var id = data.GetProperty("id").GetString() ?? "";
            if (!Regex.IsMatch(id, "^[a-fA-F0-9]{32}$")) throw new PaymentGatewayException("provider_checkout_id_invalid");
            url = "https://pay.payos.vn/web/" + id;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "pay.payos.vn" ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) throw new PaymentGatewayException("provider_checkout_url_invalid");
        return new(orderCode, amount, uri.AbsoluteUri);
    }

    public async Task<JsonElement> GetPaymentRequestAsync(long orderCode, CancellationToken ct)
    {
        if (!IsConfigured) throw new PaymentGatewayException("configuration_missing");
        if (orderCode is <= 0 or > 9007199254740991) throw new PaymentGatewayException("request_values_invalid");
        using var lookup = Request(HttpMethod.Get, "v2/payment-requests/" + orderCode.ToString(CultureInfo.InvariantCulture));
        var envelope = await SendAsync(lookup, ct);
        return VerifiedData(envelope);
    }

    private string Callback(string key)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new PaymentGatewayException("callback_url_invalid");
        var allowDevLoopback = string.Equals(config["PAYOS_ALLOW_DEV_LOOPBACK"], "true", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme == "https")
            return value;
        if (allowDevLoopback && uri.Scheme == "http" && IsLoopbackHost(uri.Host))
            return value;
        throw new PaymentGatewayException("callback_url_requires_https");
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip)) return true;
        return false;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, "https://api-merchant.payos.vn/" + path);
        request.Headers.Add("x-client-id", config["PAYOS_CLIENT_ID"]);
        request.Headers.Add("x-api-key", config["PAYOS_API_KEY"]);
        return request;
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new PaymentGatewayException("provider_http_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private JsonElement VerifiedData(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object) throw new PaymentGatewayException("provider_envelope_invalid");
        if (!envelope.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
            throw new PaymentGatewayException("provider_code_missing");
        var providerCode = code.GetString()!;
        if (providerCode != "00")
        {
            var safeCode = Regex.IsMatch(providerCode, "^[0-9]{1,4}$") ? providerCode : "other";
            throw new PaymentGatewayException("provider_code_" + safeCode);
        }
        if (!envelope.TryGetProperty("data", out var data)) throw new PaymentGatewayException("provider_data_missing");
        if (!envelope.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.String)
            throw new PaymentGatewayException("provider_signature_missing");
        if (!PayOsChecksum.Verify(data, signature.GetString()!, config["PAYOS_CHECKSUM_KEY"]!))
            throw new PaymentGatewayException("provider_signature_invalid");
        return data;
    }
}
