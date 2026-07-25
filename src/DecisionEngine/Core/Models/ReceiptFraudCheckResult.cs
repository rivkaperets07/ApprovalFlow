namespace DecisionEngine.Core.Models;

/// <summary>Two states, not three: legibility/missing-fields is entirely
/// IReceiptOcrExtractor's problem, resolved before a fraud check is ever run. An unsure
/// model should report Suspicious at low confidence rather than needing a third state -
/// "not confidently genuine" is already the signal that matters, and it still only ever
/// escalates to a human, never auto-rejects (same posture as the existing MEAL-03 rule).</summary>
public enum ReceiptGenuinenessVerdict { Genuine, Suspicious }

public class ReceiptFraudCheckResult
{
    public ReceiptGenuinenessVerdict Verdict { get; set; }
    public double Confidence { get; set; }
    public string Reasoning { get; set; } = string.Empty;
}
