# ADR 001: Data-Driven Policy Engine

## Status
Accepted

## Context
We need to support changing expense thresholds without re-deploying code (F7).

## Decision
Implemented a JSON-based policy configuration loaded at runtime via IConfiguration. 
The PolicyEngine validates invoices against this dynamic schema.

## Consequences
- Flexibility: Thresholds can be updated via ConfigMap/Dapr.
- Maintenance: No code changes required for new categories.