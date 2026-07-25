using System.Globalization;
using DecisionEngine.Core.Models;

namespace DecisionEngine.Ocr;

/// <summary>
/// Deterministic, no-dependency stand-in for real OCR - the default provider, and the one
/// CI/tests always use so a build never needs the native Tesseract engine installed.
/// Doesn't read pixels at all: it parses a small fixture convention out of the "photo"
/// string itself, the same documented-fiction idea as PaymentGateway's "FailBank" marker.
///
/// Convention: "BLURRY-RECEIPT" anywhere in the string -> extraction fails (simulates an
/// unreadable photo). Otherwise, looks for
/// "OCR:{Vendor}|{TotalAmount}|{Currency}|{item};...|{PremiumFlag}" where Currency, the
/// line-items segment, and the premium-travel segment may all be empty/omitted, and each
/// line item is "{Description}:{Amount}" separated by ';'. PremiumFlag is "PREMIUM"
/// (case-insensitive) to simulate a first/business-class ticket being detected, anything
/// else (including absent) means not detected. No marker at all -> extraction fails
/// (there's nothing to read - a real photo would be required for the real extractor).
/// </summary>
public class StubReceiptOcrExtractor : IReceiptOcrExtractor
{
    private const string BlurryMarker = "BLURRY-RECEIPT";
    private const string OcrMarkerPrefix = "OCR:";

    public ReceiptExtractionResult Extract(string receiptImageDataUri)
    {
        if (receiptImageDataUri.Contains(BlurryMarker, StringComparison.OrdinalIgnoreCase))
        {
            return new ReceiptExtractionResult { Succeeded = false, FailureReason = "Photo marked as unreadable (BLURRY-RECEIPT fixture marker)." };
        }

        var markerIndex = receiptImageDataUri.IndexOf(OcrMarkerPrefix, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return new ReceiptExtractionResult { Succeeded = false, FailureReason = "No OCR fixture marker found in the photo." };
        }

        var parts = receiptImageDataUri[(markerIndex + OcrMarkerPrefix.Length)..].Split('|');
        if (parts.Length < 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            !decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            return new ReceiptExtractionResult { Succeeded = false, FailureReason = "OCR marker present but vendor/amount could not be parsed." };
        }

        var currency = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null;
        List<LineItem>? lineItems = null;
        if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]))
        {
            lineItems = parts[3]
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Split(':'))
                .Where(pair => pair.Length == 2 && decimal.TryParse(pair[1], NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                .Select(pair => new LineItem(pair[0], decimal.Parse(pair[1], CultureInfo.InvariantCulture)))
                .ToList();
        }

        var isPremiumTravel = parts.Length > 4 && parts[4].Equals("PREMIUM", StringComparison.OrdinalIgnoreCase);

        return new ReceiptExtractionResult
        {
            Succeeded = true,
            Vendor = parts[0],
            TotalAmount = amount,
            Currency = currency,
            LineItems = lineItems is { Count: > 0 } ? lineItems : null,
            IsPremiumTravel = isPremiumTravel
        };
    }
}
