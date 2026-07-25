# Product Dilemma — Autonomy Posture

> Required by `docs/policy.md §6`: if you tune the autonomy thresholds you must say so and
> justify it here, **and the numbers below must match what the router actually enforces**.
> This document is kept in sync with `src/DecisionEngine/Policies/policies.json` and
> `src/DecisionEngine/Core/Logic/PolicyEngine.cs`.

## The dilemma

*How much money should an AI be allowed to approve with **no human involvement**?*

`policy.md §6` offers a deliberately conservative default: a single flat ceiling of
**$250** — the agent may auto-approve anything at or below it (given confidence and no hard
stop), and everything above it goes to a human. §6 also explicitly invites us to choose our
own posture and defend it.

The tension is a classic one:

- **Too conservative** (flat $250) → humans rubber-stamp a flood of low-risk, high-volume
  items (a $200 SaaS renewal from a known vendor), which defeats the purpose of automating
  at all. The auto-approval rate stays near zero and the tool adds latency without removing
  work.
- **Too permissive** → the AI's coherence check overlooks something it shouldn't (or is
  gamed by a submission crafted to read as legitimate), and that turns into real money out
  the door.

## Our decision: per-category autonomy ceilings, not one flat number

We **deviate from the flat $250 default**. Risk is not uniform across categories, so a single
number is the wrong instrument. Instead, each category has its own autonomy ceiling,
calibrated to that category's typical value, volume, and blast radius. A human still sees
everything above the category ceiling, everything below the (higher, per-category)
confidence bar, and every hard stop.

The intent: push the **auto-approval rate up on the safe, high-volume categories** (SaaS,
office supplies) while keeping the **strictest posture on the categories most prone to abuse
or high value** (Meals, Travel, Hardware, Marketing).

## Thresholds the router actually enforces

These values are read live from `policies.json` (bind-mounted, `reloadOnChange`), so they can
be tuned without a redeploy.

### Global guardrails (category-agnostic, checked first)

| Guardrail | Value | Effect |
|---|---|---|
| `RiskThreshold` | **$5,000** | Any invoice over this is escalated **before** the AI is even called — category cannot dodge it. |
| `DefaultMinConfidence` | **0.80** | Confidence floor when a category doesn't set its own. |
| `ReceiptRequiredAbove` (`GLOBAL-RECEIPT`) | **$25** | Over this, an itemized line-item breakdown is mandatory. |
| Math tolerance (`GLOBAL-MATH`) | **2% or $10, whichever is lower** | Line items must reconcile to the total within this variance, else escalate. |

### Per-category autonomy ceilings

| Category | Autonomy ceiling | Min confidence | Rationale |
|---|---|---|---|
| **SaaS** | **$200** flat | 0.80 | Matches `SAAS-01`. High-volume, low-risk, usually known vendors — this is where auto-approval earns its keep. |
| **Hardware** | **$1,000** flat | 0.85 | Matches `HW-01`; over $1,000 is Capital (`HW-02`) and always human via this ceiling. |
| **Office Supplies** | **$150** flat | 0.80 | Extended category (not in `policy.md`); low value, low risk. |
| **Marketing** | **$1,500** flat | 0.85 | Extended category; higher ceiling because spend is lumpier, offset by a stricter confidence bar. |
| **Meals** | **$75** flat, per submission (personal) / **$800** flat (client entertainment, `MEAL-02`) | 0.90 | `MEAL-01`: each person expenses their own meal separately — no group headcount, so there's no attendee-count field to falsify. Client entertainment is a distinct, submitter-flagged sub-case: `policy.md` only fixes the $500 justification threshold, not a ceiling, so **$800** is our own choice — above the $500 line where MEAL-02's justification+client-name requirement kicks in, but well below Marketing's $1,500 since this is relationship spend, not a campaign budget. Strictest confidence bar of the flat categories since Meals is the highest-frequency, easiest-to-abuse category. |
| **Travel** | **$200/day per-diem** + **$2,000 cumulative per `TripId`** | 0.85 | Conservative: per-diem ($200) is far below `TRAVEL-02`'s $1,500 single-expense line, and the cumulative cap is tracked across invoices in the Dapr state store. Missing `TripId` → escalate. First/business class (`TRAVEL-03`, submitter-flagged) is always human regardless of amount — checked before the per-diem math so a cheap premium fare can't slip through. |
| **Other / unknown** | **$100** flat | 0.80 | Fallback when a vendor's directory category has no matching policy section configured — deliberately low so a config gap trends toward a human rather than silently auto-approving. |

