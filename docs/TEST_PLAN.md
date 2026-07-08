# ApprovalFlow - Test Plan

## 1. Automated Unit Testing Strategy
The system logic is isolated within the `DecisionEngine` and `PaymentService`. We will use unit tests to verify:
* **Policy Enforcement**: Mocking `InvoicePayload` to ensure hard stops (e.g., missing receipts) trigger `human_review` status.
* **Autonomy Logic**: Testing category-specific thresholds (SaaS $500, Meals $75/attendee).

## 2. Integration & Saga Testing Scenarios (The Critical Path)

| Test ID | Scenario | Input Data | Expected Result |
| :--- | :--- | :--- | :--- |
| **TC-01** | Standard SaaS Approval | SaaS, $200, Conf: 0.9 | `auto_approve` -> `PaymentService` |
| **TC-02** | Meal Policy Limit | Meals, 2 attendees, $140 | `auto_approve` (Within $150 limit) |
| **TC-03** | Missing Receipt Stop | Hardware, $50, No Receipt | `human_review` (Hard Stop Rule) |
| **TC-04** | Saga Compensation | Valid Invoice, Bank API Fail | `Rollback` -> Budget Released |

## 3. Manual Testing Steps for Local Environment
Since the system is containerized, testers should follow these steps:
1. Run `docker compose up`.
2. Send a POST request to `localhost:5000/api/invoices` with a JSON payload.
3. Observe logs in the console to verify event publication (`invoice.submitted`).
4. Verify the `DecisionEngine` log correctly identifies the approval status.
5. In the event of a simulated bank failure, verify the `PaymentService` log shows "SAGA ROLLBACK TRIGGERED".