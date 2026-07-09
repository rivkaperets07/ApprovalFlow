using System.Text.RegularExpressions;
using DecisionEngine.Core.Models;

namespace DecisionEngine.Ai;

/// <summary>
/// Deterministic, no-network coherence checker. Used as the default provider so the system
/// runs without an API key, and as the CI/test provider so builds never depend on a
/// live LLM or rate limits (per the assignment's CI guidance).
///
/// GLOBAL-VENDOR guarantees this is only ever called for a vendor already in
/// VendorDirectory, so PolicyEngine has already resolved the category deterministically —
/// this provider isn't asked to classify it. Its job is narrower: does the free-text Notes
/// and the itemized LineItems actually look like they describe that category of expense, or
/// do they read as something else (a miscoded entry, or worse, an attempt to slip a
/// different kind of spend through under a trusted vendor's name)? Reuses the same keyword
/// table a classifier would, just for confirmation instead of a first guess.
/// </summary>
public class StubAiModelProvider : IAiModelProvider
{
    private const double CoherentConfidence = 0.97;
    private const double NoSignalConfidence = 0.90;
    private const double MismatchConfidence = 0.40;

    private static readonly (string Category, string[] Keywords)[] CategoryKeywords =
    [
        ("SaaS", ["subscription", "saas", "license", "cloud", "software"]),
        ("Hardware", ["laptop", "monitor", "keyboard", "hardware", "device"]),
        ("Meals", ["lunch", "dinner", "restaurant", "meal", "catering"]),
        ("Travel", ["flight", "hotel", "taxi", "uber", "airfare", "travel"]),
        ("OfficeSupplies", ["paper", "stationery", "office supplies", "printer"]),
        ("Marketing", ["ads", "advertising", "campaign", "marketing", "sponsorship"]),
    ];

    public Task<AiAnalysisResult> AnalyzeAsync(InvoicePayload invoice, string category, CancellationToken cancellationToken)
    {
        var haystack = string.Join(" ", new[] { invoice.Notes, invoice.Category }
            .Concat(invoice.LineItems?.Select(item => item.Description) ?? [])
            .Where(text => !string.IsNullOrWhiteSpace(text)));

        var matchedCategory = CategoryKeywords.FirstOrDefault(c => c.Keywords.Any(keyword => haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))).Category;

        double confidence;
        string reasoning;
        if (matchedCategory is null)
        {
            confidence = NoSignalConfidence;
            reasoning = $"No keyword signal in notes/line items to confirm or contradict '{category}'.";
        }
        else if (string.Equals(matchedCategory, category, StringComparison.OrdinalIgnoreCase))
        {
            confidence = CoherentConfidence;
            reasoning = $"Notes/line items are consistent with the vendor's known category '{category}'.";
        }
        else
        {
            confidence = MismatchConfidence;
            reasoning = $"Notes/line items read like '{matchedCategory}', but the vendor directory lists '{category}' for this vendor — flagged for review.";
        }

        var result = new AiAnalysisResult
        {
            ConfidenceScore = confidence,
            Reasoning = $"Stub coherence check: {reasoning}"
        };

        if (string.Equals(category, "Travel", StringComparison.OrdinalIgnoreCase))
        {
            result.LinkedTripId = ExtractTripId(invoice.Notes) ?? $"TRIP-{invoice.TrackingId}";
        }

        return Task.FromResult(result);
    }

    private static string? ExtractTripId(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var match = Regex.Match(notes, @"TripId[:\s]*([A-Za-z0-9\-]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
