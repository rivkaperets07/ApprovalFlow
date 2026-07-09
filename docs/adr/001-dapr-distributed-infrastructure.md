# ADR 001: Adoption of Dapr for Distributed Infrastructure

**Date:** July 2026
**Status:** Accepted

## Context
The assignment requires a microservices architecture (M3) that handles asynchronous events, durable state for Human-in-the-Loop pauses (M11), and cumulative state tracking for trip budgets (M5). Managing the SDKs and connections for message brokers (e.g., RabbitMQ/Kafka) and state stores (e.g., Redis) tightly couples the business logic to specific infrastructure.

## Decision
We adopted **Dapr (Distributed Application Runtime)** as a sidecar architecture for all microservices.
- We use Dapr **Pub/Sub** for decoupled asynchronous communication (`invoice.submitted`, `invoice.approved`).
- We use Dapr **State Store** to maintain the durable state of paused human reviews and to track cumulative `TripId` budgets for travel expenses.

## Consequences
- **Positive:** Business logic (C# Minimal APIs) is entirely decoupled from infrastructure. We can swap the underlying message broker without changing a single line of application code.
- **Positive:** Built-in resilience (retries, rate limiting) is handled by the sidecar.
- **Negative/Trade-off:** Adds operational overhead, requiring `docker-compose` to orchestrate both the application containers and their respective `daprd` sidecars.
