/// <summary>
/// GLOBAL-FRAUD guardrail from docs/policy.md: "round-number [amount] to a brand-new
/// vendor" is a fraud-pattern signal. Evaluated purely on this submission's own
/// attributes — it never compares one submission to another, so it can never fire just
/// because two different people happened to submit the same vendor and amount (that
/// coincidence is not fraud; see docs/PRODUCT-DILEMMA.md).
/// </summary>
public static class FraudGuard
{
    public static bool IsRoundNumber(decimal amount) => amount == decimal.Truncate(amount);

    public static bool IsLikelySuspicious(string vendor, decimal amount, IReadOnlySet<string> knownVendors)
    {
        var isKnownVendor = knownVendors.Contains(vendor.Trim());
        return IsRoundNumber(amount) && !isKnownVendor;
    }
}
