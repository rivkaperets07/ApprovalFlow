# Architecture Decision Records (ADRs)

This document captures the key architectural decisions made for the ApprovalFlow system, outlining the context, the decision, and the resulting consequences.

---

## ADR 001: Adoption of Dapr for Distributed Infrastructure
**Date:** July 2026
**Status:** Accepted

### Context
The assignment requires a microservices architecture (M3) that handles asynchronous events, durable state for Human-in-the-Loop pauses (M11), and cumulative state tracking for trip budgets (M5). Managing the SDKs and connections for message brokers (e.g., RabbitMQ/Kafka) and state stores (e.g., Redis) tightly couples the business logic to specific infrastructure.

### Decision
We adopted **Dapr (Distributed Application Runtime)** as a sidecar architecture for all microservices. 
- We use Dapr **Pub/Sub** for decoupled asynchronous communication (`invoice.submitted`, `invoice.approved`).
- We use Dapr **State Store** to maintain the durable state of paused human reviews and to track cumulative `TripId` budgets for travel expenses.

### Consequences
- **Positive:** Business logic (C# Minimal APIs) is entirely decoupled from infrastructure. We can swap the underlying message broker without changing a single line of application code.
- **Positive:** Built-in resilience (retries, rate limiting) is handled by the sidecar.
- **Negative/Trade-off:** Adds operational overhead, requiring `docker-compose` to orchestrate both the application containers and their respective `daprd` sidecars.

---

## ADR 002: Saga Pattern for Distributed Transactions (Payment Flow)
**Date:** July 2026
**Status:** Accepted

### Context
The system must guarantee consistent outcomes across services, particularly in the payment flow. We must ensure there are no orphaned budget reservations or double payments if a downstream process fails (F3, M9). In a distributed environment, traditional ACID database transactions (like Two-Phase Commit) are too slow and create tight coupling.

### Decision
We implemented the **Saga Pattern** (Choreography via Dapr Pub/Sub) within the Payment Service.
Instead of locking databases, the workflow uses a sequence of local transactions:
1. **Execute:** `ReserveBudget` is called.
2. **Execute:** `ProcessBankTransfer` is attempted.
3. **Compensate:** If the bank transfer fails, a `ReleaseBudget` (Rollback) local transaction is triggered to free the funds.

### Consequences
- **Positive:** High availability and loose coupling. Services do not block each other waiting for locks.
- **Positive:** Fulfills the idempotency requirement by using the `TrackingId` as the Saga execution ID.
- **Negative/Trade-off:** Introduces *Eventual Consistency*. The system might briefly show funds as reserved before the compensation logic completes the rollback. This trade-off is widely accepted in highly scalable financial systems.