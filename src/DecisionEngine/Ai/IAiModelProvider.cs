using DecisionEngine.Core.Models;

namespace DecisionEngine.Ai;

/// <summary>
/// Anti-corruption layer between DecisionEngine and whichever LLM backs invoice
/// classification. Swappable via the "AiProvider" config key (M15) — the PolicyEngine
/// never talks to a provider directly, only to this interface.
/// </summary>
public interface IAiModelProvider
{
    /// <summary>
    /// <paramref name="category"/> is the vendor's directory-resolved category (see
    /// PolicyEngine.ResolveVendorCategory) — the provider is not asked to classify, only to
    /// judge whether this specific submission (Notes, LineItems) is coherent with it.
    /// </summary>
    Task<AiAnalysisResult> AnalyzeAsync(InvoicePayload invoice, string category, CancellationToken cancellationToken);
}
