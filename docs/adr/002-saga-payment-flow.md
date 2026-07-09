# ADR 002: Saga Pattern for Distributed Transactions (Payment Flow)

**Date:** July 2026
**Status:** Accepted

## Context
The system must guarantee consistent outcomes across services, particularly in the payment flow. We must ensure there are no orphaned budget reservations or double payments if a downstream process fails (F3, M9). In a distributed environment, traditional ACID database transactions (like Two-Phase Commit) are too slow and create tight coupling.

## Decision
We implemented the **Saga Pattern** (Choreography via Dapr Pub/Sub) within the Payment Service.
Instead of locking databases, the workflow uses a sequence of local transactions:
1. **Execute:** `ReserveBudget` is called.
2. **Execute:** `ProcessBankTransfer` is attempted.
3. **Compensate:** If the bank transfer fails, a `ReleaseBudget` (Rollback) local transaction is triggered to free the funds.

## Consequences
- **Positive:** High availability and loose coupling. Services do not block each other waiting for locks.
- **Positive:** Fulfills the idempotency requirement (M10) via `TrackingId`-keyed state: a
  short-lived ETag-conditional "claim" taken before any work starts (so two concurrent
  deliveries of the same event can't both proceed) and a permanent "processed" record
  written only after a real success (so a failed attempt can still be retried later).
- **Negative/Trade-off:** Introduces *Eventual Consistency*. The system might briefly show funds as reserved before the compensation logic completes the rollback. This trade-off is widely accepted in highly scalable financial systems.
