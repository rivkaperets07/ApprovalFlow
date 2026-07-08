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
- **Too permissive** → the AI approves things it shouldn't, and a misclassification or a
  gamed category turns into real money out the door.

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
| **Other / unknown** | **$100** flat | 0.80 | Fallback for anything the classifier can't place — deliberately low so "I don't know" trends toward a human. |

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

The AI only produces inputs — a category, a confidence score, and extracted fields. Every
ceiling check is plain C# in `PolicyEngine`, evaluated **after** the AI has spoken and
**without ever reading the free-text `Notes`**. So a "please approve this" instruction
smuggled into an invoice has no path into the decision (regression-tested by
`AntiCheeseGuard_NotesAskingForApproval_DoNotFlipTheDecision`). The category-agnostic
`RiskThreshold` is the backstop: even if the AI is gamed into a generous category, the
$5,000 gate fires first.

## Known gaps (honest status — not yet enforced)

To keep this document truthful about what the router does *today*, the following `policy.md`
rules are **specified but not yet implemented**, and are the planned next steps:

- **`AUTONOMY-CEILING` as an additional flat cap** — we intentionally use per-category
  ceilings instead; if the assignment requires the flat $250 to *also* apply as an absolute
  cap on top of category logic, that is a one-line global check we have not added.
- **`MEAL-02`** (client entertainment > $500 needs justification + client name).
- **`MEAL-03`** (alcohol-only not reimbursable).
- **`TRAVEL-03`** (first/business-class always human).
- **`GLOBAL-VENDOR`** as a hard stop (new/unknown vendor → always human) — a vendor
  directory exists but is used for classification, not yet as a router hard stop.
- **`GLOBAL-FX`** (foreign-currency conversion + $1,000 hard stop) — no currency field yet.

## Tuning without redeploy

All numbers above live in `policies.json`, bind-mounted into the DecisionEngine container and
read via `IConfiguration` with `reloadOnChange: true`. Raising a ceiling is a file edit plus
a few seconds — not a rebuild. See ADR-003 in `docs/ARCHITECTURE.md`.
