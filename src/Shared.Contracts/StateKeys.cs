namespace ApprovalFlow.Contracts;

/// <summary>State-store keys shared *across* services. Only the invoice record itself
/// lives here — Gateway and DecisionEngine both read and write it, and a drifted key
/// format would split one invoice into two records. Keys private to a single service
/// (the Gateway's "-submitted" flag, PaymentService's claim/processed/reservation keys)
/// stay in that service, next to the code that owns them.</summary>
public static class StateKeys
{
    public static string Invoice(string trackingId) => $"invoice-{trackingId}";
}
