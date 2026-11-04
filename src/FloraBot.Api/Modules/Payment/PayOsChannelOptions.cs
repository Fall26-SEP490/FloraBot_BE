using System.Text.RegularExpressions;

namespace FloraBot.Api.Modules.Payment;

public sealed class PayOsChannelOptions
{
    public string ClientId { get; }
    public string ApiKey { get; }
    public string ChecksumKey { get; }
    public string ReturnUrl { get; }
    public string CancelUrl { get; }
    public string? ExpectedBin { get; }
    public string? ExpectedAccount { get; }
    public string? ExpectedName { get; }
    public bool AllowDevLoopback { get; }

    public PayOsChannelOptions(
        string clientId,
        string apiKey,
        string checksumKey,
        string returnUrl,
        string cancelUrl,
        string? expectedBin = null,
        string? expectedAccount = null,
        string? expectedName = null,
        bool allowDevLoopback = false)
    {
        ClientId = clientId;
        ApiKey = apiKey;
        ChecksumKey = checksumKey;
        ReturnUrl = returnUrl;
        CancelUrl = cancelUrl;
        ExpectedBin = expectedBin;
        ExpectedAccount = expectedAccount;
        ExpectedName = expectedName;
        AllowDevLoopback = allowDevLoopback;
    }

    public override string ToString() =>
        $"PayOsChannelOptions(ClientId={ClientId}, ExpectedBin={ExpectedBin}, ExpectedAccount=***, ReturnUrl={ReturnUrl})";
}

public sealed class PayOsChannelConfig
{
    public Guid SellerId { get; }
    public string SecretRef { get; }
    public PayOsChannelOptions Options { get; }

    public PayOsChannelConfig(Guid sellerId, string secretRef, PayOsChannelOptions options)
    {
        SellerId = sellerId;
        SecretRef = secretRef;
        Options = options;
    }

    public override string ToString() =>
        $"PayOsChannelConfig(SellerId={SellerId}, SecretRef={SecretRef})";
}

public interface IPayOsClientResolver
{
    PayOsClient ResolvePlatformClient();
    PayOsClient ResolveClient(PayOsChannelOptions options);
    PayOsChannelConfig ResolveChannelConfig(string secretRef);
    bool TryResolveChannelConfig(string secretRef, out PayOsChannelConfig? config);
}

public sealed class PayOsClientResolver : IPayOsClientResolver
{
    private static readonly Regex SecretRefPattern = new("^[A-Za-z0-9_-]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex BinPattern = new("^[0-9]{4,10}$", RegexOptions.Compiled);
    private static readonly Regex AccountPattern = new("^[0-9A-Za-z]{4,30}$", RegexOptions.Compiled);

    private readonly Func<HttpClient> clientProvider;
    private readonly IConfiguration configuration;

    public PayOsClientResolver(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        this.clientProvider = () => httpClientFactory.CreateClient("payos");
        this.configuration = configuration;
    }

    public static PayOsClientResolver ForTest(HttpClient client, IConfiguration configuration) =>
        new(new SingleHttpClientFactory(client), configuration);

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    public PayOsClient ResolvePlatformClient()
    {
        var client = clientProvider();
        return new PayOsClient(client, configuration);
    }

    public PayOsClient ResolveClient(PayOsChannelOptions options)
    {
        var client = clientProvider();
        return PayOsClient.ForChannel(client, options);
    }

    public bool TryResolveChannelConfig(string secretRef, out PayOsChannelConfig? config)
    {
        config = null;
        if (string.IsNullOrWhiteSpace(secretRef) || !SecretRefPattern.IsMatch(secretRef))
            return false;

        var prefix = $"PAYOS_CHANNELS:{secretRef}:";
        var sellerIdStr = configuration[prefix + "SellerId"];
        if (string.IsNullOrWhiteSpace(sellerIdStr) || !Guid.TryParse(sellerIdStr, out var sellerId))
            return false;

        var clientId = configuration[prefix + "ClientId"];
        var apiKey = configuration[prefix + "ApiKey"];
        var checksumKey = configuration[prefix + "ChecksumKey"];
        var returnUrl = configuration[prefix + "ReturnUrl"] ?? configuration["PAYOS_RETURN_URL"] ?? "";
        var cancelUrl = configuration[prefix + "CancelUrl"] ?? configuration["PAYOS_CANCEL_URL"] ?? "";
        var expectedBin = configuration[prefix + "ExpectedBin"];
        var expectedAccount = configuration[prefix + "ExpectedAccount"];
        var expectedName = configuration[prefix + "ExpectedName"];
        var allowDevLoopback = string.Equals(
            configuration[prefix + "AllowDevLoopback"] ?? configuration["PAYOS_ALLOW_DEV_LOOPBACK"],
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(checksumKey))
            return false;

        // If recipient bank details are present, validate their format
        if (expectedBin is not null && !BinPattern.IsMatch(expectedBin))
            return false;
        if (expectedAccount is not null && !AccountPattern.IsMatch(expectedAccount))
            return false;
        if (expectedName is not null && (expectedName.Length < 2 || expectedName.Length > 120))
            return false;

        var options = new PayOsChannelOptions(
            clientId,
            apiKey,
            checksumKey,
            returnUrl,
            cancelUrl,
            expectedBin,
            expectedAccount,
            expectedName,
            allowDevLoopback
        );

        config = new PayOsChannelConfig(sellerId, secretRef, options);
        return true;
    }

    public PayOsChannelConfig ResolveChannelConfig(string secretRef)
    {
        if (!TryResolveChannelConfig(secretRef, out var config) || config is null)
            throw new KeyNotFoundException("Cấu hình kênh không tồn tại hoặc không hợp lệ trên máy chủ.");
        return config;
    }
}
