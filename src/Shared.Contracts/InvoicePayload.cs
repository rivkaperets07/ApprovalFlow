namespace ApprovalFlow.Contracts;

/// <summary>
/// The invoice as it travels across services (Gateway → DecisionEngine → Payment) over
/// Dapr pub/sub and state. This is the single wire contract shared by every service:
/// previously each service declared its own near-identical copy inside its Program.cs,
/// and they had already drifted (the Gateway's copy carried payment fields the others
/// lacked). One shape, one owner (SRP).
/// </summary>
public class InvoicePayload
{
    public string? TrackingId { get; set; }
    public string Vendor { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? Status { get; set; }
    public string? Reason { get; set; }
    public string? DecidedBy { get; set; }

    // Payment outcome, merged back in by the Gateway from PaymentService.
    public string? PaymentStatus { get; set; }
    public string? PaymentMessage { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }

    /// <summary>What the AI actually classified this as — distinct from Category above,
    /// which is just whatever text the submitter typed and plays no role in the decision.
    /// Null when the AI was never consulted (e.g. fast-rejected on a global guardrail).</summary>
    public string? AiSuggestedCategory { get; set; }
    public double? AiConfidence { get; set; }

    /// <summary>docs/policy.md rule_ids the AI's coherence check actually retrieved and
    /// cited (PolicyRetriever — RAG over the policy document, not the whole file in every
    /// prompt). Directly answers "the policy rules it cited"; null/empty when the AI
    /// was never consulted, same as AiSuggestedCategory above.</summary>
    public List<string>? AiPolicyRulesCited { get; set; }

    // Structured fact the submitter provides directly (no OCR per the assignment) — always
    // takes precedence over anything the AI might otherwise guess from free-text Notes.
    public string? TripId { get; set; }
    public List<LineItem>? LineItems { get; set; }

    // MEAL-02 (docs/policy.md): client entertainment is a distinct Meals sub-case with its
    // own ceiling and, above the justification threshold, requires both fields below —
    // an explicit flag rather than inferring "entertainment" from amount, since a personal
    // meal can legitimately cost more than a cheap client coffee.
    public bool IsClientEntertainment { get; set; }
    public string? BusinessJustification { get; set; }
    public string? ClientName { get; set; }

    // GLOBAL-FX (docs/policy.md): the submitter states the original currency directly (no
    // OCR, no live FX lookup). Null/"USD" means TotalAmount is already domestic; any other
    // value marks TotalAmount as an already-converted FX amount subject to GLOBAL-FX.
    public string? Currency { get; set; }

    // TRAVEL-03: first/business-class travel is always human, regardless of amount — an
    // explicit flag rather than parsing free text, consistent with every other structured
    // fact on this payload.
    public bool IsPremiumTravel { get; set; }

    // Idempotency discriminator for invoice.submitted deliveries (Dapr pub/sub is
    // at-least-once): the Gateway stamps a fresh value on every *deliberate* publish — the
    // first submission, and each provide-info re-evaluation — so DecisionEngine can tell a
    // redelivered event (same id: skip, don't re-run the AI or re-increment trip totals)
    // from a genuine re-evaluation (new id: process). Nullable so older/foreign events
    // still deserialize; DecisionEngine falls back to TrackingId for those.
    public string? SubmissionAttemptId { get; set; }

    // GLOBAL-DUP (docs/policy.md): optional, submitter-typed. When present, a repeat of the
    // same Vendor + InvoiceNumber + TotalAmount is rejected outright as a duplicate. Not
    // required — the actual double-payment protection is TrackingId-based idempotency
    // (ISubmissionStore) plus the UI's submit-button guard, since a typed invoice number can
    // always be omitted or altered and so is not load-bearing on its own.
    public string? InvoiceNumber { get; set; }
}
