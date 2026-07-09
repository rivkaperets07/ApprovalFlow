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
- **xUnit + Moq** for automated tests.
- Plain HTML/JS for the UI — no frontend framework/build step.

## Services

| Service | Responsibility |
| --- | --- |
| `src/GatewayService` | Single external entry point (rate-limited). Accepts submissions, exposes status/escalations/stats, forwards manual approve/reject. |
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
trail), or `admin` (everything). `POST /token` issues a self-signed demo token for a chosen
role — deliberately with no password check, since the demo demonstrates role-based
authorization wiring, not identity management. The UI has a role picker and signs in as
`admin` on load so one page can drive all personas; switch roles to see 403s on
out-of-role actions. Dapr-sidecar-delivered routes (`/payment-completed`,
`/invoice-decided-index`) stay anonymous by design — the sidecar carries no JWT and those
routes are not part of the public surface. The signing key is `JWT_SIGNING_KEY`
(`.env.example`); the checked-in fallback is for local demo only.

## Quick manual test

```powershell
$token = (Invoke-RestMethod -Uri http://localhost:5000/token -Method Post -ContentType 'application/json' -Body '{"Role":"admin"}').token
$headers = @{ Authorization = "Bearer $token" }
$body = '{"vendor":"CloudSoft Inc","category":"SaaS","totalAmount":350,"notes":"Monthly subscription for cloud-hosted software license."}'
$submit = Invoke-RestMethod -Uri http://localhost:5000/submit -Method Post -Headers $headers -ContentType 'application/json' -Body $body
Invoke-RestMethod -Uri "http://localhost:5000/status/$($submit.trackingId)" -Method Get -Headers $headers
Invoke-RestMethod -Uri http://localhost:5000/escalations -Method Get -Headers $headers
Invoke-RestMethod -Uri http://localhost:5000/stats -Method Get -Headers $headers
```

## How to test

Unit tests (no Docker required):

```powershell
dotnet test test/GatewayService.Tests
dotnet test test/PaymentService.Tests
dotnet test test/DecisionEngine.Tests
```

End-to-end verification of the four worked journeys (auto-approve, escalate-and-resume,
duplicate, payment-failure-and-compensation) against a running `docker compose` stack,
using the fixtures in `docs/sample-invoices.json`:

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
  `IInvoiceNotifier` in `GatewayEndpoints.cs`. `GET /status/{trackingId}` still exists for
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
