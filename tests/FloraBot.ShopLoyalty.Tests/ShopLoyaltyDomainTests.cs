using FloraBot.Api.Modules.Identity;

namespace FloraBot.ShopLoyalty.Tests;

[Trait("Category", "Unit")]
public sealed class ShopLoyaltyDomainTests
{
    [Theory]
    [InlineData("SHOP_POLICY", true)]
    [InlineData("LEGACY_GLOBAL", false)]
    public void OrderProvenance_CorrectlyIdentifiesShopLoyaltyPolicy(string policy, bool isShopPolicy)
    {
        Assert.Equal(isShopPolicy, policy == "SHOP_POLICY");
    }

    [Theory]
    [InlineData(100_000, 1_000)]
    [InlineData(350_000, 3_500)]
    [InlineData(297_000, 2_970)]
    [InlineData(99, 0)]
    [InlineData(0, 0)]
    public void Preorder_EarnsExactOnePercentOfActualPaidAmount(long actualPaidAmount, long expectedEarn)
    {
        var earn = CalculateEarnPoints(policy: "SHOP_POLICY", orderType: "PREORDER", sellerEnabled: true, isSelfOrder: false, actualPaidAmount);
        Assert.Equal(expectedEarn, earn);
    }

    [Fact]
    public void LegacyGlobalPolicy_DoesNotEarnIntoShopLoyalty()
    {
        var earn = CalculateEarnPoints(policy: "LEGACY_GLOBAL", orderType: "PREORDER", sellerEnabled: true, isSelfOrder: false, actualPaidAmount: 100_000);
        Assert.Equal(0, earn);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(350_000)]
    [InlineData(297_000)]
    public void WalkIn_AlwaysEarnsZero(long actualPaidAmount)
    {
        var earn = CalculateEarnPoints(policy: "SHOP_POLICY", orderType: "WALK_IN", sellerEnabled: true, isSelfOrder: false, actualPaidAmount);
        Assert.Equal(0, earn);
    }

    [Fact]
    public void SelfOrder_AlwaysEarnsZero()
    {
        var earn = CalculateEarnPoints(policy: "SHOP_POLICY", orderType: "PREORDER", sellerEnabled: true, isSelfOrder: true, actualPaidAmount: 500_000);
        Assert.Equal(0, earn);
    }

    [Fact]
    public void DisabledLoyalty_AlwaysEarnsZero()
    {
        var earn = CalculateEarnPoints(policy: "SHOP_POLICY", orderType: "PREORDER", sellerEnabled: false, isSelfOrder: false, actualPaidAmount: 500_000);
        Assert.Equal(0, earn);
    }

    [Fact]
    public void DebtRepayment_ClearsDebtBeforeAccruingBalance()
    {
        long balance = 0;
        long debt = 500;
        long earned = 1200;

        ApplyEarnWithDebtRepayment(ref balance, ref debt, earned, out var debtRepaid);

        Assert.Equal(500, debtRepaid);
        Assert.Equal(0, debt);
        Assert.Equal(700, balance);
    }

    [Fact]
    public void DebtRepayment_PartialWhenEarnedLessThanDebt()
    {
        long balance = 0;
        long debt = 1500;
        long earned = 800;

        ApplyEarnWithDebtRepayment(ref balance, ref debt, earned, out var debtRepaid);

        Assert.Equal(800, debtRepaid);
        Assert.Equal(700, debt);
        Assert.Equal(0, balance);
    }

    [Fact]
    public void EarnAlreadySpent_RefundPreservesDebtWithoutNegativeCash()
    {
        long balance = 0; // customer spent the earned points on another order
        long debt = 0;
        long pointsToReverse = 1000;

        ApplyReversePointsWithDebt(ref balance, ref debt, pointsToReverse, out var deficit);

        Assert.Equal(0, balance);
        Assert.Equal(1000, debt);
        Assert.Equal(1000, deficit);
    }

