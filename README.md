# ZioNet Approval Flow

This project implements an invoice approval flow using .NET microservices and Dapr.

## Main structure

- `docker-compose.yml` - orchestrates Redis, Dapr placement, services and official sidecars.
- `components/` - Dapr components for pub/sub and state store.
- `src/GatewayService` - receives invoices, saves state and publishes events.
- `src/DecisionEngine` - subscribes to `invoice.submitted`, auto-approves or escalates, and publishes decision events.
- `src/PaymentService` - processes `invoice.approved` and simulates payment.
- `src/UI` - static UI for submitting invoices and manual approve/reject actions.

## Requirements

- Docker Desktop
- Docker Compose

## How to run

```powershell
cd C:\Users\rivka\ZioNet-ApprovalFlow
docker compose up --build -d
```

## Check status

Ensure all services are running:

```powershell
docker compose ps
```

Follow logs if needed:

```powershell
docker compose logs -f gateway gateway-sidecar decision-engine decision-engine-sidecar payment-service payment-service-sidecar
```

## Quick test

Submit a test invoice to the gateway:

```powershell
$body = '{"vendor":"ACME","category":"Office","totalAmount":500,"notes":"Test invoice"}'
Invoke-RestMethod -Uri http://localhost:5000/submit -Method Post -ContentType 'application/json' -Body $body
```

Check its status:

```powershell
Invoke-RestMethod -Uri http://localhost:5000/status/<trackingId> -Method Get
```

## UI

Open in the browser:

- http://localhost:8080

## Important notes

- `placement` uses the image `daprio/dapr:1.13.0`.
- Service sidecars use `daprio/daprd:1.13.0`.
- Applications are configured to talk to their sidecar at `http://localhost:3500`.
- The Dapr component `components/pubsub.yaml` is configured to use Redis at `redis:6379`.
