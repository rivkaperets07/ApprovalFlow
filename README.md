# ApprovalFlow

An AI-assisted, microservice-based invoice/expense approval platform. The system ingests
invoices, resolves each one's category from a trusted vendor directory, has an AI agent
review it for coherence against `docs/policy.md` (not classify it — see
`docs/PRODUCT-DILEMMA.md`), auto-approves the in-policy majority, and escalates the rest to
a human — while a deterministic
[`PolicyEngine`](src/DecisionEngine/Core/Logic/PolicyEngine.cs) guarantees the AI can
never push a decision past the configured ceilings. Approved items flow through a
Saga-based payment with compensation on failure. See
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the system diagram, sequence diagram,
and ADRs, and [`docs/policy.md`](docs/policy.md) for the expense policy being enforced.

## Technologies

- **.NET 8** (C#, minimal APIs) for all four services.
- **[Dapr](https://dapr.io)** as the distributed application runtime: pub/sub (Redis),
  state store (Redis), and secrets (`secretstores.local.env`) — no infrastructure SDK is
  referenced directly by application code.
- **Redis** backs both the Dapr pub/sub and state store components.
- **Groq** (free-tier LLM) or a deterministic stub behind `IAiModelProvider` for invoice
  coherence review — swappable via config, no code change.
- **Swashbuckle** (OpenAPI/Swagger UI) on every service.
- **xUnit + Moq** for unit tests, **Microsoft.AspNetCore.Mvc.Testing** (`WebApplicationFactory`)
  for integration tests, and a PowerShell e2e script against a live `docker compose` stack
  (N6: three test tiers — see "How to test" below).
- **OpenTelemetry** (traces + metrics) on all three backend services, exported to
  **Jaeger** and **Prometheus** (N4 — see "Observability" below).
- Plain HTML/JS for the UI — no frontend framework/build step.

## Services

| Service | Responsibility |
| --- | --- |
| `src/GatewayService` | Single external entry point (rate-limited), JWT-authenticated with role-based access (submitter/approver/admin). Accepts submissions, exposes status/escalations/stats/audit trail, handles approve/reject/request-info, proxies vendor/policy admin actions. |
| `src/DecisionEngine` | Subscribes to `invoice.submitted`; calls the AI provider, then `PolicyEngine`, to auto-approve or escalate. |
| `src/PaymentService` | Subscribes to `invoice.approved`; runs the reserve → transfer → compensate Saga with idempotent, race-free claiming. |
| `src/UI` | Static page to submit invoices and drive manual approve/reject. |

## Requirements

- Docker Desktop + Docker Compose (WSL2 backend on Windows)
- .NET 8 SDK (only needed to run tests locally outside Docker)

## Configuration

Copy `.env.example` to `.env` (same folder as `docker-compose.yml`) and adjust:

```
GROQ_API_KEY=        # only needed if AI_PROVIDER=Groq
AI_PROVIDER=Stub      # "Stub" (deterministic, default) or "Groq" (free-tier LLM)
```

Expense policy thresholds live in `src/DecisionEngine/Policies/policies.json`, which is
bind-mounted into the container (not baked into the image) — edit it and the running
DecisionEngine picks up the change within seconds via `reloadOnChange`, no rebuild or
redeploy required (F7, M13).

## How to run

```powershell
cd C:\Users\rivka\ZioNet-ApprovalFlow
docker compose up --build -d
docker compose ps
```

Follow logs if needed:

```powershell
docker compose logs -f gateway gateway-sidecar decision-engine decision-engine-sidecar payment-service payment-service-sidecar
```

Open the UI at http://localhost:8080, or drive the API directly at http://localhost:5000
(Swagger UI at http://localhost:5000/swagger).

## Authentication (N1)

Every business endpoint requires a JWT with a role — `submitter` (submit and track your
own expenses), `approver` (escalation queue, approve/reject/request-info, dashboards, audit
trail), or `admin` (everything). Accounts are real: `POST /register` creates one (email +
password, hashed with PBKDF2 — see `PasswordHasher.cs`) and `POST /login` exchanges those
credentials for a self-signed JWT. Accounts live in the Dapr state store, keyed by email
(`DaprUserStore.cs`).

Every self-registered account starts as `submitter` — there's no role field on
`POST /register` at all, so there's nothing for a new signup to escalate. Getting an
`approver` or `admin` account takes an existing admin, one of two ways:
`POST /users` (admin-only, body `{"Email","Password","Name","Role"}`) creates a brand-new
account with the role already set — no self-registration step first; or
`PUT /users/{email}/role` (admin-only, body `{"Role":"approver"}`) promotes or demotes an
already-registered account. A fresh deployment's first admin comes from the seeded demo
account below.

`POST /change-password` (body `{"Email","CurrentPassword","NewPassword"}`) is self-service
and anonymous, same as `/login` — supplying the current password is the proof of identity,
so no bearer token is required. Wrong email and wrong current password return the same
401 `/login` does, for the same anti-enumeration reason.

Three demo accounts are seeded on Gateway startup (idempotent — safe on every restart) so
the system is usable immediately:

| Email | Password | Role |
| --- | --- | --- |
| `submitter@zionet.demo` | `Submitter123!` | submitter |
| `approver@zionet.demo` | `Approver123!` | approver |
| `admin@zionet.demo` | `Admin123!` | admin |

These are published on purpose — it's a demo state store, not a production identity
system, and hiding credentials that are checked into a public repo would only be theater.
The UI's Session card logs in against these (or lets you register a new account) and shows
only the cards your role can actually call. Dapr-sidecar-delivered routes
(`/payment-completed`, `/invoice-decided-index`) stay anonymous by design — the sidecar
carries no JWT and those routes are not part of the public surface. The JWT signing key is
`JWT_SIGNING_KEY` (`.env.example`); the checked-in fallback is for local demo only.

## Admin: vendors and policy, without a redeploy (ADR 004)

Two config files an admin can now change live, no rebuild or restart: the known-vendor
directory and the expense policy thresholds. Both endpoints are admin-only on the Gateway,
proxied to DecisionEngine over Dapr, and both write into the exact files
`IConfiguration` already loads with `reloadOnChange` — a write takes effect on the next
invoice evaluated.

- `POST /vendors` (body `{"Vendor","Category"}`) adds a new vendor to the directory, or
  updates an existing one's category (matched case-insensitively, so it can't accumulate
  case-variant duplicates).
- `GET /policy` / `PUT /policy` read or replace the whole `policies.json` document —
  `RiskThreshold`, `ReceiptRequiredAbove`, and every category's `MaxAmount`/`MinConfidence`
  (plus Travel's `PerDiem`/`TripCap` and Meals' client-entertainment fields). Validation is
  intentionally shallow (must be a JSON object with `GlobalGuardrails` and
  `ExpensePolicies`) — `PolicyEngine`'s hard-coded gate is what keeps this safe (M12), not
  this check, so a bad edit can only make the system more conservative, never let something
  bypass a ceiling.

The UI's "Manage Vendors & Policy" admin card covers both: a small form for vendor
add/update, and a raw-JSON textarea (load, edit, save) for the policy document.

## Outbox and bulkhead (N3, ADR 005)

**Outbox** — DecisionEngine's automatic decision path saves the invoice's Approved/
Escalated state and publishes `invoice.decided` in one atomic call
(`DaprClient.ExecuteStateTransactionAsync`) instead of two independent ones, using Dapr's
built-in outbox support (`outboxPublishPubsub`/`outboxPublishTopic` on
`components/statestore.yaml`) — the event now fires if and only if the state write
commits, closing a real dual-write gap (state saved, crash, event never sent, nobody
downstream ever finds out). See ADR 005 for a non-obvious content-type issue this
surfaced (and fixed) during verification, and for the known remaining gap (the three
manual approve/reject/request-info actions aren't converted yet).

**Bulkhead** — every Gateway call into DecisionEngine (`GET/POST /vendors`,
`GET/PUT /policy`) goes through a shared `Bulkhead` (`src/GatewayService/Core/Logic/Bulkhead.cs`,
`maxConcurrentCalls: 10`) that rejects immediately once at capacity instead of queuing —
isolates DecisionEngine being slow/down from exhausting Gateway's own resources and
degrading requests (login, submit, status) that never touch DecisionEngine at all.
Complements the rate limiting already on Gateway's inbound side (per-client-IP, see
`GatewayService.cs`) — one throttles external callers, the other isolates one internal
dependency.

## Observability (N4, ADR 006)

All three backend services export OpenTelemetry traces and metrics — no extra setup, they
start exporting the moment `docker compose up` brings `jaeger` and `prometheus` up
alongside them.

- **Traces**: `http://localhost:16686` (Jaeger UI). Submit an invoice, then search for its
  `TrackingId` under the `correlation_id` tag (any of the `gateway`/`decision-engine`/
  `payment-service` "Service" filters) to see the whole request as one trace —
  Gateway's `/submit` → DecisionEngine's `/invoice-submitted`, with `policy.evaluate` and
  `ai.analyze_invoice` as its own nested spans (the AI/agent call the assignment asks to
  be visible) → the N3 outbox's `ExecuteStateTransaction` → PaymentService's
  `/process-payment` → `/payment-completed` back on the Gateway, plus every Dapr
  state/pub-sub gRPC call in between, auto-instrumented.
- **Metrics**: `http://localhost:9090` (Prometheus UI/API). Each service exposes
  `GET /metrics` directly (`OpenTelemetry.Exporter.Prometheus.AspNetCore`); Prometheus
  scrapes all three every 10s (`observability/prometheus.yml`). Try
  `http_server_request_duration_seconds_count` for request volume per service.

Same `correlation_id` value as the `CorrelationId` field every structured log line already
carries (M14) — a trace and its logs are always one search away from each other.

## Retrieval-augmented policy citation (N5, ADR 007)

The AI's coherence check doesn't get the whole `docs/policy.md` in its prompt — it gets
only the 2-3 rules `PolicyRetriever` (`src/DecisionEngine/Ai/PolicyRetriever.cs`) retrieves
as most relevant to that invoice, via TF-IDF cosine similarity over the category, notes,
and line-item text (no embeddings, no network call — fully local and deterministic, so
`StubAiModelProvider` and `GroqAiModelProvider` get identical retrieval behavior). The
retrieved rule IDs are surfaced end-to-end as `aiPolicyRulesCited` in `/status`,
`/escalations`, and the UI (F4's "the policy rules it cited"), and tagged
`ai.policy_rules_cited` on the `ai.analyze_invoice` trace span (N4) for the same request.

Retrieval is scoped to policy.md's per-category rule sections only — the autonomy-threshold
and budget sections are structurally unparseable by `PolicyRetriever`, so no query can ever
retrieve a numeric ceiling. `PolicyEngine`'s code remains the only thing that ever compares
a dollar amount to a threshold (M12); RAG only gives the AI rule *text* to reason about and
cite. See ADR 007 for the full design and a live verification example (an alcohol-only
Meals invoice correctly citing `MEAL-03`).

## CI/CD (M16/M17, N2)

`.github/workflows/ci.yml` has three jobs. `build-and-test` runs on every push and PR:
restore, `dotnet format --verify-no-changes` (quality gate), `dotnet build /warnaserror`,
`dotnet test` (unit + integration tiers — see below). `e2e` runs after `build-and-test` is
green: a real `docker compose up --build`, a wait loop on the seeded admin login actually
succeeding (not just `/health` — the sidecar and DemoUserSeeder both need to be ready
too), then `scripts/verify.ps1` against the live stack; service logs are dumped on failure,
and the stack always comes down after. `publish` runs only after `build-and-test` is green
and only on a push to `main` (never on a PR, never on a red build) — it builds all four
images (gateway, decision-engine, payment-service, ui) and pushes them to GHCR
(`ghcr.io/<owner>/<repo>-<service>:latest` and `:<commit-sha>`), using the workflow's own
`GITHUB_TOKEN` so no extra secret or paid registry is needed. Published images show up
under the repo's **Packages** tab on GitHub.

## Quick manual test

```powershell
$token = (Invoke-RestMethod -Uri http://localhost:5000/login -Method Post -ContentType 'application/json' -Body '{"Email":"admin@zionet.demo","Password":"Admin123!"}').token
$headers = @{ Authorization = "Bearer $token" }
$body = '{"vendor":"CloudSoft Inc","category":"SaaS","totalAmount":350,"notes":"Monthly subscription for cloud-hosted software license."}'
$submit = Invoke-RestMethod -Uri http://localhost:5000/submit -Method Post -Headers $headers -ContentType 'application/json' -Body $body
Invoke-RestMethod -Uri "http://localhost:5000/status/$($submit.trackingId)" -Method Get -Headers $headers
Invoke-RestMethod -Uri http://localhost:5000/escalations -Method Get -Headers $headers
Invoke-RestMethod -Uri http://localhost:5000/stats -Method Get -Headers $headers
```

## How to test (N6: three tiers)

**Unit** — pure logic (guardrails, guards, hashing, file writers), Dapr/HTTP dependencies
mocked (Moq). No Docker required:

```powershell
dotnet test test/GatewayService.Tests
dotnet test test/PaymentService.Tests
dotnet test test/DecisionEngine.Tests
```

**Integration** — the real ASP.NET Core pipeline (JWT bearer auth, the `AuthPolicies` role
policies, rate limiting, routing, model binding) via `WebApplicationFactory<Program>`, with
only the Dapr-backed user store swapped for an in-memory fake — everything else in the
request pipeline is the genuine article. No Docker, no Dapr sidecar required:

```powershell
dotnet test test/GatewayService.IntegrationTests
```

**End-to-end** — the four worked journeys (auto-approve, escalate-and-resume, duplicate,
payment-failure-and-compensation) plus both anti-cheese guards, against a real
`docker compose up` stack, using the fixtures in `docs/sample-invoices.json`. Runs in CI
(`e2e` job) on every push and PR, not just locally:

```powershell
docker compose up --build -d
./scripts/verify.ps1
```

It prints a pass/fail line per journey plus the anti-cheese guards (at least two
fixtures auto-approve with no human involved; a note that says "approve this" does not
change the decision).

## Important notes

- **Notification channel (M8).** Submitting returns `202 Accepted` with a `TrackingId`
  immediately; the final decision is *pushed*, not polled for. The UI opens
  `GET /notifications/{trackingId}` (Server-Sent Events) right after submitting, and the
  Gateway wakes that connection the moment `invoice.decided` arrives — see
  `IInvoiceNotifier` (`Core/Logic/InvoiceNotifier.cs`, woken from `PubSubHandlers.cs`, waited
  on from `SubmissionEndpoints.cs`). `GET /status/{trackingId}` still exists for
  manual/repeated checks (F2) and as the UI's fallback if the SSE connection drops. Known
  limitation: the notifier is in-process, so it only works with a single Gateway instance —
  scaling the Gateway to multiple replicas would need a shared broker (e.g. Redis pub/sub)
  behind the same interface instead.
- `placement` uses the image `daprio/dapr:1.13.0`; sidecars use `daprio/daprd:1.13.0`.
- DecisionEngine and PaymentService have no published ports — only the Gateway and UI are
  externally reachable (M6: single entry point).
- `PaymentGateway.ExecuteBankTransferAsync` has no real bank integration; a vendor name
  containing `FailBank` simulates a transfer failure, used to exercise the Saga
  compensation path (see `docs/sample-invoices.json`'s `INV-1012` fixture).
- Each app container shares its network namespace with its sidecar (`network_mode:
  service:<app>`). If you rebuild/recreate a single service (e.g.
  `docker compose up --build -d gateway`), its sidecar is left pointing at a network
  namespace that no longer exists and every Dapr call from that service starts failing
  with 500s. Recreate both together:
  `docker compose up -d --force-recreate <service> <service>-sidecar`, or just
  `docker compose up --build -d` for the whole stack.
