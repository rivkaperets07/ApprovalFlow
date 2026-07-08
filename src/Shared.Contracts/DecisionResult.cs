namespace ApprovalFlow.Contracts;

/// <summary>Published on <c>invoice.decided</c> so the Gateway can keep its escalation
/// index and status view in sync with whatever the DecisionEngine (or a human) decided.</summary>
public class DecisionResult
{
    public string TrackingId { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? DecidedBy { get; set; }
}
