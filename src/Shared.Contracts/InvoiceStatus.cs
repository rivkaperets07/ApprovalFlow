namespace ApprovalFlow.Contracts;

/// <summary>Canonical invoice status values used across all services.</summary>
public static class InvoiceStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Escalated = "Escalated";
    public const string Rejected = "Rejected";
    /// <summary>A reviewer sent this back to the submitter for missing information
    /// instead of approving/rejecting outright. Distinct from Escalated — it drops out of
    /// the approver's queue (nothing for them to decide until the submitter responds) and
    /// re-enters it only once <c>POST /provide-info</c> triggers a fresh evaluation.</summary>
    public const string NeedsInfo = "NeedsInfo";
    /// <summary>GLOBAL-DUP: rejected outright as a re-submission of an already-seen
    /// Vendor + InvoiceNumber + TotalAmount — distinct from Rejected (a human's call) and
    /// from the silent TrackingId-retry short-circuit (not a new submission at all).</summary>
    public const string Duplicate = "Duplicate";
}
