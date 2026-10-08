using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloraBot.Api.Modules.Payment;

public sealed record PaymentLinkResponse(long OrderCode, long Amount, string CheckoutUrl);
public sealed class PaymentGatewayException : Exception { }

public sealed class PayOsClient(HttpClient client, IConfiguration config)
{
    public bool IsConfigured => new[] { "PAYOS_CLIENT_ID", "PAYOS_API_KEY", "PAYOS_CHECKSUM_KEY", "PAYOS_RETURN_URL", "PAYOS_CANCEL_URL" }
        .All(key => !string.IsNullOrWhiteSpace(config[key]));

    public async Task<PaymentLinkResponse> CreateAsync(long orderCode, long amount, CancellationToken ct, long? expiredAt = null)
    {
        if (!IsConfigured || amount <= 0 || orderCode is <= 0 or > 9007199254740991) throw new PaymentGatewayException();
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
            data.GetProperty("status").GetString() != "PENDING") throw new PaymentGatewayException();
        string url;
        if (data.TryGetProperty("checkoutUrl", out var checkout)) url = checkout.GetString()!;
        else
        {
            var id = data.GetProperty("id").GetString() ?? "";
            if (!Regex.IsMatch(id, "^[a-fA-F0-9]{32}$")) throw new PaymentGatewayException();
            url = "https://pay.payos.vn/web/" + id;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "pay.payos.vn" ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) throw new PaymentGatewayException();
        return new(orderCode, amount, uri.AbsoluteUri);
    }

    private string Callback(string key)
    {
        var value = config[key]!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new PaymentGatewayException();
        return value;
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
        if (!response.IsSuccessStatusCode) throw new PaymentGatewayException();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private JsonElement VerifiedData(JsonElement envelope)
    {
        if (envelope.GetProperty("code").GetString() != "00" || !envelope.TryGetProperty("data", out var data) ||
            !envelope.TryGetProperty("signature", out var signature) ||
            !PayOsChecksum.Verify(data, signature.GetString()!, config["PAYOS_CHECKSUM_KEY"]!)) throw new PaymentGatewayException();
        return data;
    }
}
