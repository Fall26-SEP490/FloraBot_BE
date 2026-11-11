using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloraBot.Api.Modules.Payment;

internal sealed record PayOsOrderReceiver(string Bin, string AccountNumber, string? AccountName, string Mode)
{
    public override string ToString() => $"PayOsOrderReceiver(Bin={Bin}, Mode={Mode}, Account=***)";
}

internal static class PayOsReceiverEvidence
{
    private static readonly Regex Account = new(@"\A[0-9A-Za-z]{4,30}\z", RegexOptions.CultureInvariant);
    private static readonly Regex BankBin = new(@"\A[0-9]{4,10}\z", RegexOptions.CultureInvariant);
    private static readonly Regex FingerprintFormat = new(@"\Av1:[0-9a-f]{64}\z", RegexOptions.CultureInvariant);

    internal static string Fingerprint(PayOsChannelOptions options) => "v1:" +
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            options.ClientId, options.ApiKey, options.ChecksumKey, options.ExpectedBin, options.ExpectedAccount
        })));

    internal static bool MatchesFingerprint(PayOsChannelOptions options, string? fingerprint) =>
        fingerprint is not null && FingerprintFormat.IsMatch(fingerprint) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Fingerprint(options)), Encoding.UTF8.GetBytes(fingerprint));

    internal static bool IsAccount(string? value) => value is not null && Account.IsMatch(value);

    // Call only after verifying the provider signature, order, amount and status.
    internal static PayOsOrderReceiver ReadCreation(JsonElement data, PayOsChannelOptions options, bool verifiedVirtualAccounts)
    {
        if (data.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(options.ExpectedBin) ||
            !BankBin.IsMatch(options.ExpectedBin) ||
            !data.TryGetProperty("bin", out var bin) || bin.ValueKind != JsonValueKind.String ||
            !string.Equals(bin.GetString(), options.ExpectedBin, StringComparison.Ordinal))
            throw new PaymentGatewayException("provider_receiver_bin_mismatch");
        if (!IsAccount(options.ExpectedAccount)) throw new PaymentGatewayException("configuration_receiver_invalid");
        if (!data.TryGetProperty("accountNumber", out var account))
            throw new PaymentGatewayException("provider_receiver_account_missing");
        if (account.ValueKind != JsonValueKind.String || !IsAccount(account.GetString()))
            throw new PaymentGatewayException("provider_receiver_account_invalid");
        var actual = account.GetString()!;
        var isRoot = string.Equals(actual, options.ExpectedAccount, StringComparison.OrdinalIgnoreCase);
        if (!isRoot && !verifiedVirtualAccounts) throw new PaymentGatewayException("provider_receiver_account_mismatch");
        string? name = null;
        if (data.TryGetProperty("accountName", out var nameValue) && nameValue.ValueKind != JsonValueKind.Null)
        {
            if (nameValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameValue.GetString()) ||
                nameValue.GetString()!.Length > 120 || nameValue.GetString()!.Any(char.IsControl))
                throw new PaymentGatewayException("provider_receiver_name_invalid");
            name = nameValue.GetString();
        }
        return new(options.ExpectedBin, actual, name, isRoot ? "ROOT_ACCOUNT" : "VIRTUAL_ACCOUNT");
    }

    // accountNumber may describe the root or the order VA; virtualAccountNumber, if supplied,
    // must agree with the signed recipient saved for this exact order. Never inspect payer fields.
    internal static bool ValidateSubsequent(JsonElement data, string root, string? pinned, bool requireAccount)
    {
        if (data.ValueKind != JsonValueKind.Object || !IsAccount(root) || pinned is not null && !IsAccount(pinned)) return false;
        if (data.TryGetProperty("accountNumber", out var account))
        {
            if (account.ValueKind != JsonValueKind.String || !IsAccount(account.GetString())) return false;
            var actual = account.GetString();
            if (!string.Equals(actual, root, StringComparison.OrdinalIgnoreCase) &&
                (pinned is null || !string.Equals(actual, pinned, StringComparison.OrdinalIgnoreCase))) return false;
        }
        else if (requireAccount) return false;
        if (data.TryGetProperty("virtualAccountNumber", out var va) && va.ValueKind != JsonValueKind.Null)
        {
            if (va.ValueKind != JsonValueKind.String) return false;
            var actual = va.GetString();
            if (!string.IsNullOrEmpty(actual) && (!IsAccount(actual) || pinned is null ||
                !string.Equals(actual, pinned, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }
}
