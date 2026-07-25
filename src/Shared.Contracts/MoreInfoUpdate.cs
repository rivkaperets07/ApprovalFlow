namespace ApprovalFlow.Contracts;

/// <summary>Body for <c>POST /provide-info/{trackingId}</c>: the submitter fills in
/// whatever a reviewer said was missing. Deliberately excludes Vendor/TotalAmount/Category —
/// the fields that drive the autonomy ceiling — so "more info" can never be used to quietly
/// change the amount that already triggered a human review. Every field is optional
/// and only overwrites the stored invoice when non-null, so a submitter only filling in the
/// one thing that was missing doesn't blank out everything else.</summary>
public class MoreInfoUpdate
{
    public string? Notes { get; set; }
    public List<LineItem>? LineItems { get; set; }
    public string? BusinessJustification { get; set; }
    public string? ClientName { get; set; }
    public string? Currency { get; set; }
    public string? TripId { get; set; }
    public bool? IsPremiumTravel { get; set; }

    // dev-branch extension: lets a submitter asked to retake an unreadable receipt photo
    // (GLOBAL-RECEIPT-UNREADABLE) attach a clearer one via this same resume path, without
    // a new endpoint. Not a ceiling-driving field by itself - Vendor/TotalAmount only ever
    // change as a side effect of re-running OCR on the new photo, same as any other retry.
    public string? ReceiptImageDataUri { get; set; }
}
