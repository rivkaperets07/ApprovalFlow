using System.Globalization;
using Dapr.Client;

/// <summary>
/// GLOBAL-DUP guardrail from docs/policy.md: "A duplicate (same vendor + invoiceNumber +
/// total) is rejected as a duplicate — no second payment." Unlike GLOBAL-FRAUD (a fuzzy,
/// amount-only signal that must never compare different submissions), this is an exact
/// match on three fields the submitter controls — a much higher-confidence signal, so it
/// rejects outright rather than escalating. No time window: an invoice number should never
/// legitimately repeat for the same vendor and amount.
/// </summary>
public static class DuplicateInvoiceGuard
{
    public static string BuildKey(string vendor, string invoiceNumber, decimal total)
    {
        var normalizedVendor = vendor.Trim().ToLowerInvariant();
        var normalizedInvoiceNumber = invoiceNumber.Trim().ToLowerInvariant();
        var normalizedTotal = total.ToString("F2", CultureInfo.InvariantCulture);
        return $"dup-{normalizedVendor}-{normalizedInvoiceNumber}-{normalizedTotal}";
    }

    /// <summary>
    /// Returns true if this exact Vendor + InvoiceNumber + TotalAmount combination was
    /// already seen (i.e. this submission should be rejected). Otherwise records it as
    /// seen and returns false.
    /// </summary>
    public static async Task<bool> IsDuplicateAsync(DaprClient daprClient, string storeName, string vendor, string invoiceNumber, decimal total)
    {
        var key = BuildKey(vendor, invoiceNumber, total);
        var (seen, etag) = await daprClient.GetStateAndETagAsync<bool?>(storeName, key);
        if (seen == true)
            return true;

        await daprClient.TrySaveStateAsync(storeName, key, true, etag);
        return false;
    }
}
