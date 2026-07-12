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

    private readonly PolicyRetriever _policyRetriever;

    public StubAiModelProvider(PolicyRetriever policyRetriever)
    {
        _policyRetriever = policyRetriever;
    }

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

        // Retrieve only the policy.md rule(s) relevant to this category/notes instead
        // of reasoning in a vacuum — the Stub provider gets the same RAG treatment as Groq
        // (PolicyRetriever has no network dependency either way), so CI/tests exercise the
        // retrieval path without needing an API key.
        var citedClauses = _policyRetriever.Retrieve(PolicyRetriever.BuildQuery(invoice, category), topK: 2);
        var citedRuleIds = citedClauses.Select(c => c.RuleId).ToList();

        var result = new AiAnalysisResult
        {
            ConfidenceScore = confidence,
            Reasoning = citedRuleIds.Count > 0
                ? $"Stub coherence check: {reasoning} (considered {string.Join(", ", citedRuleIds)})"
                : $"Stub coherence check: {reasoning}",
            PolicyRulesCited = citedRuleIds
        };

        if (string.Equals(category, "Travel", StringComparison.OrdinalIgnoreCase))
        {
            // The submitter-typed TripId field (structured, no OCR) takes priority over
            // guessing one out of free-text Notes — the latter is only a fallback for
            // callers that never set the field explicitly.
            result.LinkedTripId = !string.IsNullOrWhiteSpace(invoice.TripId)
                ? invoice.TripId
                : ExtractTripId(invoice.Notes) ?? $"TRIP-{invoice.TrackingId}";
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
