using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloraBot.Api.Modules.Payment;

public sealed record PaymentLinkResponse(long OrderCode, long Amount, string CheckoutUrl, string? QrCode = null,
    string? RecipientName = null, string? RecipientAccountMasked = null);
public sealed record PayOsConfirmWebhookResponse(string WebhookUrl, string AccountNumber, string AccountName, string? Name = null, string? ShortName = null);
public sealed class PaymentGatewayException(string failureCode) : Exception
{
    // A short, non-sensitive diagnostic label for server logs. Never include credentials,
    // signatures, callback values, or raw provider response bodies here.
    public string FailureCode { get; } = failureCode;
}

public sealed class PayOsClient
{
    private readonly HttpClient client;
    private readonly PayOsChannelOptions options;

    public PayOsClient(HttpClient client, IConfiguration config)
        : this(client, new PayOsChannelOptions(
            config["PAYOS_CLIENT_ID"] ?? "",
            config["PAYOS_API_KEY"] ?? "",
            config["PAYOS_CHECKSUM_KEY"] ?? "",
            config["PAYOS_RETURN_URL"] ?? "",
            config["PAYOS_CANCEL_URL"] ?? "",
            config["PAYOS_EXPECTED_BIN"],
            config["PAYOS_EXPECTED_ACCOUNT"],
            config["PAYOS_EXPECTED_NAME"],
            string.Equals(config["PAYOS_ALLOW_DEV_LOOPBACK"], "true", StringComparison.OrdinalIgnoreCase)
        ))
    {
    }

    internal PayOsClient(HttpClient client, PayOsChannelOptions options)
    {
        this.client = client;
        this.options = options;
    }

    public static PayOsClient ForChannel(HttpClient client, PayOsChannelOptions options) => new(client, options);

    public string? ExpectedBin => options.ExpectedBin;
    public string? ExpectedAccount => options.ExpectedAccount;
    public string? ExpectedName => options.ExpectedName;

