namespace ApprovalFlow.Contracts;

/// <summary>The pub/sub topics that stitch the services together. Publisher and
/// subscriber must agree on these exact strings, and a drifted copy doesn't fail
/// compilation — it publishes to a topic nobody hears — so both sides reference this
/// single set of constants.</summary>
public static class Topics
{
    /// <summary>Gateway → DecisionEngine: a submission (or a provide-info re-evaluation)
    /// ready to be decided.</summary>
    public const string InvoiceSubmitted = "invoice.submitted";

    /// <summary>DecisionEngine/Gateway → Gateway: a decision landed (AI or human); drives
    /// the escalation index and the M8 notification channel.</summary>
    public const string InvoiceDecided = "invoice.decided";

    /// <summary>DecisionEngine/Gateway → PaymentService: an approved invoice ready to be
    /// paid (the Saga's trigger).</summary>
    public const string InvoiceApproved = "invoice.approved";

    /// <summary>PaymentService → Gateway: the payment outcome (including Saga
    /// compensation) to merge into the invoice record.</summary>
    public const string PaymentCompleted = "payment.completed";
}