### Hard stops — always human, regardless of amount or confidence

Enforced today: `RiskThreshold`, `GLOBAL-VENDOR` (vendor not in the known-vendor
directory, checked in `PolicyEngine` before any category logic — see below), `GLOBAL-RECEIPT`
(missing itemization), `GLOBAL-MATH` (mismatch), `GLOBAL-FRAUD` (round-number amount to a
vendor not in the known-vendor directory, blocked at the Gateway *before publish* — judged
on the submission's own attributes only, so two different people expensing the same known
vendor for the same amount never trips it), `MEAL-02` (client entertainment over $500
missing a business justification or client name), `MEAL-03` (alcohol-only line items —
escalated rather than auto-rejected, since the keyword signal can misfire and a human
should have the final say on money leaving the door), `GLOBAL-FX` (a foreign-currency item
— submitter-declared, no live FX lookup — over **$1,000**, matching `policy.md`'s literal
number), `GLOBAL-DUP` (an exact repeat of `Vendor` + `InvoiceNumber` + `TotalAmount`,
rejected outright — see caveat below), `TRAVEL-03` (first/business class,
submitter-flagged), missing required info for Travel (`TripId`), and any AI/provider error
(fails to `Escalated`, never to approval).

Note: `GLOBAL-FRAUD`'s vendor-newness clause is now a strict subset of `GLOBAL-VENDOR`
(any unknown vendor is already a hard stop, round-number or not) — kept as a separate,
earlier check at the Gateway purely as a cost/latency optimization (skips the pub/sub
round-trip and the AI call entirely for that case), not as an independent rule.

**`GLOBAL-DUP` caveat, stated plainly:** `InvoiceNumber` is an *optional*, submitter-typed
field (`DuplicateInvoiceGuard`, checked at the Gateway before publish, no time window — an
invoice number should never legitimately repeat). It is implemented literally as
`policy.md` specifies, but it is **not** F3/M10's real double-payment protection: a typed
field can always be omitted or altered, so it only catches the honest/careless case. The
mechanism that actually can't be dodged is `TrackingId`-based idempotency
(`ISubmissionStore`), which catches redelivered/retried requests regardless of what the
submitter types, plus the UI disabling its submit button while a request is in flight to
prevent a literal double-click from ever making two HTTP requests. `GLOBAL-DUP` is real,
additional evidence trail on top of that — not a substitute for it.

## Why this can't be over-stepped (the M12 guarantee)

