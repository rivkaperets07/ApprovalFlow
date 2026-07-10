namespace DecisionEngine.Core.Models;

/// <summary>
/// Category-agnostic guardrails bound from <c>Policies/policies.json → GlobalGuardrails</c>.
/// These are checked before any category logic so a misclassified or gamed category can
/// never dodge them.
/// </summary>
public class GlobalGuardrailsConfig
{
    public decimal RiskThreshold { get; set; } = 5000m;
    public double DefaultMinConfidence { get; set; } = 0.80;

    /// <summary>GLOBAL-RECEIPT: itemization required above this amount.</summary>
    public decimal ReceiptRequiredAbove { get; set; } = 25m;

    /// <summary>GLOBAL-MATH: allowed variance between line items and claimed total, as a
    /// fraction of the total (e.g. 0.02 = 2%) — capped by MathToleranceCap, whichever is lower.</summary>
    public double MathToleranceFraction { get; set; } = 0.02;
    public decimal MathToleranceCap { get; set; } = 10m;

    /// <summary>GLOBAL-FX: any foreign-currency item above this amount is a hard stop,
    /// regardless of category ceiling.</summary>
    public decimal FxMaxAmount { get; set; } = 1000m;
}
