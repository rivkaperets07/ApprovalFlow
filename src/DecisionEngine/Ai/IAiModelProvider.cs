using DecisionEngine.Core.Models;

namespace DecisionEngine.Ai;

/// <summary>
/// Anti-corruption layer between DecisionEngine and whichever LLM backs invoice
/// classification. Swappable via the "AiProvider" config key (M15) — the PolicyEngine
/// never talks to a provider directly, only to this interface.
/// </summary>
public interface IAiModelProvider
{
    Task<AiAnalysisResult> AnalyzeAsync(InvoicePayload invoice, CancellationToken cancellationToken);
}