The AI never picks the category — `PolicyEngine.ResolveVendorCategory` reads it straight
from `VendorDirectory`, since `GLOBAL-VENDOR` already guarantees the vendor is known before
the AI is ever consulted. The AI only produces a confidence score, a reasoning string, and
extracted fields (like Travel's `TripId`). Every ceiling check is plain C# in
`PolicyEngine`, evaluated **after** the AI has spoken and **without ever reading the
free-text `Notes`**. So a "please approve this" instruction smuggled into an invoice has no
path into the decision (regression-tested by
`AntiCheeseGuard_NotesAskingForApproval_DoNotFlipTheDecision`), and there is no "generous
category" for a gamed AI response to redirect to in the first place — the category was
never the AI's to choose. The category-agnostic `RiskThreshold` remains a backstop on the
amount itself regardless.

## Previously flagged gaps — now closed

This document used to list the items below as "specified but not yet implemented." Verified
against `PolicyEngine.cs` on 2026-07-08 (regression-tested in `PolicyEngineTests.cs`), all are
implemented and enforced:

- **`MEAL-02`** (client entertainment > $500 needs justification + client name) — implemented
  in `EvaluateMeals`.
- **`MEAL-03`** (alcohol-only not reimbursable) — implemented in `EvaluateMeals`.
- **`TRAVEL-03`** (first/business-class always human) — implemented in `EvaluateTravelAsync`.
- **`GLOBAL-VENDOR`** (new/unknown vendor → always human) — implemented as a hard stop in
  `CheckGlobalGuardrails`, checked before any category logic.
- **`GLOBAL-FX`** (foreign-currency conversion + $1,000 hard stop) — implemented in
  `CheckGlobalGuardrails`; `InvoicePayload.Currency` carries the submitter-declared original
  currency.

## AI role change: coherence review, not classification (2026-07-08)

`GLOBAL-VENDOR` already guarantees any vendor the AI sees is in `VendorDirectory` — so
asking the AI to also guess the category was redundant (and was one more AI-controlled
input than M12 needed). `PolicyEngine.ResolveVendorCategory` now resolves it with a plain
config lookup, and `AiAnalysisResult` no longer has a `SuggestedCategory` field. The AI's
`ConfidenceScore` was repurposed rather than dropped: it now reflects whether the
submission's `Notes` and (newly passed in) `LineItems` actually read as a legitimate
expense in the already-known category — the same `MinConfidence`-per-category threshold
still escalates a low score, but a low score now means "this looks inconsistent or
suspicious" instead of "the AI wasn't sure what category this is." `StubAiModelProvider`
implements this by reusing its keyword table for confirmation instead of a first guess
(`CoherentConfidence` / `NoSignalConfidence` / `MismatchConfidence`); `GroqAiModelProvider`
tells the LLM the category up front and asks it to judge coherence instead of classify.

## Known gaps (honest status — still open)

- **`AUTONOMY-CEILING` as an additional flat cap** — we intentionally use per-category
  ceilings instead of layering the flat $250 on top; if the assignment requires both, that is
  a one-line global check we have not added.
- **F3/M10, general accidental-resubmission case — mostly closed (2026-07-08).**
  `TrackingId`-based idempotency (`ISubmissionStore`) only dedupes when the *same*
  `TrackingId` comes back; the UI now generates and holds one across a submission attempt so
  a failed request's retry reuses it. For clients that mint a fresh `TrackingId` per call
  (bypassing that), `RecentSubmissionGuard` is a second backstop: it keys on content (Vendor +
  TotalAmount + Category + Notes) with a 60-second TTL in the state store, so an accidental
  repeat without an `InvoiceNumber` is still caught. Deliberately time-boxed rather than
  permanent — like `GLOBAL-FRAUD`, a fuzzy content match must expire so two people
  legitimately expensing the same vendor/amount/category don't collide. What's still open:
  two *genuinely distinct* submissions with identical content more than 60 seconds apart are
  (correctly) not deduped — there's no way to tell those apart from an intentional resubmit
  without an explicit idempotency key from the caller.
- **F9, structured rule citations — mostly closed (N5).** `InvoicePayload.AiPolicyRulesCited`
  is now a queryable `List<string>` populated by `PolicyRetriever`'s RAG citations, surfaced
  end-to-end through `/status`, `/escalations`, and the UI — not something an auditor has to
  grep out of a sentence. What's still just embedded in a string:
  `RouterDecision.Reason`'s own citation of the triggering `rule_id` for the flat categories
  (`SAAS-01`, `HW-01`, `MEAL-01`) — that's `PolicyEngine`'s deterministic decision, a separate
  code path from the AI's citations, and hasn't been given its own structured field.
- **No document verification (by design, not oversight) — closed on the `dev` branch,
  still true of `main`.** The assignment states "No OCR required for invoices/expenses,"
  so on `main` every field (`Vendor`, `TotalAmount`, `LineItems`, `TripId`,
  `InvoiceNumber`...) is submitter-typed text, never checked against an actual invoice
  image; nothing stops a submitter from inventing numbers wholesale. The system's real
  defenses there (`TrackingId` idempotency, `RecentSubmissionGuard`, the global guardrails
  above) are built to not depend on any typed field being true — see the `GLOBAL-DUP`
  caveat. On `dev` only (see `docs/adr/008-receipt-photo-submission.md`), this is closed
  properly rather than worked around: a receipt photo is now the *only* submission path —
  typed `Vendor`/`TotalAmount` is rejected outright, not merely de-prioritized, because a
  "people can't lie" claim is meaningless if lying is still one field away. Local OCR
  extracts the fields; a narrowly-scoped AI judges only whether the photo itself looks
  fabricated. Neither can approve anything — `PolicyEngine` never learns an invoice came
  from a photo, so M12 holds exactly as it did before this existed.

## Tuning without redeploy

All numbers above live in `policies.json`, bind-mounted into the DecisionEngine container and
read via `IConfiguration` with `reloadOnChange: true`. Raising a ceiling is a file edit plus
a few seconds — not a rebuild. See [ADR 004](adr/004-file-based-policy-configuration.md).
