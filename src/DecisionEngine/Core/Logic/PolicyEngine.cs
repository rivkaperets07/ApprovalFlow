using Dapr.Client;
using DecisionEngine.Core.Models;

namespace DecisionEngine.Core.Logic;

/// <summary>
/// Deterministic gate that turns an AI classification into an approve/escalate decision.
/// The AI only ever supplies inputs (category, confidence, extracted metadata); every
/// threshold check below is plain code, so a manipulated or overconfident AI response
/// can never push a decision past the configured ceilings (M12).
/// </summary>
public class PolicyEngine
{
    private const string StateStoreName = "statestore";

    private readonly IConfiguration _config;
    private readonly DaprClient _daprClient;

    public PolicyEngine(IConfiguration config, DaprClient daprClient)
    {
        _config = config;
        _daprClient = daprClient;
    }

    /// <summary>
    /// Cheap, synchronous, no-AI pre-check. The risk threshold never depends on category,
    /// so there is no reason to pay for an AI classification call when the amount alone
    /// already forces an escalation — the caller should check this first and only invoke
    /// the AI provider when it returns null. EvaluateAsync also re-checks the threshold
    /// (defense in depth for any caller that skips this fast path), so skipping straight
    /// to EvaluateAsync is never unsafe, only sometimes wasteful.
    /// </summary>
    public RouterDecision? TryFastRejectOnRiskThreshold(InvoicePayload invoice)
    {
        var guardrails = _config.GetSection("GlobalGuardrails").Get<GlobalGuardrailsConfig>() ?? new GlobalGuardrailsConfig();
        if (invoice.TotalAmount > guardrails.RiskThreshold)
            return RouterDecision.Escalated($"{invoice.TotalAmount:C} exceeds the global risk threshold of {guardrails.RiskThreshold:C}.");

        return null;
    }

    public async Task<RouterDecision> EvaluateAsync(InvoicePayload invoice, AiAnalysisResult aiResult)
    {
        var guardrails = _config.GetSection("GlobalGuardrails").Get<GlobalGuardrailsConfig>() ?? new GlobalGuardrailsConfig();

        // Absolute ceiling, checked before category logic. This is what makes the ceiling
        // provable regardless of category: the AI cannot escape it by picking a more
        // generous category, because this check does not depend on the category at all.
        if (invoice.TotalAmount > guardrails.RiskThreshold)
            return RouterDecision.Escalated($"{invoice.TotalAmount:C} exceeds the global risk threshold of {guardrails.RiskThreshold:C}.");

        var category = aiResult.SuggestedCategory;
        var policy = _config.GetSection($"ExpensePolicies:{category}").Get<PolicyConfig>();
        var usedFallback = policy is null;
        if (usedFallback)
        {
            policy = _config.GetSection("ExpensePolicies:Other").Get<PolicyConfig>();
            if (policy is null)
                return RouterDecision.Escalated($"Unknown category '{category}' and no fallback policy configured.");
        }

        var minConfidence = policy!.MinConfidence ?? guardrails.DefaultMinConfidence;
        if (aiResult.ConfidenceScore < minConfidence)
            return RouterDecision.Escalated($"AI confidence {aiResult.ConfidenceScore:0.00} is below the required {minConfidence:0.00} for '{category}'.");

        var effectiveCategory = usedFallback ? "Other" : category;

        return effectiveCategory switch
        {
            "Meals" => EvaluateMeals(invoice, aiResult, policy),
            "Travel" => await EvaluateTravelAsync(invoice, aiResult, policy),
            _ => EvaluateFlat(invoice, policy, effectiveCategory)
        };
    }

    private static RouterDecision EvaluateFlat(InvoicePayload invoice, PolicyConfig policy, string category)
    {
        if (invoice.TotalAmount > policy.MaxAmount)
            return RouterDecision.Escalated($"{invoice.TotalAmount:C} exceeds the {category} ceiling of {policy.MaxAmount:C}.");

        return RouterDecision.Approved($"Within the {category} ceiling of {policy.MaxAmount:C}.");
    }

    private static RouterDecision EvaluateMeals(InvoicePayload invoice, AiAnalysisResult aiResult, PolicyConfig policy)
    {
        if (aiResult.MealAttendeesCount <= 0)
            return RouterDecision.Escalated("Meals category requires a verified attendee count.");

        var perAttendee = policy.PerAttendeeAmount ?? 0m;
        var ceiling = perAttendee * aiResult.MealAttendeesCount;
        if (invoice.TotalAmount > ceiling)
            return RouterDecision.Escalated($"{invoice.TotalAmount:C} exceeds {perAttendee:C} x {aiResult.MealAttendeesCount} attendees ({ceiling:C}).");

        return RouterDecision.Approved($"Within the Meals ceiling of {perAttendee:C}/attendee ({ceiling:C} for {aiResult.MealAttendeesCount} attendees).");
    }

    private async Task<RouterDecision> EvaluateTravelAsync(InvoicePayload invoice, AiAnalysisResult aiResult, PolicyConfig policy)
    {
        if (string.IsNullOrWhiteSpace(aiResult.LinkedTripId))
            return RouterDecision.Escalated("Travel category requires a valid TripId.");

        var perDiem = policy.PerDiem ?? 0m;
        if (invoice.TotalAmount > perDiem)
            return RouterDecision.Escalated($"{invoice.TotalAmount:C} exceeds the {perDiem:C} daily travel allowance.");

        var tripKey = $"trip-{aiResult.LinkedTripId}-total";
        var priorTotal = await _daprClient.GetStateAsync<decimal?>(StateStoreName, tripKey) ?? 0m;
        var tripCap = policy.TripCap ?? 0m;
        var newTotal = priorTotal + invoice.TotalAmount;

        if (newTotal > tripCap)
            return RouterDecision.Escalated($"Trip {aiResult.LinkedTripId} cumulative total {newTotal:C} would exceed the {tripCap:C} trip cap ({priorTotal:C} already used).");

        // Only persist the reservation once the invoice is actually approved.
        await _daprClient.SaveStateAsync(StateStoreName, tripKey, newTotal);
        return RouterDecision.Approved($"Within the {perDiem:C}/day allowance; trip {aiResult.LinkedTripId} now at {newTotal:C} of {tripCap:C}.");
    }
}

public class PolicyConfig
{
    /// <summary>Flat ceiling, used by every category except Meals and Travel.</summary>
    public decimal MaxAmount { get; set; }
    public double? MinConfidence { get; set; }

    /// <summary>Meals only: ceiling = PerAttendeeAmount * AiAnalysisResult.MealAttendeesCount.</summary>
    public decimal? PerAttendeeAmount { get; set; }

    /// <summary>Travel only: cumulative cap per TripId, tracked in the Dapr state store.</summary>
    public decimal? TripCap { get; set; }

    /// <summary>Travel only: per-invoice daily allowance.</summary>
    public decimal? PerDiem { get; set; }
}

public class GlobalGuardrailsConfig
{
    public decimal RiskThreshold { get; set; } = 5000m;
    public double DefaultMinConfidence { get; set; } = 0.80;
}
