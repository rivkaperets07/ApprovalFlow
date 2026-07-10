# ADR 005: Outbox Pattern and Bulkhead Isolation (N3)

**Date:** July 2026
**Status:** Accepted

## Context

Two gaps remained in the system's resilience story, both flagged under N3 ("outbox pattern
and bulkhead / throttling"):

1. **Dual-write risk.** DecisionEngine's automatic decision path saved the invoice's
   Approved/Escalated status with `SaveStateAsync`, then separately published
   `invoice.decided` with `PublishEventAsync`. These are two independent calls to two
   different Dapr building blocks — a crash (or a transient network blip) between them
   leaves the state committed but the event never fired. Nothing downstream would ever
   find out: the Gateway's escalation index goes stale, and anyone holding an open
   `GET /notifications/{trackingId}` connection (M8) waits forever for an event that
   already isn't coming. The three manual approve/reject/request-info actions in
   `ApproverEndpoints.cs` have the identical shape.

2. **No isolation on the Gateway→DecisionEngine call path.** Gateway proxies four
   operations to DecisionEngine over Dapr service invocation (`GET/POST /vendors`,
   `GET/PUT /policy`). If DecisionEngine is slow or overloaded, nothing stopped every
   concurrent caller from piling into that same dependency — exhausting Gateway's own
   thread/connection pool and degrading unrelated requests (login, submit, status) that
   never touch DecisionEngine at all. Rate limiting (already in place, `GatewayService.cs`)
   throttles *inbound* traffic per client; it does nothing to cap *outbound* concurrency
   into one specific downstream dependency — a different failure mode, needing a different
   mechanism.

## Decision

### Outbox

Dapr has a built-in outbox implementation (state-store-level, since 1.12) rather than a
hand-rolled outbox table + relay process: configure the state store component
(`components/statestore.yaml`) with `outboxPublishPubsub` / `outboxPublishTopic`, then
call `DaprClient.ExecuteStateTransactionAsync` instead of `SaveStateAsync` +
`PublishEventAsync`. Dapr writes the state and publishes the event atomically — the event
fires if and only if the write commits.

Applied to DecisionEngine's automatic decision path
(`InvoiceEndpoints.EvaluateAndPublishAsync`), reusing the existing `invoice.decided` topic
name so the one subscriber (`PubSubHandlers.HandleInvoiceDecidedIndexAsync`) needed no
topic-side changes. It does need a body-shape change: outbox publishes the *state item
itself* (the full `InvoicePayload`), not the smaller `DecisionResult` DTO the old
`PublishEventAsync` call sent. Safe here because that handler only ever reads
`TrackingId` off the event and re-fetches the real invoice for everything else — verified
by reading the handler before relying on it, not assumed.

**A real, non-obvious problem surfaced during verification and is worth recording:** an
outbox-published CloudEvent doesn't declare `datacontenttype: application/json` (Dapr has
no way to know the raw state bytes it's forwarding are JSON), so ASP.NET Core's default
`[FromBody]` JSON model binder rejected it with 415 even though the body genuinely was
JSON — the endpoint executed, model binding failed inside it. Confirmed by reproducing
against the real `docker compose` stack (a submitted invoice never reached the escalation
index) before concluding it was fixed, not just build-succeeded. Fixed by having
`HandleInvoiceDecidedIndexAsync` read the request body manually
(`JsonDocument.ParseAsync`) and pull out `TrackingId`, instead of a typed `[FromBody]`
parameter — content-type-agnostic, and the handler never needed the rest of the payload's
shape anyway.

**Scope, stated plainly:** only DecisionEngine's automatic path was converted. The three
manual actions in `ApproverEndpoints.cs` (approve/reject/request-info) still use the older
`SaveStateAsync` + `PublishEventAsync` pair — same dual-write risk, not yet closed. Left
as a known gap rather than converted under time pressure: the automatic path is the
highest-value one (every invoice goes through it, unattended), and extending the same,
now-proven pattern to the other three call sites is mechanical repetition, not a new
decision.

### Bulkhead

Added a small hand-rolled `Bulkhead` class (`src/GatewayService/Core/Logic/Bulkhead.cs`) —
a `SemaphoreSlim`-backed concurrency cap that **rejects immediately**
(`BulkheadRejectedException`) rather than queuing once the cap is hit. Queuing is the
wrong default for a bulkhead: a caller parked behind an unbounded queue for an overloaded
dependency still ties up a thread while it waits, which is exactly the resource exhaustion
a bulkhead exists to prevent. Registered as a single singleton
(`maxConcurrentCalls: 10`) shared across all four Gateway→DecisionEngine call sites
(`SubmissionEndpoints.GetVendorsAsync`, `AdminEndpoints.CreateVendorAsync` /
`GetPolicyAsync` / `UpdatePolicyAsync`) — one shared cap, not one per endpoint, because
they all funnel into the same downstream dependency; that dependency's capacity is the
actual resource being protected, not any individual endpoint's.

A rejected call returns the same 503 an unreachable DecisionEngine would (the caller's
correct response — retry later — is identical either way), but logs a distinct message
("bulkhead is already at capacity") so this is diagnosable apart from a genuine outage.

No new dependency (Polly, etc.) for this — the mechanism is small enough (~40 lines) that
hand-rolling it was less overhead than adding and learning a library for one call site's
worth of use, and it's unit-tested directly (`BulkheadTests.cs`) rather than trusted to a
library's own test suite.

Rate limiting (already in `GatewayService.cs`, per-client-IP fixed window, pre-dates this
ADR) is the other half of "bulkhead / throttling" — inbound throttling and outbound
bulkheading are complementary, not redundant: one protects Gateway from too many external
callers, the other protects Gateway from one slow internal dependency.

## Consequences

- **Positive:** The highest-traffic decision path (every invoice, unattended) can no
  longer silently strand a decision with no event — verified against a real
  `docker compose` stack, not just assumed from the Dapr docs.
- **Positive:** DecisionEngine being slow/down no longer risks cascading into unrelated
  Gateway requests (login, submit, status) that never call it.
- **Negative/known gap:** the three manual approve/reject/request-info actions
  (`ApproverEndpoints.cs`) still have the pre-outbox dual-write shape. Lower risk in
  practice (a human is watching the UI response for that specific action), but not
  provably closed the way the automatic path now is.
- **Negative/trade-off:** a `Bulkhead`-rejected request surfaces as the same 503 as a
  genuine DecisionEngine outage to the caller (distinguishable only in Gateway's own
  logs) — acceptable since the correct client behavior (retry later) is identical either
  way, but worth knowing if debugging from the outside in.
- **Trade-off documented in code, not just here:** `outboxPublishTopic` is one topic per
  state store component; a transaction needing to fan out to two different topics
  (`invoice.decided` always, `invoice.approved` conditionally, both from the same
  automatic-decision write) would need either a second state store component or the
  `outbox.projection` per-operation metadata override. Not needed yet — `invoice.approved`
  is still published as a plain, subsequent `PublishEventAsync` after the outbox
  transaction commits, which only weakens the guarantee for that second event, not the
  state-write-vs-notification gap the outbox was added to close.
