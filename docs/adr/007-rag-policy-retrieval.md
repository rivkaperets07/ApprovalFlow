# ADR 007: RAG over docs/policy.md (N5)

**Date:** July 2026
**Status:** Accepted

## Context

Every AI provider (`StubAiModelProvider`, `GroqAiModelProvider`) already runs a coherence
check against the submitted invoice — is the category, vendor, and notes internally
consistent, and does it match what the policy actually says about that kind of expense.
Until now that check had no policy text to reason against at all; N5's own wording in the
assignment is specific about the shape of the fix: "retrieve only the relevant rule(s)
instead of putting the whole policy in the prompt." Two constraints shaped the design:

- **F4** requires surfacing "the policy rules it cited," so retrieval has to produce
  identifiable rule IDs, not just prose the model paraphrases.
- **M12** requires the system be "provably incapable of auto-approving above the
  configured ceiling — even when the agent is forced to recommend approval." `PolicyEngine`
  already enforces this in code, never asking the AI about numeric thresholds at all
  (`docs/adr/003-policy-engine-and-swappable-ai-provider.md`). Retrieval must not quietly
  undo that guarantee by handing the AI the ceiling numbers through a side door.

## Decision

### Retrieval: TF-IDF cosine similarity, not embeddings

`PolicyRetriever` (`src/DecisionEngine/Ai/PolicyRetriever.cs`) parses `docs/policy.md`'s
rule tables into `PolicyClause(RuleId, Section, Text)` records once at startup, then scores
a query against every clause with classical TF-IDF + cosine similarity. The corpus is a few
dozen short rules — well inside what TF-IDF handles well — and staying off an embeddings
API keeps retrieval fully local and deterministic: no network call, no extra model to
version alongside `AiProvider`, and `StubAiModelProvider` (used in CI and most tests) gets
the exact same retrieval behavior as `GroqAiModelProvider`, which an embeddings API
reachable only when a key is configured would not allow.

A crude suffix-stripping stemmer (`Stem()`) runs at tokenization time — just enough to stop
"subscription" (a submitter's free-text notes) from scoring as unrelated to "Subscriptions"
(the rule's own wording) purely over pluralization. Indexing folds each clause's section
header into its vector alongside the rule text (`IndexedText`), so a query built around a
category name (e.g. "SaaS") aligns with the section it belongs to ("Software / SaaS") even
when no individual rule's own sentence happens to repeat that word.

### Scope: sections 1-5 only, never 6 or 7 (M12)

`PolicyRetriever.IndexableSections` allow-lists exactly the per-category rule sections
(Meals & Entertainment, Travel, Software / SaaS, Hardware, Global rules). Section 6
(autonomy thresholds) and section 7 (budgets) are structurally excluded from parsing — not
filtered after the fact, not trusted to the AI's own restraint. `PolicyRetrieverTests`
asserts this directly (`ParsePolicyMarkdown_ExcludesAutonomyThresholdsSection`): no query,
however crafted, can retrieve `AUTONOMY-CEILING` or any other numeric ceiling, because
those rows never become `PolicyClause` records in the first place. The AI's prompt gets
rule *text* it can reason about and cite; the dollar amounts that actually gate
approve/escalate stay exclusively in `PolicyEngine`'s code path, exactly as ADR 003 already
established.

### Query construction and citation

`PolicyRetriever.BuildQuery(invoice, category)` combines the AI-classified category, the
submitter's free-text notes, and any line-item descriptions — the same signals the
coherence check already reasons over, not new input. `StubAiModelProvider` retrieves the
top 2 clauses and appends their rule IDs to its canned reasoning; `GroqAiModelProvider`
retrieves the top 3, embeds their text in the system prompt with an explicit instruction
not to compute or restate dollar thresholds from them, and falls back to the retrieved rule
IDs if the model's own `PolicyRulesCited` response comes back empty. Either way, the result
flows into `AiAnalysisResult.PolicyRulesCited` → `InvoicePayload.AiPolicyRulesCited`,
surfaced through `/status`, `/escalations`, and the UI's status card and escalations table
(answering F4 directly).

### Wiring

`docs/policy.md` lives outside `DecisionEngine`'s build context (`src/`), so it can only
reach the container via a bind mount, never a Dockerfile `COPY` — `docker-compose.yml`
mounts the whole `docs/` directory read-only (`./docs:/app/docs:ro`), a directory mount for
the same Docker Desktop/Hyper-V reliability reasons as every other bind mount in this
stack (see `docker-compose.yml`'s own comments): a single-file mount is itself a mount
point, and containers referencing one have gotten stuck un-killably "Created" on this setup
more than once. `PolicyRetriever.LoadFromFile` is registered as a singleton
(`DecisionEngine.cs`) and parsed once at startup, not per request.

### Verified, not assumed

143 unit/integration tests pass (`PolicyRetrieverTests` covers parsing, section exclusion,
ranking, `topK`, empty/no-overlap queries, and `BuildQuery`), `dotnet format
--verify-no-changes` is clean. Live: submitted a Meals invoice to a known vendor with
alcohol-only line items (`"Craft beer flight"`, `"Bottle of red wine"`, notes "drinks only,
no food ordered"). `/status` and `/escalations` both came back with
`"reason": "Alcohol-only receipts are not reimbursable (MEAL-03)."` and
`"aiPolicyRulesCited": ["MEAL-03", "GLOBAL-FRAUD"]`. Cross-checked against the N4 trace for
the same `correlation_id` in Jaeger: the `ai.analyze_invoice` span carries a matching
`ai.policy_rules_cited = MEAL-03,GLOBAL-FRAUD` tag, confirming the same retrieval result
that reached the API also reached the trace — not two independently-plausible code paths
that happen to agree in theory.

## Consequences

- **Positive:** the AI's prompt now contains a handful of relevant rules instead of the
  entire policy document, which is both what N5 asks for and, incidentally, what keeps
  Groq prompt sizes small regardless of how large `docs/policy.md` grows.
- **Positive:** M12's guarantee survives N5 by construction, not by convention — the
  numeric ceilings are unreachable through this code path, not merely unlikely to be
  retrieved.
- **Negative/trade-off:** TF-IDF cosine similarity has no real semantic understanding;
  paraphrases with no vocabulary overlap with a rule's own wording (or the stemmer's blind
  spots) won't retrieve it. Acceptable here because retrieval only *augments* the AI's
  qualitative reasoning and citation — `PolicyEngine`'s code, not this ranking, is what
  actually enforces policy.
- **Negative/known gap:** the stemmer is deliberately crude (suffix-stripping, not a real
  Porter stemmer) and English-only, consistent with the rest of this assignment's scope.
