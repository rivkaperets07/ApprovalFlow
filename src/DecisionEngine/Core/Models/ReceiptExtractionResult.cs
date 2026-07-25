namespace DecisionEngine.Core.Models;

/// <summary>
/// What IReceiptOcrExtractor read off a receipt photo. Succeeded requires at minimum a
/// confident Vendor + TotalAmount read - LineItems/Currency/IsPremiumTravel are best-effort
/// extras that simply stay null/false when the photo doesn't show them clearly enough to
/// bother guessing (all three already have real consumers in PolicyEngine: GLOBAL-MATH/
/// MEAL-03 for LineItems, GLOBAL-FX for Currency, TRAVEL-03 for IsPremiumTravel -
/// populating them costs nothing new there). TripId is deliberately NOT here: it identifies
/// which trip an expense belongs to, a fact about the submitter's business context, not
/// something printed on the receipt itself - it stays a submitter-typed field exactly like
/// on `main` (see InvoicePayload.TripId).
/// </summary>
public class ReceiptExtractionResult
{
    public string? Vendor { get; set; }
    public decimal? TotalAmount { get; set; }
    public List<LineItem>? LineItems { get; set; }
    public string? Currency { get; set; }
    public bool IsPremiumTravel { get; set; }
    public bool Succeeded { get; set; }
    public string? FailureReason { get; set; }
}
