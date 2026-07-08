using System.Globalization;
using Dapr.Client;

/// <summary>
/// GLOBAL-FRAUD guardrail from docs/policy.md: "Multiple transactions featuring identical
/// amounts from the same vendor within a rolling 24-hour window are systematically
/// blocked." TrackingId-based dedupe (ISubmissionStore) only catches a *retried* request
/// that reuses the same id — it does nothing for two independent submissions (no shared
/// id, each gets its own GUID) that happen to be the same vendor and amount, which is
/// exactly the accidental-double-submit case this guardrail exists for.
/// </summary>
public static class FraudGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public static string BuildKey(string vendor, decimal amount)
    {
        var normalizedVendor = vendor.Trim().ToLowerInvariant();
        var normalizedAmount = amount.ToString("F2", CultureInfo.InvariantCulture);
        return $"fraud-{normalizedVendor}-{normalizedAmount}";
    }

    /// <summary>
    /// Returns true if a submission for this vendor+amount was already seen within the
    /// last 24 hours (i.e. this one should be blocked). Otherwise records "now" as the
    /// latest occurrence and returns false.
    /// </summary>
    public static async Task<bool> IsLikelyDuplicateAsync(DaprClient daprClient, string storeName, string vendor, decimal amount, DateTimeOffset now)
    {
        var key = BuildKey(vendor, amount);
        var (lastSeen, etag) = await daprClient.GetStateAndETagAsync<DateTimeOffset?>(storeName, key);
        if (lastSeen.HasValue && now - lastSeen.Value < Window)
            return true;

        await daprClient.TrySaveStateAsync(storeName, key, now, etag);
        return false;
    }
}
