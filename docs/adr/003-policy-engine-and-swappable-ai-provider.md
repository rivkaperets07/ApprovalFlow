# ADR 003: Data-Driven PolicyEngine with a Swappable AI Provider

**Date:** July 2026
**Status:** Accepted

## Context
The Dilemma (assignment brief) requires choosing an autonomy posture per expense
category, encoding it in code, and proving it can never be overstepped (M12) — while
still letting a controller change thresholds without a redeploy (F7, M13). The AI's role
is coherence review and extraction only; it must not be able to talk its way past a
ceiling — and, since `GLOBAL-VENDOR` already guarantees any vendor reaching it is in
`VendorDirectory`, it isn't asked to classify the category either. `PolicyEngine` resolves
the category with a plain lookup (`ResolveVendorCategory`); the AI's job is to judge
whether the submission's `Notes`/`LineItems` actually look coherent with that category
(and to flag it, via a lower confidence, when they don't — the closest thing this system
has to an AI-driven fraud signal, on top of the deterministic guardrails below).

## Decision
- **PolicyEngine is deterministic and does not depend on the AI for the category at
  all.** It reads `Policies/policies.json` (bind-mounted, not baked into the image — see
  below) and evaluates, in order: (1) a flat `RiskThreshold` ($5000) that applies
  regardless of category; (2) a per-category minimum confidence, now read as "how
  coherent is this submission" rather than "how sure is the AI of the category"; (3) the
  category rule itself — flat ceilings for every category (Meals included: $75 per
  submission, since each person expenses their own meal separately) and a
  Dapr-state-backed cumulative `TripId` total + per-diem for Travel. Travel is a
  hard-coded branch rather than a generic rule DSL — with only one formula-based category,
  a small expression engine would be speculative complexity for no present benefit.
- **`IAiModelProvider` is an anti-corruption layer (M15).** `StubAiModelProvider` is a
  deterministic keyword-based coherence checker used by default and in CI/tests, so builds
  never depend on a live LLM or hit a rate limit. `GroqAiModelProvider` calls a free-tier
  LLM and is selected via the `AiProvider` config key — swapping providers is a config
  change, not a code change. Any provider failure (timeout, bad response, missing key)
  is caught at the call site and forces `RouterDecision.Escalated(...)`; it never falls
  through to an approval.
- **Secrets vs. config.** The Groq API key is fetched via `DaprClient.GetSecretAsync`
  against a `secretstores.local.env` component (M5's "secrets" building block). Policy
  thresholds, by contrast, are bind-mounted from the host and read via
  `IConfiguration` + `reloadOnChange: true` rather than Dapr's Configuration API: Dapr
  Configuration is read/subscribe-only from the app's side (there is no "write config"
  call an app can make), so using it here would need extra seeding tooling with no
  corresponding requirement forcing it — the bind mount already satisfies "changeable
  without a redeploy" (F7, M13) with far less moving parts.

## Consequences
- **Positive:** M12's proof does not depend on trusting the AI at all for the category —
  the AI is never asked to choose one, so there is nothing for a gamed/hallucinated
  response to redirect. The `RiskThreshold` remains a category-agnostic backstop on top of
  that for the amount itself.
- **Positive:** Threshold changes (e.g. raising the SaaS ceiling) are a file edit and a
  few seconds' wait for `reloadOnChange`, not a container rebuild.
- **Trade-off:** The `Other` fallback category and Travel's cumulative-trip-cap special
  case mean a brand-new category with its own formula still requires a code change, not
  just a config change — accepted because the assignment's category list is fixed by
  `policy.md`.
