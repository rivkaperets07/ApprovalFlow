using System.Security.Cryptography;
using System.Text;
using Dapr.Client;

/// <summary>
/// dev-branch guardrail (docs/adr/008): with typed Vendor/InvoiceNumber/TotalAmount no
/// longer available at submit time, DuplicateInvoiceGuard's vendor+invoiceNumber+total key
/// is unusable here (see that class's own doc comment - it is now orphaned, not deleted,
/// since main still submits typed fields and could reuse it). This instead keys on the
/// receipt photo's own content: an exact SHA-256 hash of ReceiptImageDataUri.
///
/// Deliberately not OCR'd-invoice-number-based, and deliberately exact rather than a
/// perceptual/fuzzy image hash: an invoice number printed on an informal receipt (a taxi
/// ticket, a lunch receipt) is often just a small sequential counter that resets per day or
/// per register - two different legitimate receipts can share one, so trusting it alone
/// risks rejecting a genuine expense. A photo's own bytes don't have that problem, and an
/// exact hash never produces a false-positive collision between two different photos. The
/// tradeoff, accepted here: retaking a fresh photo of the same physical receipt produces
/// different bytes and slips past this guard - closing that gap would need a perceptual
/// hash (tolerant of recompression/crop/lighting), left as a known future extension.
/// </summary>
public static class DuplicatePhotoGuard
{
    private const int MaxAttempts = 3;

    public static string BuildKey(string receiptImageDataUri)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(receiptImageDataUri));
        return $"dup-photo-{Convert.ToHexString(hash)}";
    }

    /// <summary>
    /// Returns true if this exact photo was already submitted (i.e. this submission should
    /// be rejected). Otherwise records it as seen and returns false. Same ETag-conditional-
    /// write/retry shape as DuplicateInvoiceGuard, for the same reason: two submissions of
    /// the same photo landing at the same instant must not both slip through.
    /// </summary>
    public static async Task<bool> IsDuplicateAsync(DaprClient daprClient, string storeName, string receiptImageDataUri)
    {
        var key = BuildKey(receiptImageDataUri);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var (seen, etag) = await daprClient.GetStateAndETagAsync<bool?>(storeName, key);
            if (seen == true)
                return true;

            if (await daprClient.TrySaveStateAsync(storeName, key, true, etag))
                return false;
        }

        // Same reasoning as DuplicateInvoiceGuard: the only writer of this key is this
        // guard for this exact photo content, so err toward rejecting rather than letting
        // an unresolved race pay the same receipt twice.
        return true;
    }
}
