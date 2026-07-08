# Corporate Expense & Invoice Approval Policy

## 1. Overview & Governance
This document defines the automated expense and invoice approval policy for the enterprise. To ensure strict compliance, financial safety, and fraud prevention, our system implements a **Hybrid Router-Decides Architecture**. 

The system boundary ensures that while AI is leveraged for semantic analysis, data extraction, and contextual classification, **all financial ceilings and compliance rules are strictly enforced by deterministic code (C# Guardrails).** The system is provably incapable of over-stepping the autonomous thresholds defined below.

---

## 2. Global Guardrails (Deterministic Pre/Post-Filters)
These rules apply universally to all incoming invoices and expenses, regardless of their specific category. They are executed within the application code as hard boundaries.

* **GLOBAL-VENDOR:** Every vendor must exist within the master corporate registry. Transactions from unregistered or blacklisted vendors are blocked and escalated immediately.
* **GLOBAL-MATH:** A maximum variance of up to 2% or $10.00 (whichever is lower) between the calculated sum of line-items and the claimed receipt total is automatically tolerated. Any discrepancy above this threshold is blocked.
* **GLOBAL-FX:** Any transaction denominated in a foreign currency (non-USD) that exceeds an equivalent of $1,000.00 requires mandatory human review.
* **GLOBAL-FRAUD:** Duplicate submission protection is enforced. Multiple transactions featuring identical amounts from the same vendor within a rolling 24-hour window are systematically blocked to prevent double-payment.
* **GLOBAL-RECEIPT:** An itemized, line-by-line breakdown is strictly mandatory for any transaction exceeding $25.00.

---

## 3. Autonomous Escalation Logic (Fail-Fast Boundaries)
The system will immediately bypass AI evaluation or override AI approval recommendations, routing the invoice to a human auditor, under the following conditions:

* **RISK-THRESHOLD:** Any single invoice or expense with a `TotalAmount` exceeding **$5,000.00** is strictly stripped of autonomy and routed directly to human review.
* **CONFIDENCE-THRESHOLD:** If the AI Agent's classification or extraction confidence score drops below **0.80** (or **0.90** for specific high-risk categories), the transaction is flagged as volatile and escalated.

---

## 4. Category-Specific Policies & Autonomy Ceilings

### 4.1 SaaS (Subscriptions & Software Licenses)
* **Description:** Monthly/annual software-as-a-service fees, cloud infrastructure, and tool licenses.
* **Autonomy Ceiling:** Up to **$500.00** flat per invoice.
* **Minimum AI Confidence:** 0.80

### 4.2 Hardware & Equipment
* **Description:** Laptops, peripherals, office machinery, and physical tech assets.
* **Autonomy Ceiling:** Up to **$250.00** flat per invoice.
* **Minimum AI Confidence:** 0.85

### 4.3 Meals & Hospitality
* **Description:** Business entertainment, client dinners, and internal team-building meals.
* **Mandatory Rule:** Must explicitly contain an itemized list of all attendees. **Individual dining during corporate travel does NOT belong under this category.**
* **Autonomy Ceiling:** Dynamically calculated at **$75.00 multiplied by the number of verified attendees** extracted by the AI.
* **Minimum AI Confidence:** 0.90 (High-strictness classification).

### 4.4 Travel, Lodging & Personal Transit
* **Description:** Flights, hotels, car rentals, and incidental travel expenses.
* **Mandatory Rule:** Must explicitly map to a valid, pre-approved `TripId`.
* **Trip Budget Cap:** Maximum cumulative total of **$2,000.00** per `TripId` (tracked dynamically across line-items via the distributed State Store).
* **Daily Allowance (Per Diem):** Maximum of **$200.00 per day** per receipt. This allowance covers personal meals while traveling, airport transit, and hotel incidentals.
* **Minimum AI Confidence:** 0.85

### 4.5 Office Supplies & Operations (Extended Category)
* **Description:** General office stationary, printing supplies, and day-to-day workplace operational needs.
* **Autonomy Ceiling:** Up to **$150.00** flat per invoice.
* **Minimum AI Confidence:** 0.80

### 4.6 Marketing & Advertising (Extended Category)
* **Description:** Digital ad spend (Google Ads, Meta), promotional merchandise, and corporate event campaigns.
* **Autonomy Ceiling:** Up to **$1,500.00** extended ceiling (validated against corporate ad accounts).
* **Minimum AI Confidence:** 0.85

---

## 5. Architectural Compliance Mapping

| Policy Requirement | Enforcement Mechanism | Failure Mode | Target Audience |
| :--- | :--- | :--- | :--- |
| **Risk Containment** | Code-level `if (Amount > 5000)` check prior to AI inference. | Immediate Escalation | Executive Board / Risk Management |
| **Volatility Management** | Structural verification of `ConfidenceScore` in C# Service. | Immediate Escalation | AI Engineering / Auditors |
| **Dynamic Ceilings** | Programmatic calculation (`75 * AttendeesCount`) applied post-inference. | Immediate Over-rule | Financial Compliance |