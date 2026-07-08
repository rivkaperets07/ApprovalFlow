using System.Text.RegularExpressions;
using DecisionEngine.Core.Models;
using Microsoft.Extensions.Configuration;

namespace DecisionEngine.Ai;

/// <summary>
/// Deterministic, no-network classifier. Used as the default provider so the system
/// runs without an API key, and as the CI/test provider so builds never depend on a
/// live LLM or rate limits (per the assignment's CI guidance).
///
/// A real invoice carries more signal than free-text keywords alone — in practice the
/// vendor itself is usually the strongest signal ("Delta Airlines" is Travel no matter
/// what the notes say). This checks a known-vendor directory first (high confidence),
/// falls back to keyword matching in vendor/notes/category text (medium confidence),
/// and only defaults to "Other" with reduced confidence when neither signal fires — low
/// enough to fall below Other's MinConfidence and escalate rather than guess.
/// </summary>
public class StubAiModelProvider : IAiModelProvider
{
    private const double KnownVendorConfidence = 0.97;
    private const double KeywordMatchConfidence = 0.85;
    private const double NoSignalConfidence = 0.75;

    private static readonly (string Category, string[] Keywords)[] CategoryKeywords =
    [
        ("SaaS", ["subscription", "saas", "license", "cloud", "software"]),
        ("Hardware", ["laptop", "monitor", "keyboard", "hardware", "device"]),
        ("Meals", ["lunch", "dinner", "restaurant", "meal", "catering"]),
        ("Travel", ["flight", "hotel", "taxi", "uber", "airfare", "travel"]),
        ("OfficeSupplies", ["paper", "stationery", "office supplies", "printer"]),
        ("Marketing", ["ads", "advertising", "campaign", "marketing", "sponsorship"]),
    ];

    private readonly IConfiguration _config;

    public StubAiModelProvider(IConfiguration config)
    {
        _config = config;
    }

    public Task<AiAnalysisResult> AnalyzeAsync(InvoicePayload invoice, CancellationToken cancellationToken)
    {
        var vendorMatch = _config["VendorDirectory:" + invoice.Vendor.Trim()];

        string category;
        double confidence;
        string reasoning;

        if (!string.IsNullOrEmpty(vendorMatch))
        {
            category = vendorMatch;
            confidence = KnownVendorConfidence;
            reasoning = $"Vendor '{invoice.Vendor}' matched the known vendor directory.";
        }
        else
        {
            var haystack = $"{invoice.Vendor} {invoice.Notes} {invoice.Category}";
            var keywordMatch = CategoryKeywords.FirstOrDefault(c => c.Keywords.Any(keyword => haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
            if (keywordMatch.Category is not null)
            {
                category = keywordMatch.Category;
                confidence = KeywordMatchConfidence;
                reasoning = "Vendor not in the known directory; classified from keywords in vendor/notes/category text.";
            }
            else
            {
                category = "Other";
                confidence = NoSignalConfidence;
                reasoning = "No known vendor match and no recognizable keywords — low-confidence Other, likely to escalate.";
            }
        }

        var result = new AiAnalysisResult
        {
            SuggestedCategory = category,
            ConfidenceScore = confidence,
            Reasoning = $"Stub classification: {reasoning}"
        };

        if (category == "Travel")
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
