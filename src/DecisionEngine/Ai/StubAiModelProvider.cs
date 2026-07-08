using System.Text.RegularExpressions;
using DecisionEngine.Core.Models;

namespace DecisionEngine.Ai;

/// <summary>
/// Deterministic, no-network classifier. Used as the default provider so the system
/// runs without an API key, and as the CI/test provider so builds never depend on a
/// live LLM or rate limits (per the assignment's CI guidance).
/// </summary>
public class StubAiModelProvider : IAiModelProvider
{
    private static readonly (string Category, string[] Keywords)[] CategoryKeywords =
    [
        ("SaaS", ["subscription", "saas", "license", "cloud", "software"]),
        ("Hardware", ["laptop", "monitor", "keyboard", "hardware", "device"]),
        ("Meals", ["lunch", "dinner", "restaurant", "meal", "catering"]),
        ("Travel", ["flight", "hotel", "taxi", "uber", "airfare", "travel"]),
        ("OfficeSupplies", ["paper", "stationery", "office supplies", "printer"]),
        ("Marketing", ["ads", "advertising", "campaign", "marketing", "sponsorship"]),
    ];

    public Task<AiAnalysisResult> AnalyzeAsync(InvoicePayload invoice, CancellationToken cancellationToken)
    {
        var haystack = $"{invoice.Vendor} {invoice.Notes} {invoice.Category}";
        var category = CategoryKeywords
            .FirstOrDefault(c => c.Keywords.Any(keyword => haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .Category ?? "Other";

        var result = new AiAnalysisResult
        {
            SuggestedCategory = category,
            ConfidenceScore = 0.95,
            Reasoning = "Stub classification: deterministic keyword match against vendor/notes, no LLM call."
        };

        switch (category)
        {
            case "Meals":
                result.MealAttendeesCount = ExtractAttendeeCount(invoice.Notes) ?? 1;
                break;
            case "Travel":
                result.LinkedTripId = ExtractTripId(invoice.Notes) ?? $"TRIP-{invoice.TrackingId}";
                break;
        }

        return Task.FromResult(result);
    }

    private static int? ExtractAttendeeCount(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var match = Regex.Match(notes, @"(\d+)\s*attendee", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static string? ExtractTripId(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var match = Regex.Match(notes, @"TripId[:\s]*([A-Za-z0-9\-]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
