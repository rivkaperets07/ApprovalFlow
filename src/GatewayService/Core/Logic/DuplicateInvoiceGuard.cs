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
    private const int MaxAttempts = 3;

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

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var (seen, etag) = await daprClient.GetStateAndETagAsync<bool?>(storeName, key);
            if (seen == true)
                return true;

            // The TrySaveStateAsync result matters: losing this ETag race means a
            // concurrent submission with the same Vendor + InvoiceNumber + TotalAmount just
            // recorded the key — loop back so the re-read flags *this* one as the duplicate,
            // instead of both slipping through and paying twice.
            if (await daprClient.TrySaveStateAsync(storeName, key, true, etag))
                return false;
        }

        // Attempts exhausted without recording or confirming. The only writer of this key
        // is this guard for this exact three-field content, so err on the side GLOBAL-DUP
        // exists for: no second payment — call it a duplicate.
        return true;
    }
}
