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
}