# ApprovalFlow — Architecture

## System Overview

```mermaid
flowchart LR
    UI["UI (static HTML)"] -->|HTTP| Gateway
    Gateway["Gateway Service<br/>(single external entry point,<br/>rate-limited)"] -->|pub/sub: invoice.submitted| Bus((Dapr Pub/Sub<br/>Redis))
    Bus -->|invoice.submitted| Decision["Decision Engine<br/>(VendorDirectory lookup → AI coherence check → PolicyEngine)"]
    Decision -->|AI coherence check| AI["IAiModelProvider<br/>Groq / Stub"]
    Decision -->|invoice.decided| Bus
    Decision -->|invoice.approved| Bus
    Bus -->|invoice.approved| Payment["Payment Service<br/>(Saga: reserve → transfer → compensate)"]
    Bus -->|invoice.decided| Gateway
    Gateway <-->|state| Store[("Dapr State Store<br/>Redis")]
    Decision <-->|state: invoice, TripId budgets| Store
    Payment <-->|state: claim, reservation, processed| Store
    Decision -.->|secret: GROQ_API_KEY| Secrets[("Dapr Secret Store")]
```

Every service talks to the others only through its Dapr sidecar — pub/sub for
`invoice.submitted` / `invoice.decided` / `invoice.approved`, and the shared Redis-backed
state store for durable invoice records, idempotency claims, and per-`TripId` budgets.
The Gateway is the only service with a published port; DecisionEngine and PaymentService
are reachable only via their sidecars (M6).

## Sequence: Invoice Submission → Decision

```mermaid
sequenceDiagram
    actor Submitter
    participant GW as Gateway
    participant Bus as Dapr Pub/Sub
    participant DE as DecisionEngine
    participant AI as IAiModelProvider
    participant PE as PolicyEngine
    participant Store as State Store

    Submitter->>GW: POST /submit {Vendor, TotalAmount, Category, Notes}
    GW->>Store: HasBeenSubmittedAsync(trackingId)?
    alt already submitted
        GW-->>Submitter: 202 Accepted (short-circuited, no re-publish)
    else new submission
        GW->>Store: SaveState(invoice, Status=Pending)
        GW->>Bus: publish invoice.submitted
        GW-->>Submitter: 202 Accepted {trackingId}
        Bus->>DE: invoice.submitted
        DE->>PE: ResolveVendorCategory(vendor)
        PE-->>DE: category (VendorDirectory lookup —<br/>GLOBAL-VENDOR already guarantees it's known)
        DE->>AI: AnalyzeAsync(invoice, category)
        AI-->>DE: AiAnalysisResult {confidence, reasoning, metadata}
        DE->>PE: EvaluateAsync(invoice, category, aiResult)
        Note over PE: Risk threshold → confidence →<br/>category ceiling (flat / Meals formula / Travel cumulative)
        PE-->>DE: RouterDecision {Approved | Escalated, Reason}
        DE->>Store: SaveState(invoice, Status, Reason, DecidedBy=AI)
        DE->>Bus: publish invoice.decided
        opt approved
            DE->>Bus: publish invoice.approved
        end
    end
    Submitter->>GW: GET /status/{trackingId}
    GW->>Store: GetState(invoice)
    GW-->>Submitter: Status, Reason, DecidedBy
```

Escalated items surface on `GET /escalations`; an approver calls `POST /approve/{id}` or
`POST /reject/{id}` on the Gateway, which resumes the workflow exactly where the AI left
it off — the underlying invoice record and its `TrackingId` never change, so nothing about
the pause is special-cased in the payment flow that follows.

## Payment Flow & Compensation (Saga)

```mermaid
flowchart TD
    A[invoice.approved event] --> B{Already processed?<br/>GetState processedKey}
    B -->|yes| Z1[Duplicate — ignored]
    B -->|no| C{Claim invoice<br/>ETag-conditional write}
    C -->|lost race| Z1
    C -->|won claim| D[ReserveBudget]
    D -->|insufficient| E1[Abort — release claim]
    D -->|reserved| F[ExecuteBankTransferAsync]
    F -->|success| G[Mark processed=true<br/>Delete reservation<br/>Release claim]
    G --> H[PaymentResult Success]
    F -->|failure| I[Compensate:<br/>ReleaseBudget]
    I --> J[Delete reservation<br/>Release claim]
    J --> K[PaymentResult Failed<br/>— eligible for retry]
```

The claim (step C) is what makes this safe under Dapr's at-least-once pub/sub delivery:
two concurrent deliveries of the same `invoice.approved` event race on the same
ETag-guarded write, and only one can win it. A **failed** transfer releases the claim, so
a legitimate retry later is not permanently blocked — only a **completed** payment is
permanently deduped (`processed` key). See ADR 002.

---

# Architecture Decision Records (ADRs)

Each key decision is captured as a standalone ADR (context → decision → consequences)
under [docs/adr/](adr/):

| ADR | Decision |
| --- | --- |
| [ADR 001](adr/001-dapr-distributed-infrastructure.md) | Adoption of Dapr for distributed infrastructure (pub/sub, state, secrets via sidecars) |
| [ADR 002](adr/002-saga-payment-flow.md) | Saga pattern for the payment flow (reserve → transfer → compensate, idempotent claiming) |
| [ADR 003](adr/003-policy-engine-and-swappable-ai-provider.md) | Deterministic PolicyEngine with a swappable AI provider (the numbers are config, the gate is code) |
| [ADR 004](adr/004-file-based-policy-configuration.md) | File-based dynamic policy configuration (bind mount + reloadOnChange, vs. Dapr Configuration) |