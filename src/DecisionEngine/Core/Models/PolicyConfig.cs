namespace DecisionEngine.Core.Models;

/// <summary>
/// Per-category policy row bound from <c>Policies/policies.json</c>. Most categories use
/// only <see cref="MaxAmount"/>; Travel and Meals also use their dedicated fields below.
/// </summary>
public class PolicyConfig
{
    /// <summary>Flat ceiling, used by every category except Travel. For Meals, this is
    /// specifically MEAL-01's personal-meal ceiling.</summary>
    public decimal MaxAmount { get; set; }
    public double? MinConfidence { get; set; }

    /// <summary>Travel only: cumulative cap per TripId, tracked in the Dapr state store.</summary>
    public decimal? TripCap { get; set; }

    /// <summary>Travel only: per-invoice daily allowance.</summary>
    public decimal? PerDiem { get; set; }

    /// <summary>Meals only, MEAL-02: ceiling for client entertainment (distinct from
    /// <see cref="MaxAmount"/>'s personal-meal ceiling).</summary>
    public decimal? ClientEntertainmentMaxAmount { get; set; }

    /// <summary>Meals only, MEAL-02: above this amount, client entertainment requires both
    /// a business justification and a client name or it is escalated.</summary>
    public decimal? ClientEntertainmentJustificationThreshold { get; set; }
}
