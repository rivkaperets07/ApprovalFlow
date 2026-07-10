# ADR 006: OpenTelemetry — Distributed Tracing and Metrics (N4)

**Date:** July 2026
**Status:** Accepted

## Context

M14 already gives every log line a `CorrelationId` (the invoice's `TrackingId`), so one
request's log lines can be found across all three services. That answers "what happened,"
but not "how long did each step take, and in what order" — for that you'd need to
manually reconstruct a timeline from timestamps across three separate log streams. N4
asks for the tool built for exactly this: one distributed trace per request, with the
AI/agent call visible as its own span, plus metrics — both free and local, per the
assignment's constraints.

## Decision

### Instrumentation

Each service (`GatewayService`, `DecisionEngine`, `PaymentService`) adds the OpenTelemetry
.NET SDK: ASP.NET Core instrumentation (auto-spans every incoming HTTP/pub-sub request),
HTTP client instrumentation (covers `GroqAiModelProvider`'s outbound calls when
`AiProvider=Groq`), and gRPC client instrumentation — the one that matters most here,
since `Dapr.Client` talks gRPC to its sidecar for every state, pub/sub, and service-
invocation call. Without it, the trace would stop at each service's boundary; with it,
every `SaveState`/`GetState`/`PublishEvent`/`ExecuteStateTransaction`/service-invocation
call shows up as its own span, and — because the sidecar forwards the W3C trace context
Dapr already propagates — a pub/sub delivery to a *different* service continues the same
trace instead of starting a new one.

Two custom spans in DecisionEngine (`DecisionEngineTelemetry.ActivitySource`,
`InvoiceEndpoints.cs`) around the parts auto-instrumentation can't see because they're
pure in-process code, not a network call the SDK can hook into: `policy.evaluate` (the
whole route-and-decide step) and, nested inside it, `ai.analyze_invoice` — the assignment
calls out "the agent's model/tool calls" specifically, and this is that call, whether it's
resolving through `StubAiModelProvider` (no network call at all, so nothing else would
ever make it show up) or the real `GroqAiModelProvider` (where the nested HTTP span from
instrumentation shows the actual model latency).

### Correlating a trace with the logs

Every span at each service's entry point (`SubmissionEndpoints.SubmitAsync`,
`InvoiceEndpoints.HandleInvoiceSubmittedAsync`, `PaymentEndpoints.ProcessPaymentAsync`) is
tagged `correlation_id` = the same `TrackingId` M14's `BeginScope(CorrelationId)` already
puts on every log line for that request. This is what "stitching... via the correlation
id" means in practice: the log line and the trace share a value, so either one is the way
to find the other — search `CorrelationId` in the logs, or `correlation_id` in Jaeger,
and land on the same request.

### Backend: Jaeger + Prometheus, not a hand-rolled collector

Jaeger's all-in-one image accepts OTLP directly (gRPC on 4317, HTTP on 4318) since 1.35 —
no separate OTel Collector needed, one container is both the ingest point and the UI
(`http://localhost:16686`). Prometheus scrapes each service's `GET /metrics`
(`OpenTelemetry.Exporter.Prometheus.AspNetCore`) on its own 10s schedule
(`observability/prometheus.yml`) rather than the services pushing — a Prometheus outage
can never block a request the way a synchronous push exporter could. Both run purely
local, free, no license, consistent with the rest of this stack.

### Verified, not assumed

Rebuilt the full `docker compose` stack and ran `scripts/verify.ps1`'s INV-1012 journey
(the one that actually reaches all three services: submit → auto-approve → payment
attempt → compensation), then queried Jaeger's HTTP API directly
(`/api/traces?service=gateway&tags={"correlation_id":"INV-1012"}`) rather than trusting
the wiring compiled and assuming it worked. One trace came back containing, among the
Dapr-auto-instrumented spans, both `policy.evaluate` and `ai.analyze_invoice`, correctly
nested under `POST /invoice-submitted`, itself linked to `POST /submit` on the Gateway
side and `POST /process-payment`/`POST /payment-completed` on the PaymentService side —
one connected trace across all three services, not three disconnected ones. Queried
Prometheus's `/api/v1/targets` the same way and confirmed all three scrape jobs `"health":
"up"` with real counter values, not just that the endpoint returns 200.

## Consequences

- **Positive:** a request's full cross-service timeline — including exactly how long the
  AI step took relative to everything else — is now one view in Jaeger instead of a
  manual reconstruction from three log streams.
- **Positive:** the gRPC client instrumentation needed for Dapr calls to show up came
  free with propagation already working correctly (Dapr forwards W3C trace context on its
  own), so no manual trace-context plumbing was needed across the pub/sub hops.
- **Negative/trade-off:** two more containers (`jaeger`, `prometheus`) in `docker compose
  up` — longer cold start, two more things that could fail to start cleanly. Both are
  best-effort from the app's point of view: `AddOtlpExporter`'s batched processor doesn't
  block or fail a request if Jaeger is slow/unreachable, so their absence degrades
  observability, never correctness.
- **Negative/known gap:** custom spans exist only in DecisionEngine (the one place with
  genuine non-network work worth naming — the AI call). Gateway and PaymentService rely
  entirely on auto-instrumentation, which is honestly sufficient for what they do (thin
  orchestration around network calls that already get their own spans) — but it does mean
  the trace's non-Dapr spans are asymmetric across services, worth knowing rather than
  assuming uniform depth everywhere.
