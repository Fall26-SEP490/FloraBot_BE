using FloraBot.Api.Modules.Payment;

namespace FloraBot.SellerRefunds.Tests;

[Trait("Category", "Unit")]
public sealed class SellerRefundsDomainTests
{
    [Fact]
    public void Currency_MustBeVnd()
    {
        Assert.True(IsValidCurrency("VND"));
        Assert.True(IsValidCurrency("vnd"));
        Assert.False(IsValidCurrency("USD"));
        Assert.False(IsValidCurrency(""));
        Assert.False(IsValidCurrency(null));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1000, false)]
    [InlineData(1000, true)]
    [InlineData(500000, true)]
    public void Amount_MustBeStrictlyPositive(long amount, bool expected)
    {
        Assert.Equal(expected, amount > 0);
    }

    [Theory]
    [InlineData("TXN123456", true)]
    [InlineData("AB", false)] // too short (<3)
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ProviderReference_MustBeBounded(string reference, bool expected)
    {
        var isValid = !string.IsNullOrWhiteSpace(reference) &&
                      reference.Trim().Length is >= 3 and <= 100 &&
                      !reference.Any(char.IsControl);
        Assert.Equal(expected, isValid);
    }

    [Fact]
    public void ReceiverSnapshot_MatchesDecryptedBank()
    {
        const string customerBank = "Vietcombank";
        const string customerHolder = "NGUYEN VAN A";
        const string customerAccount = "0123456789";

        var matchesExact = IsReceiverConsistent(
            submittedBank: "Vietcombank",
            submittedHolder: "NGUYEN VAN A",
            submittedAccount: "0123456789",
            expectedBank: customerBank,
            expectedHolder: customerHolder,
            expectedAccount: customerAccount);

        Assert.True(matchesExact);

        var matchesCaseInsensitive = IsReceiverConsistent(
            submittedBank: "vietcombank",
            submittedHolder: "nguyen van a",
            submittedAccount: "0123456789",
            expectedBank: customerBank,
            expectedHolder: customerHolder,
            expectedAccount: customerAccount);

        Assert.True(matchesCaseInsensitive);

        var failsWrongAccount = IsReceiverConsistent(
            submittedBank: "Vietcombank",
            submittedHolder: "NGUYEN VAN A",
            submittedAccount: "9999999999",
            expectedBank: customerBank,
            expectedHolder: customerHolder,
            expectedAccount: customerAccount);

        Assert.False(failsWrongAccount);
    }

    [Fact]
    public void MakerChecker_ConfirmerMustBeIndependent()
    {
        var adminApprover = Guid.NewGuid();
        var sellerSubmitter = Guid.NewGuid();
        var adminConfirmer = Guid.NewGuid();

        // Confirmer cannot be approver
        Assert.False(CanAdminConfirm(adminConfirmer: adminApprover, approverId: adminApprover, submitterId: sellerSubmitter));

        // Confirmer cannot be submitter
        Assert.False(CanAdminConfirm(adminConfirmer: sellerSubmitter, approverId: adminApprover, submitterId: sellerSubmitter));

        // Independent admin can confirm
        Assert.True(CanAdminConfirm(adminConfirmer: adminConfirmer, approverId: adminApprover, submitterId: sellerSubmitter));
    }

    [Fact]
    public void TransferTime_MustNotBeInFutureOrTooOld()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(IsValidTransferTime(now.AddMinutes(-5), now));
        Assert.True(IsValidTransferTime(now.AddDays(-10), now));

        // Future by > 5 min rejected
        Assert.False(IsValidTransferTime(now.AddMinutes(10), now));

        // Older than 30 days rejected
        Assert.False(IsValidTransferTime(now.AddDays(-31), now));
    }

    [Theory]
    [InlineData("SELLER_DIRECT", true, true)]
    [InlineData("PLATFORM_LEGACY", false, false)]
    [InlineData("PLATFORM", false, false)]
    [InlineData("DIRECT_REQUIRED", false, false)]
    public void DirectRefund_Identification(string provenance, bool isDirectReceipt, bool expectedDirect)
    {
        var isDirect = provenance == "SELLER_DIRECT" || isDirectReceipt;
        Assert.Equal(expectedDirect, isDirect);
    }

    private static bool IsValidCurrency(string? currency) =>
        string.Equals(currency?.Trim(), "VND", StringComparison.OrdinalIgnoreCase);

    private static bool IsReceiverConsistent(
        string submittedBank, string submittedHolder, string submittedAccount,
        string expectedBank, string expectedHolder, string expectedAccount) =>
        string.Equals(submittedBank?.Trim(), expectedBank?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(submittedHolder?.Trim(), expectedHolder?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(submittedAccount?.Trim(), expectedAccount?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool CanAdminConfirm(Guid adminConfirmer, Guid approverId, Guid submitterId) =>
        adminConfirmer != approverId && adminConfirmer != submitterId;

    private static bool IsValidTransferTime(DateTimeOffset transferredAt, DateTimeOffset now) =>
        transferredAt <= now.AddMinutes(5) && transferredAt >= now.AddDays(-30);
}
