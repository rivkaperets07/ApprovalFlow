/// <summary>
/// State machine for the approver's manual actions (approve / reject / request-info): they
/// only ever act on an item that is actually waiting on a human. Everything else is either
/// still in flight or already final:
/// - Pending would race the DecisionEngine's own decision for the same invoice;
/// - Approved has already published invoice.approved (and likely paid) — re-approving
///   double-fires the payment event, and reject-after-approve can't claw money back;
/// - Rejected / Duplicate are terminal calls the audit trail relies on staying put.
/// This is also what stops a Travel invoice's cumulative trip total from being counted
/// twice: without it, request-info on an already-Approved invoice followed by provide-info
/// re-runs the whole evaluation (and its trip-total increment) for money already spent.
/// </summary>
public static class ApproverActionGuard
{
    /// <summary>True when a human may act on this status: Escalated (the queue itself) or
    /// NeedsInfo (parked with the submitter, but the approver may still decide outright or
    /// re-word what they asked for).</summary>
    public static bool CanActOn(string? status)
        => status is InvoiceStatus.Escalated or InvoiceStatus.NeedsInfo;
}
