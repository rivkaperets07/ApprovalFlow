using System.Globalization;
using Dapr.Client;

/// <summary>
/// Backstop for accidental duplicate submissions that don't reuse the same TrackingId
/// and don't supply an InvoiceNumber (so neither the TrackingId check nor GLOBAL-DUP catches
/// them) — e.g. an API client that mints a fresh TrackingId per call, or a genuine
/// double-click that slips past the UI's button-disable guard. Keys on content (Vendor +
/// TotalAmount + Category + Notes) within a short time-to-live window rather than
/// permanently: unlike GLOBAL-DUP's exact InvoiceNumber match, this is a fuzzy signal that
/// must expire — two people legitimately expensing the same vendor/amount/category on
/// different days (or even the same day) must never collide (same reasoning as FraudGuard's
/// "never compare across submissions forever").
/// </summary>
public static class RecentSubmissionGuard
{
    private const int WindowSeconds = 60;
    private const int MaxAttempts = 3;

    public static string BuildKey(string vendor, decimal total, string? category, string? notes)
    {
        var normalizedVendor = vendor.Trim().ToLowerInvariant();
        var normalizedTotal = total.ToString("F2", CultureInfo.InvariantCulture);
        var normalizedCategory = (category ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedNotes = (notes ?? string.Empty).Trim().ToLowerInvariant();
        return $"recent-{normalizedVendor}-{normalizedTotal}-{normalizedCategory}-{normalizedNotes}";
    }

    /// <summary>
    /// Claims this content for <see cref="WindowSeconds"/> seconds. Returns the TrackingId of
    /// the earlier submission if this exact content was already claimed within the window
    /// (i.e. this is an accidental repeat the caller should short-circuit on), or null if this
    /// is a fresh claim.
    /// </summary>
    public static async Task<string?> TryClaimAsync(DaprClient daprClient, string storeName, string vendor, decimal total, string? category, string? notes, string trackingId)
    {
        var key = BuildKey(vendor, total, category, notes);
        var metadata = new Dictionary<string, string> { ["ttlInSeconds"] = WindowSeconds.ToString(CultureInfo.InvariantCulture) };

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var (existingTrackingId, etag) = await daprClient.GetStateAndETagAsync<string?>(storeName, key);
            if (!string.IsNullOrEmpty(existingTrackingId))
                return existingTrackingId;

            // The TrySaveStateAsync result matters: losing this ETag race means a
            // concurrent identical submission claimed the key a moment ago — loop back and
            // read *its* TrackingId instead of letting both through, since two requests
            // landing in the same instant is exactly the double-click this guard exists for.
            if (await daprClient.TrySaveStateAsync(storeName, key, trackingId, etag, metadata: metadata))
                return null;
        }

        // Attempts exhausted: the key kept changing under us without ever reading back a
        // claimant (not a pattern a real double-click produces). Treat as fresh — this
        // guard is a best-effort backstop, and GLOBAL-DUP still covers the exact-match case.
        return null;
    }
}
