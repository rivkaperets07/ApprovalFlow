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
    private readonly IConfiguration _config;
    private readonly DaprClient _daprClient;

    public PolicyEngine(IConfiguration config, DaprClient daprClient)
    {
        _config = config;
        _daprClient = daprClient;
    }

    /// <summary>
    /// Cheap, synchronous, no-AI pre-check: the risk threshold, and the GLOBAL-RECEIPT /
    /// GLOBAL-MATH guardrails, none of which depend on category. There's no reason to pay
    /// for an AI classification call on an invoice that's escalating on amount/receipt
    /// grounds alone — the caller should check this first and only invoke the AI provider
    /// when it returns null. EvaluateAsync re-checks the same guardrails (defense in depth
    /// for any caller that skips this fast path), so skipping straight to EvaluateAsync is
    /// never unsafe, only sometimes wasteful.
    /// </summary>
    public RouterDecision? TryFastRejectOnGlobalGuardrails(InvoicePayload invoice)
        => CheckGlobalGuardrails(invoice, LoadGuardrails(), LoadKnownVendors());

    /// <summary>
    /// GLOBAL-VENDOR (checked in CheckGlobalGuardrails, both here and in the fast-reject
    /// path) guarantees a vendor reaching EvaluateAsync is in VendorDirectory — so the
    /// category is this deterministic lookup, not something the AI needs to guess anymore.
    /// Null only if a caller skips the guardrail check first (defense in depth).
    /// </summary>
    public string? ResolveVendorCategory(string vendor)
        => _config[$"{VendorDirectory.SectionName}:{vendor.Trim()}"];

    public async Task<RouterDecision> EvaluateAsync(InvoicePayload invoice, string category, AiAnalysisResult aiResult)
    {
        var guardrails = LoadGuardrails();

        var globalReject = CheckGlobalGuardrails(invoice, guardrails, LoadKnownVendors());
        if (globalReject is not null)
            return globalReject;

        var policy = _config.GetSection($"ExpensePolicies:{category}").Get<PolicyConfig>();
        var usedFallback = policy is null;
        if (usedFallback)
        {
            policy = _config.GetSection("ExpensePolicies:Other").Get<PolicyConfig>();
            if (policy is null)
                return RouterDecision.Escalated($"Vendor category '{category}' has no configured policy and no fallback is available.");
        }

        var minConfidence = policy!.MinConfidence ?? guardrails.DefaultMinConfidence;
        if (aiResult.ConfidenceScore < minConfidence)
            return RouterDecision.Escalated($"AI confidence {aiResult.ConfidenceScore:0.00} is below the required {minConfidence:0.00} for '{category}'.");

        var effectiveCategory = usedFallback ? "Other" : category;

        return effectiveCategory switch
        {
            "Meals" => EvaluateMeals(invoice, policy),
            "Travel" => await EvaluateTravelAsync(invoice, aiResult, policy),
            _ => EvaluateFlat(invoice, policy, effectiveCategory)
        };
    }

    private GlobalGuardrailsConfig LoadGuardrails()
        => _config.GetSection("GlobalGuardrails").Get<GlobalGuardrailsConfig>() ?? new GlobalGuardrailsConfig();

    // Shared with the Gateway's GLOBAL-FRAUD check via VendorDirectory (Shared.Contracts):
    // both guardrails must agree on what "known vendor" means, or one can call a vendor
    // brand-new while the other trusts it.
    private IReadOnlySet<string> LoadKnownVendors()
        => VendorDirectory.LoadKnownVendors(_config);

    private static RouterDecision? CheckGlobalGuardrails(InvoicePayload invoice, GlobalGuardrailsConfig guardrails, IReadOnlySet<string> knownVendors)
    {
        // Absolute ceiling, checked before category logic. This is what makes the ceiling
        // provable regardless of category: the AI cannot escape it by picking a more
        // generous category, because this check does not depend on the category at all.
        if (invoice.TotalAmount > guardrails.RiskThreshold)
            return RouterDecision.Escalated($"{invoice.TotalAmount:C} exceeds the global risk threshold of {guardrails.RiskThreshold:C}.");

        // GLOBAL-VENDOR (docs/policy.md): a vendor the company has never dealt with before
        // is always reviewed by a human, regardless of amount or category — checked before
        // any category logic so a generous category ceiling (or a confident AI guess) can
        // never paper over an unrecognized vendor.
        if (!knownVendors.Contains(invoice.Vendor.Trim()))
            return RouterDecision.Escalated($"Vendor '{invoice.Vendor}' is not in the known-vendor directory (GLOBAL-VENDOR).");

        // GLOBAL-FX (docs/policy.md): TotalAmount is already the USD-converted amount (no
        // live FX lookup here — the submitter states the original currency directly). Any
        // FX item over the configured amount is a hard stop regardless of category ceiling.
        if (!string.IsNullOrWhiteSpace(invoice.Currency) &&
            !string.Equals(invoice.Currency.Trim(), "USD", StringComparison.OrdinalIgnoreCase) &&
            invoice.TotalAmount > guardrails.FxMaxAmount)
            return RouterDecision.Escalated($"Foreign-currency ({invoice.Currency}) item of {invoice.TotalAmount:C} exceeds the {guardrails.FxMaxAmount:C} FX ceiling (GLOBAL-FX).");

        // GLOBAL-RECEIPT / GLOBAL-MATH (docs/policy.md): no OCR — the submitter provides
        // the line items directly (structured input), never inferred by the AI.
        if (invoice.TotalAmount > guardrails.ReceiptRequiredAbove)
        {
            if (invoice.LineItems is null || invoice.LineItems.Count == 0)
                return RouterDecision.Escalated($"An itemized line-by-line breakdown is required for invoices over {guardrails.ReceiptRequiredAbove:C} (GLOBAL-RECEIPT).");

            var lineItemsTotal = invoice.LineItems.Sum(item => item.Amount);
            var tolerance = Math.Min(invoice.TotalAmount * (decimal)guardrails.MathToleranceFraction, guardrails.MathToleranceCap);
            var variance = Math.Abs(invoice.TotalAmount - lineItemsTotal);
            if (variance > tolerance)
                return RouterDecision.Escalated($"Line items sum to {lineItemsTotal:C}, which differs from the claimed total {invoice.TotalAmount:C} by {variance:C} (allowed: {tolerance:C}) (GLOBAL-MATH).");
        }

        return null;
    }

    private static readonly string[] AlcoholKeywords =
        ["beer", "wine", "liquor", "cocktail", "alcohol", "spirits", "whiskey", "vodka", "tequila", "champagne", "bar tab"];

    private static RouterDecision EvaluateMeals(InvoicePayload invoice, PolicyConfig policy)
    {
        // MEAL-03 (docs/policy.md): a receipt that is alcohol-only (every line item reads
        // as alcohol) is not reimbursable. Escalated rather than auto-rejected — the
        // keyword signal can misfire, so a human keeps the final say on money leaving the
        // door, consistent with this engine never guessing in the risky direction (M12/M15).
        if (invoice.LineItems is { Count: > 0 } && invoice.LineItems.All(IsAlcoholLineItem))
            return RouterDecision.Escalated("Alcohol-only receipts are not reimbursable (MEAL-03).");

        // MEAL-01: ordinary personal meals use the flat per-submission ceiling.
        if (!invoice.IsClientEntertainment)
            return EvaluateFlat(invoice, policy, "Meals");

        // MEAL-02: client entertainment is a distinct sub-case with its own (higher)
        // ceiling. Above the justification threshold it additionally needs both a business
        // justification and a client name, or it is a hard stop regardless of amount.
        var justificationThreshold = policy.ClientEntertainmentJustificationThreshold ?? 0m;
        if (invoice.TotalAmount > justificationThreshold &&
            (string.IsNullOrWhiteSpace(invoice.BusinessJustification) || string.IsNullOrWhiteSpace(invoice.ClientName)))
            return RouterDecision.Escalated($"Client entertainment over {justificationThreshold:C} requires both a business justification and a client name (MEAL-02).");

        var ceiling = policy.ClientEntertainmentMaxAmount ?? 0m;
        if (invoice.TotalAmount > ceiling)
            return RouterDecision.Escalated($"{invoice.TotalAmount:C} exceeds the client entertainment ceiling of {ceiling:C}.");

        return RouterDecision.Approved($"Client entertainment within the {ceiling:C} ceiling.");
    }

    private static bool IsAlcoholLineItem(LineItem item)
        => AlcoholKeywords.Any(keyword => item.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    // F9: lets an auditor pull the exact policy.md rule_id behind a flat-ceiling decision.
    // Only categories docs/policy.md actually assigns an id to are listed here — Office
    // Supplies/Marketing are this system's own extended categories with no policy.md rule
    // to cite, so they're left unlabeled rather than citing something that doesn't exist.
    private static readonly Dictionary<string, string> FlatCategoryRuleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SaaS"] = "SAAS-01",
        ["Hardware"] = "HW-01",
        ["Meals"] = "MEAL-01",
    };

    private static RouterDecision EvaluateFlat(InvoicePayload invoice, PolicyConfig policy, string category)
    {
        var ruleSuffix = FlatCategoryRuleIds.TryGetValue(category, out var ruleId) ? $" ({ruleId})" : string.Empty;

        if (invoice.TotalAmount > policy.MaxAmount)
            return RouterDecision.Escalated($"{invoice.TotalAmount:C} exceeds the {category} ceiling of {policy.MaxAmount:C}{ruleSuffix}.");

        return RouterDecision.Approved($"Within the {category} ceiling of {policy.MaxAmount:C}{ruleSuffix}.");
    }

    private async Task<RouterDecision> EvaluateTravelAsync(InvoicePayload invoice, AiAnalysisResult aiResult, PolicyConfig policy)
    {
        if (string.IsNullOrWhiteSpace(aiResult.LinkedTripId))
            return RouterDecision.Escalated("Travel category requires a valid TripId.");

        // TRAVEL-03: first/business-class is always human, regardless of amount — checked
        // before the per-diem/trip-cap math so a small first-class fare can't sneak through
        // just because it happens to be under the daily allowance.
        if (invoice.IsPremiumTravel)
            return RouterDecision.Escalated("First/business-class travel always requires manager approval (TRAVEL-03).");

        var perDiem = policy.PerDiem ?? 0m;
        if (invoice.TotalAmount > perDiem)
            return RouterDecision.Escalated($"{invoice.TotalAmount:C} exceeds the {perDiem:C} daily travel allowance.");

        var tripKey = $"trip-{aiResult.LinkedTripId}-total";
        var priorTotal = await _daprClient.GetStateAsync<decimal?>(DaprComponents.StateStore, tripKey) ?? 0m;
        var tripCap = policy.TripCap ?? 0m;
        var newTotal = priorTotal + invoice.TotalAmount;

        if (newTotal > tripCap)
            return RouterDecision.Escalated($"Trip {aiResult.LinkedTripId} cumulative total {newTotal:C} would exceed the {tripCap:C} trip cap ({priorTotal:C} already used).");

        // Only persist the reservation once the invoice is actually approved.
        await _daprClient.SaveStateAsync(DaprComponents.StateStore, tripKey, newTotal);
        return RouterDecision.Approved($"Within the {perDiem:C}/day allowance; trip {aiResult.LinkedTripId} now at {newTotal:C} of {tripCap:C}.");
    }
}