    [Fact]
    public void EarnPartiallySpent_RefundDeductsAvailableAndPreservesRemainingDebt()
    {
        long balance = 300; // customer spent 700 of 1000 points
        long debt = 0;
        long pointsToReverse = 1000;

        ApplyReversePointsWithDebt(ref balance, ref debt, pointsToReverse, out var deficit);

        Assert.Equal(0, balance);
        Assert.Equal(700, debt);
        Assert.Equal(700, deficit);
    }

    [Fact]
    public void PartialRefund_ReversesProportionalPointsOnly()
    {
        long totalPaid = 100_000;
        long totalAwarded = 1_000;
        long alreadyReversed = 0;
        long refundAmount = 30_000;

        var toReverse = CalculateProportionalReverse(totalPaid, totalAwarded, alreadyReversed, refundAmount);

        Assert.Equal(300, toReverse);
    }

    [Fact]
    public void RedemptionCheck_BlockedWhenDebtExists()
    {
        long balance = 500;
        long debt = 200;

        var canRedeem = CanRedeemPoints(balance, debt, pointsToRedeem: 100, out var reason);

        Assert.False(canRedeem);
        Assert.Contains("nợ", reason);
    }

    [Fact]
    public void RedemptionCheck_BlockedWhenExceedingBalance()
    {
        long balance = 300;
        long debt = 0;

        var canRedeem = CanRedeemPoints(balance, debt, pointsToRedeem: 500, out var reason);

        Assert.False(canRedeem);
        Assert.Contains("không đủ", reason);
    }

    [Fact]
    public void RedemptionCheck_AllowedWhenSufficientBalanceAndZeroDebt()
    {
        long balance = 500;
        long debt = 0;

        var canRedeem = CanRedeemPoints(balance, debt, pointsToRedeem: 300, out var reason);

        Assert.True(canRedeem);
        Assert.Null(reason);
    }

    // Domain helpers matching SQL specifications
    private static long CalculateEarnPoints(string policy, string orderType, bool sellerEnabled, bool isSelfOrder, long actualPaidAmount)
    {
        if (policy != "SHOP_POLICY" || orderType != "PREORDER" || !sellerEnabled || isSelfOrder || actualPaidAmount <= 0)
            return 0;
        return (long)Math.Floor(actualPaidAmount * 0.01m);
    }

    private static void ApplyEarnWithDebtRepayment(ref long balance, ref long debt, long earned, out long debtRepaid)
    {
        if (debt > 0)
        {
            debtRepaid = Math.Min(debt, earned);
            var remaining = earned - debtRepaid;
            debt -= debtRepaid;
            balance += remaining;
        }
        else
        {
            debtRepaid = 0;
            balance += earned;
        }
    }

    private static void ApplyReversePointsWithDebt(ref long balance, ref long debt, long pointsToReverse, out long deficit)
    {
        if (balance >= pointsToReverse)
        {
            balance -= pointsToReverse;
            deficit = 0;
        }
        else
        {
            deficit = pointsToReverse - balance;
            balance = 0;
            debt += deficit;
        }
    }

    private static long CalculateProportionalReverse(long totalPaid, long totalAwarded, long alreadyReversed, long refundAmount)
    {
        var proportional = (long)Math.Floor(refundAmount * 0.01m);
        var maxCanReverse = Math.Max(0, totalAwarded - alreadyReversed);
        return Math.Min(proportional, maxCanReverse);
    }

    private static bool CanRedeemPoints(long balance, long debt, long pointsToRedeem, out string? reason)
    {
        if (debt > 0)
        {
            reason = $"Tài khoản đang có nợ {debt} điểm tại shop này";
            return false;
        }
        if (pointsToRedeem > balance)
        {
            reason = $"Chỉ còn {balance} điểm tại shop này, không đủ {pointsToRedeem}";
            return false;
        }
        reason = null;
        return true;
    }
}
