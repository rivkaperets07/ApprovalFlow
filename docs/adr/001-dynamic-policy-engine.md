# ADR 001: Data-Driven Policy Engine

## Status
Accepted

## Context
F7 and M13 require that the **expense policy and autonomy thresholds be
changeable without redeploying code**. The topic map ties this to *MSA · Dapr
(config / secrets)*.

We had to decide *where* the thresholds live and *how* they reach the running
`PolicyEngine` so an operator can retune them (e.g. raise the risk ceiling,
tighten a category's confidence floor) against a live system.

A second, separate concern is M12: the autonomy ceiling must be **provably**
impossible to overstep even when the AI recommends approval. That constrains
what may be data-driven — the *numbers* can be configuration, but the *gate that
enforces them* must be deterministic code the AI cannot influence.

## Decision
Thresholds live in a plain JSON file, [`policies.json`](../../src/DecisionEngine/Policies/policies.json),
loaded into `IConfiguration` at startup with `reloadOnChange: true`
([DecisionEngine.cs](../../src/DecisionEngine/DecisionEngine.cs)). The file is
**bind-mounted from the host**, not baked into the image
([docker-compose.yml](../../docker-compose.yml)):

```yaml
- ./src/DecisionEngine/Policies:/app/Policies
```

`PolicyEngine` reads every threshold via `_config.GetSection(...)` on each
evaluation (never cached), so editing the file on the host is picked up live:
edit → `reloadOnChange` fires → the next invoice uses the new values. No rebuild,
no redeploy, no container restart.

The **structural evaluation logic stays in code** (`PolicyEngine.cs`): the
per-category shape (Travel = per-diem + cumulative trip cap, flat ceilings for every
other category including Meals) and, critically, the global guardrails
(`RiskThreshold`, `GLOBAL-RECEIPT`, `GLOBAL-MATH`) that run **before** any
category logic and do not depend on the AI's chosen category. Configuration
supplies the *numbers*; code owns the *gate*. This is what makes M12 provable —
a manipulated or overconfident AI response can change none of the checks, only
feed inputs into them.

### On "via Dapr"
The topic map suggests the Dapr configuration building block. We deliberately
used ASP.NET Core file configuration + `reloadOnChange` instead: it satisfies
the literal requirement ("changeable without a code redeploy") with far less
moving infrastructure, keeps the thresholds versioned in the repo alongside the
code that enforces them, and works identically in tests and CI. Dapr is still
used where it earns its keep (state, secrets, pub/sub — M5). Migrating the
thresholds to a Dapr `configurationstore` later is a config-only change; no
`PolicyEngine` code would move.

## Consequences
- **F7 / M13 met** for all numeric thresholds — retuned live by editing one
  file, no redeploy.
- **M12 preserved** — the enforcing gate is deterministic code, not data, so no
  configuration edit (and no AI output) can push a decision past the ceiling.
- **Not self-service (yet).** Retuning means editing a JSON file on the host,
  not a controller-facing UI/API. Adequate for the demo; a future admin endpoint
  (`GET/PUT /policy`) gated by an `admin` role (N1) would close this gap and
  reuse the same file + `reloadOnChange` path.
- **New *flat* categories are pure config** (add a section; unknown categories
  fall back to `Other`). New categories with *bespoke* logic (like Travel's cumulative
  trip cap) still require code — an accepted, deliberate boundary, since the assignment
  requires only *thresholds* to be externally configurable.
- **Trade-off vs. Dapr config** accepted and documented above, so it can be
  defended rather than discovered.
