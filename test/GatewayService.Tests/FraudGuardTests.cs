using Xunit;

public class FraudGuardTests
{
    private static readonly IReadOnlySet<string> KnownVendors =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CloudSoft Inc", "The Corner Bistro" };

    [Fact]
    public void RoundNumber_ToUnknownVendor_IsSuspicious()
    {
        Assert.True(FraudGuard.IsLikelySuspicious("Shady Consulting LLC", 500m, KnownVendors));
    }

    [Fact]
    public void RoundNumber_ToKnownVendor_IsNotSuspicious()
    {
        Assert.False(FraudGuard.IsLikelySuspicious("CloudSoft Inc", 500m, KnownVendors));
    }

    [Fact]
    public void NonRoundNumber_ToUnknownVendor_IsNotSuspicious()
    {
        Assert.False(FraudGuard.IsLikelySuspicious("Shady Consulting LLC", 499.99m, KnownVendors));
    }

    [Fact]
    public void KnownVendorMatch_IsCaseAndWhitespaceInsensitive()
    {
        Assert.False(FraudGuard.IsLikelySuspicious("  cloudsoft inc  ", 500m, KnownVendors));
    }

    [Fact]
    public void TwoDifferentSubmitters_SameKnownVendorAndAmount_NeitherIsSuspicious()
    {
        // The old vendor+amount+24h heuristic used to block the second of these — a real
        // false positive (two coworkers lunching at the same place for the same price).
        // The signal is now evaluated per-submission, so neither one trips it.
        Assert.False(FraudGuard.IsLikelySuspicious("The Corner Bistro", 18m, KnownVendors));
        Assert.False(FraudGuard.IsLikelySuspicious("The Corner Bistro", 18m, KnownVendors));
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(250, true)]
    [InlineData(100.50, false)]
    [InlineData(0.99, false)]
    public void IsRoundNumber_HasNoFractionalCents(decimal amount, bool expected)
    {
        Assert.Equal(expected, FraudGuard.IsRoundNumber(amount));
    }
}
