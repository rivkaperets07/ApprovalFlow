namespace DecisionEngine.Core.Models;

/// <summary>
/// The AI's opinion of a submission it was told the category for (PolicyEngine resolves the
/// category itself from VendorDirectory — GLOBAL-VENDOR guarantees it's known — so this no
/// longer includes a SuggestedCategory). ConfidenceScore now reflects whether the Notes/
/// LineItems plausibly describe a legitimate expense in that category, not how sure the AI
/// is about classification.
/// </summary>
public class AiAnalysisResult
{
    public string? LinkedTripId { get; set; }
    public double ConfidenceScore { get; set; }
    public string Reasoning { get; set; } = string.Empty;

    /// <summary>rule_ids from docs/policy.md the AI actually considered — retrieved by
    /// PolicyRetriever, not the whole document, and never the numeric thresholds in it
    /// (those stay code-only). Matches the doc's own suggested field name
    /// ("policy_violations[].rule_id"), directly answering "the policy rules it
    /// cited."</summary>
    public List<string> PolicyRulesCited { get; set; } = [];
}