    private bool HasExpectedReceiver =>
        !string.IsNullOrWhiteSpace(options.ExpectedBin) || !string.IsNullOrWhiteSpace(options.ExpectedAccount);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.ClientId) &&
        !string.IsNullOrWhiteSpace(options.ApiKey) &&
        !string.IsNullOrWhiteSpace(options.ChecksumKey) &&
        !string.IsNullOrWhiteSpace(options.ReturnUrl) &&
        !string.IsNullOrWhiteSpace(options.CancelUrl);

    public async Task<PaymentLinkResponse> CreateAsync(long orderCode, long amount, CancellationToken ct, long? expiredAt = null)
    {
        if (!IsConfigured) throw new PaymentGatewayException("configuration_missing");
        if (amount <= 0 || orderCode is <= 0 or > 9007199254740991) throw new PaymentGatewayException("request_values_invalid");
        var returnUrl = Callback(options.ReturnUrl);
        var cancelUrl = Callback(options.CancelUrl);
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
            ["signature"] = PayOsChecksum.Sign(signed, options.ChecksumKey)
        };
        if (expiredAt is not null) payload["expiredAt"] = expiredAt.Value;
        request.Content = JsonContent.Create(payload);
        var envelope = await SendAsync(request, ct);
        if (envelope.GetProperty("code").GetString() != "00")
        {
            // A previous attempt may have reached payOS before its response was lost.
            // GET lookup may lack recipient, so it cannot serve as new link recovery when receiver is unproven.
            // Only legacy platform path without expected receiver does GET lookup fallback.
            if (!HasExpectedReceiver)
            {
                using var lookup = Request(HttpMethod.Get, "v2/payment-requests/" + orderCode.ToString(CultureInfo.InvariantCulture));
                envelope = await SendAsync(lookup, ct);
            }
        }
        var data = VerifiedData(envelope);
        if (data.GetProperty("orderCode").GetInt64() != orderCode || data.GetProperty("amount").GetInt64() != amount ||
            data.GetProperty("status").GetString() != "PENDING") throw new PaymentGatewayException("provider_data_mismatch");

        // When expected receiver fields are configured, signed response MUST contain and match expected bin/account
        if (HasExpectedReceiver)
        {
            if (!string.IsNullOrWhiteSpace(options.ExpectedBin))
            {
                if (!data.TryGetProperty("bin", out var binProp) || binProp.ValueKind != JsonValueKind.String ||
                    !string.Equals(binProp.GetString(), options.ExpectedBin, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PaymentGatewayException("provider_receiver_bin_mismatch");
                }
            }
            if (!string.IsNullOrWhiteSpace(options.ExpectedAccount))
            {
                if (!data.TryGetProperty("accountNumber", out var accProp) || accProp.ValueKind != JsonValueKind.String ||
                    !string.Equals(accProp.GetString(), options.ExpectedAccount, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PaymentGatewayException("provider_receiver_account_mismatch");
                }
            }
        }

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
        // This is the signed VietQR payload, not a URL or an image. A lookup of an
        // existing order may omit it; the payment-link adapter restores our saved copy.
        string? qrCode = null;
        if (data.TryGetProperty("qrCode", out var qr) && qr.ValueKind != JsonValueKind.Null)
        {
            if (qr.ValueKind != JsonValueKind.String) throw new PaymentGatewayException("provider_qr_code_invalid");
            qrCode = qr.GetString();
            if (string.IsNullOrWhiteSpace(qrCode) || qrCode.Length > 4096 ||
                !qrCode.StartsWith("000201", StringComparison.Ordinal) || qrCode.Any(char.IsControl))
                throw new PaymentGatewayException("provider_qr_code_invalid");
        }
        return new(orderCode, amount, uri.AbsoluteUri, qrCode);
    }

    public async Task<JsonElement> GetPaymentRequestAsync(long orderCode, CancellationToken ct)
    {
        if (!IsConfigured) throw new PaymentGatewayException("configuration_missing");
        if (orderCode is <= 0 or > 9007199254740991) throw new PaymentGatewayException("request_values_invalid");
        using var lookup = Request(HttpMethod.Get, "v2/payment-requests/" + orderCode.ToString(CultureInfo.InvariantCulture));
        var envelope = await SendAsync(lookup, ct);
        return VerifiedData(envelope);
    }

    public async Task<PayOsConfirmWebhookResponse> ConfirmWebhookAsync(string webhookUrl, CancellationToken ct)
    {
        if (!IsConfigured) throw new PaymentGatewayException("configuration_missing");
        if (string.IsNullOrWhiteSpace(webhookUrl) || !Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !options.AllowDevLoopback))
            throw new PaymentGatewayException("webhook_url_invalid");

        using var request = Request(HttpMethod.Post, "confirm-webhook");
        request.Content = JsonContent.Create(new { webhookUrl });
        var envelope = await SendAsync(request, ct);

        if (envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("code", out var code) || code.GetString() != "00")
        {
            var safeCode = envelope.TryGetProperty("code", out var c) && Regex.IsMatch(c.GetString() ?? "", "^[0-9]{1,4}$") ? c.GetString()! : "other";
            throw new PaymentGatewayException("provider_code_" + safeCode);
        }

        if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new PaymentGatewayException("provider_data_missing");

        var returnedUrl = data.TryGetProperty("webhookUrl", out var u) ? u.GetString() ?? "" : "";
        var accountNumber = data.TryGetProperty("accountNumber", out var a) ? a.GetString() ?? "" : "";
        var accountName = data.TryGetProperty("accountName", out var n) ? n.GetString() ?? "" : "";
        var name = data.TryGetProperty("name", out var nm) ? nm.GetString() : null;
        var shortName = data.TryGetProperty("shortName", out var sn) ? sn.GetString() : null;

        if (string.IsNullOrWhiteSpace(accountNumber))
            throw new PaymentGatewayException("provider_account_missing");

        return new PayOsConfirmWebhookResponse(returnedUrl, accountNumber, accountName, name, shortName);
    }

    private string Callback(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new PaymentGatewayException("callback_url_invalid");
        if (uri.Scheme == "https")
            return url;
        if (options.AllowDevLoopback && uri.Scheme == "http" && IsLoopbackHost(uri.Host))
            return url;
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
        request.Headers.Add("x-client-id", options.ClientId);
        request.Headers.Add("x-api-key", options.ApiKey);
        return request;
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            var diagnostic = await ReadSafeProviderDiagnosticAsync(response, ct);
            throw new PaymentGatewayException($"provider_http_{status}{diagnostic}");
        }
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static async Task<string> ReadSafeProviderDiagnosticAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return "";

            var code = ReadString(document.RootElement, "code");
            var description = ReadString(document.RootElement, "desc") ?? ReadString(document.RootElement, "message");
            var safeCode = SafeDiagnosticPart(code, 12);
            var safeDescription = SafeDiagnosticPart(description, 48);

            if (safeCode.Length == 0 && safeDescription.Length == 0)
                return "";

            var parts = new List<string>(2);
            if (safeCode.Length > 0) parts.Add("code_" + safeCode);
            if (safeDescription.Length > 0) parts.Add("desc_" + safeDescription);
            return "_" + string.Join("_", parts);
        }
        catch
        {
            // Provider diagnostics are best-effort; never replace the original HTTP failure.
            return "";
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SafeDiagnosticPart(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var safe = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        return safe.Length <= maxLength ? safe : safe[..maxLength].TrimEnd('_');
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
        if (!PayOsChecksum.Verify(data, signature.GetString()!, options.ChecksumKey))
            throw new PaymentGatewayException("provider_signature_invalid");
        return data;
    }
}
