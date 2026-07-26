# ADR 008: Receipt-Photo-Only Submission — Local OCR + a Narrow AI Fraud Check (dev branch)

**Date:** July 2026
**Status:** Accepted — `dev` branch only, never `main`

## Context

The assignment states "No OCR required for invoices/expenses," and this
project deliberately built around that boundary: every field on `main` is
submitter-typed text, and the real defenses (`TrackingId` idempotency,
`RecentSubmissionGuard`, the global guardrails) are built to not depend on
any typed field being true (see `docs/PRODUCT-DILEMMA.md`'s "No document
verification" note). That note already anticipated the natural next step —
a photo upload, OCR extraction, and an auto-`NeedsInfo` path for an
illegible image — as a genuine scope change, not a small addition.

The submission deadline for the graded assignment has passed; `main` is
frozen. This work happens entirely on a `dev` branch, per the assignment's
own allowance for post-deadline work shown at the presentation but not
evaluated as part of the submission.

The goal, in the project owner's own words: let a submitter attach a photo
of the receipt so "people can't lie" about what they're expensing.

## Decision

### Photo replaces typed submission entirely — it is not an alternative

`POST /submit` now requires `ReceiptImageDataUri` and **rejects outright**
(400) any request that also supplies `Vendor` or `TotalAmount`. This was
deliberated explicitly: an earlier design let a submitter choose either
typed input or a photo, with the photo "winning" if both were present. That
was rejected — if typing is still available at all, anyone who wants to lie
simply keeps typing and never touches the verification path. A "people
can't lie" claim only holds if there is no way around it.

### Two components, two separate jobs, on purpose

- **`IReceiptOcrExtractor`** (`src/DecisionEngine/Ocr/`) — a local library
  (`Tesseract`, shelled out to via the CLI rather than bound as a native
  library, to avoid chasing `libtesseract`/`TESSDATA_PREFIX` paths inside a
  container), not an AI call, no network. It reads the photo and fills in
  the exact fields `main` already has: `Vendor`, `TotalAmount`, and, as an
  extension, `LineItems` and `Currency` when the receipt shows them clearly.
- **`IReceiptFraudDetector`** (`src/DecisionEngine/Ai/`) — a narrow AI call
  that does exactly one thing: judge whether the image reflects a genuine
  transaction or has been fabricated/tampered with. It never extracts a
  field and never touches `Vendor`/`TotalAmount`/anything else — that
  separation was the project owner's explicit framing, kept literally in
  the code, not just in prose. "Genuine" deliberately covers more than a
  photographed paper receipt: a born-digital e-invoice/PDF/exported
  screenshot from a real system is just as legitimate, and the prompt is
  written to judge tampering, not digital-vs-photographed (see the July 26
  revision below — an earlier version of this prompt conflated the two).

Both are registered via the same config-driven DI switch pattern as
`AiProvider` (`OcrExtractor`, `ReceiptFraudDetector` — independent of each
other and of `AiProvider`, so any combination can run/test), with
deterministic Stub implementations (the same "magic string" idiom as
`PaymentGateway`'s `"FailBank"` marker) as the CI/test default.

### Order of operations, and why `NeedsInfo` is reached without a human

In `InvoiceEndpoints.EvaluateAndPublishAsync`, OCR runs first — before
category resolution, since there is no vendor yet to look one up for:

1. **OCR fails** (no confident vendor + total) → `Status = NeedsInfo`,
   citing `GLOBAL-RECEIPT-UNREADABLE`. Neither the fraud detector, the
   existing text-coherence `IAiModelProvider`, nor `PolicyEngine` are ever
   invoked. This is a data-quality problem, not a fraud signal or a policy
   decision — it doesn't deserve a human's time, and resubmitting via the
   existing `POST /provide-info/{trackingId}` path (now accepting a
   re-attached `ReceiptImageDataUri`) is the fastest correct fix. This is a
   genuine extension of `main`'s `NeedsInfo` status: on `main`, only a human
   approver's `request-info` action ever produces it; here, the automatic
   path produces it directly. `PolicyEngine`/`RouterDecision` were
   deliberately left untouched to keep this possible — `RouterDecision`
   only ever models Approved/Escalated, so this short-circuit lives entirely
   in `InvoiceEndpoints`, one level up, rather than growing a third state
   into code whose whole job is staying a simple binary gate.
2. **OCR succeeds** → `invoice.Vendor`/`TotalAmount`/`LineItems`/`Currency`
   are set from the extraction, then the fraud detector runs:
   - **Suspicious** → `Escalated`, citing `GLOBAL-RECEIPT-FRAUD`. **Never
     auto-rejects, even at high confidence** — same posture as the existing
     `MEAL-03` rule ("the signal can misfire, a human keeps the final
     say"). Confirmed explicitly with the project owner rather than
     assumed: a more aggressive posture (auto-reject on high-confidence
     fraud, closer to how `GLOBAL-FRAUD`/`GLOBAL-DUP` already reject
     outright) was considered and set aside in favor of the more
     conservative one.
   - **Genuine** → the existing pipeline runs **completely unchanged**:
     `TryFastRejectOnGlobalGuardrails`, category resolution, the
     text-coherence AI call, `PolicyEngine.EvaluateAsync`. **No code inside
     `PolicyEngine.cs` changed at all.** It never learns an invoice came
     from a photo — a decimal is a decimal regardless of who or what typed
     it, and every ceiling check still runs exactly as before.

### Why `LineItems`, `Currency`, and `IsPremiumTravel`, and nothing else,
### were added to OCR's scope

Discussed explicitly: a receipt photo could yield far more than vendor and
total (date, tax breakdown, payment method, address...). Only extras with an
**existing** `PolicyEngine` consumer were added — `GLOBAL-MATH`/`MEAL-03` for
`LineItems`, `GLOBAL-FX` for `Currency`, `TRAVEL-03` for `IsPremiumTravel`
(a first/business-class fare is a fact printed on the ticket itself, read
the same way `MEAL-03`'s alcohol keywords are already read off `LineItems`).
Adding them costs nothing new in `PolicyEngine`; anything else would need
new guardrail logic to matter and was deliberately left out of this
increment.

**`TripId` is the deliberate exception.** Discussed explicitly and rejected
as an OCR target: which trip an expense belongs to is the submitter's own
business context, not a fact printed on any receipt — no photo of a taxi
ticket says "TRIP-42." It stays a plain, optional, submitter-typed field on
the submit form (the one exception to "everything comes from the photo"),
exactly as it already was on `main`.

## Consequences

- **M12 holds exactly as before.** `PolicyEngine` is untouched. Whether
  `Vendor`/`TotalAmount` arrived via typing (on `main`) or OCR (on `dev`),
  the only code that ever compares an amount to a ceiling is the same
  deterministic code it always was. Neither new component — OCR or the
  fraud detector — has a path to `Approved`; the fraud detector can only
  ever push toward `Escalated`, exactly like the text-coherence AI's
  confidence score always could.
- **Accepted gap, partially closed: `RecentSubmissionGuard`/`FraudGuard`
  still don't run at submit time.** Both key on `Vendor`/`TotalAmount` *at
  the moment of submission* — those fields don't exist yet on this branch
  (OCR runs later, in DecisionEngine, to keep `/submit` non-blocking per
  M8). `PolicyEngine`'s own `GLOBAL-VENDOR`/`GLOBAL-FX`/ceiling checks still
  run in full once OCR populates the fields, so this is a real but bounded
  gap, not a hole in M12.
  `DuplicateInvoiceGuard` (vendor+invoiceNumber+total) is in the same
  position — orphaned, not deleted, on this branch — but `GLOBAL-DUP`
  itself is **not** left as a gap: `DuplicatePhotoGuard`
  (`src/GatewayService/Core/Logic/DuplicatePhotoGuard.cs`) replaces it,
  keyed on an exact SHA-256 hash of `ReceiptImageDataUri` instead of any
  OCR'd field. This was a deliberate choice over extracting an invoice
  number by OCR and reusing `DuplicateInvoiceGuard` as-is: real informal
  receipts (a taxi ticket, a lunch receipt — the common case for expense
  reimbursement) often carry no fiscal identifier at all, only a small
  per-register/per-day counter that can legitimately repeat across two
  *different* genuine receipts from the same vendor — trusting it alone
  risks rejecting a real expense, not just catching a real duplicate. A
  photo's own bytes don't have that failure mode, and an exact hash never
  false-positives between two different photos. The accepted tradeoff:
  retaking a fresh photo of the same physical receipt produces different
  bytes and slips past this guard — a perceptual/fuzzy image hash
  (tolerant of recompression/crop/lighting) would close that, and is noted
  below as a known future extension rather than built now.
- **Migration cost, paid deliberately.** Because typed submission no longer
  exists at all on this branch, the four core worked-journey fixtures
  (`INV-1001`/`1003`/`1007`/`1012`) and `scripts/verify.ps1` had to be
  converted to submit via photo, using `StubReceiptOcrExtractor`'s `"OCR:"`
  fixture-marker convention to stay fully deterministic. Each journey's
  expected Approved/Escalated outcome is unchanged; only the wire shape of
  the submission changed.
- **OCR accuracy is genuinely limited.** `TesseractReceiptOcrExtractor`'s
  field parsing is plain heuristics on raw transcribed text (first line =
  vendor, the `$`-amount nearest a "total" keyword, etc.) — it will misread
  plenty of real-world receipt layouts, and `LineItems`/`Currency`
  extraction is best-effort on top of that. The safe failure direction is
  built in: an unconfident vendor/amount read fails closed to `NeedsInfo`
  rather than guessing, and an unread `LineItems`/`Currency` simply stays
  `null` rather than fabricating a value.
- **Fixed (2026-07-26): the fraud check used to conflate "digital" with
  "fake."** The original `IReceiptFraudDetector` prompt asked whether the
  photo looked like a genuine photographed/scanned paper receipt, versus
  fabricated or AI-generated. That framing assumed every legitimate expense
  started life on paper — but a real, born-digital e-invoice (e.g. an
  Israeli Tranzila-issued חשבונית מס, never printed or photographed at all)
  is legitimate precisely *because* it's digital-native, and scored
  Suspicious under the old prompt for a reason that had nothing to do with
  fraud. Confirmed live against a real such invoice before the fix.
  `GeminiVisionFraudDetector`/`GroqVisionFraudDetector`'s prompts now ask
  two separate questions instead of one: is this a legitimate document
  (photographed paper *or* a born-digital e-invoice/PDF/exported
  screenshot are equally legitimate), and does it show actual signs of
  tampering (inconsistent fonts, editing artifacts, mismatched layout) —
  looking clean and digitally rendered is explicitly called out as *not*
  itself a fraud signal.
- **OCR assumes Latin script and a `$` sign.** `TesseractReceiptOcrExtractor`
  only has the English trained data installed, and `AmountPattern` matches
  `$` specifically — confirmed live that a real Hebrew, ₪-denominated
  invoice fails OCR entirely (falls to `NeedsInfo`), not because the photo
  is unclear, but because the language/currency are outside what this
  increment was built for. Real fix would add `tesseract-ocr-heb` and a
  currency-symbol table beyond `$`.
- **Base64-in-`InvoicePayload` bloats every Redis record, pub/sub delivery,
  and outbox transaction** with the full receipt photo. Acceptable at demo
  scale; a real production system would use a dedicated blob store and
  reference it by URL instead. No blob-storage Dapr component exists in
  this project, so this is the pragmatic dev-branch choice, not the
  recommended production one.
- **No retry limit on the unreadable → retake loop.** A submitter (or a
  genuinely unphotographable receipt) could resubmit indefinitely without
  ever reaching a human. Low risk for a demo; a real version would escalate
  after N unclear attempts rather than looping forever.
- **`DuplicatePhotoGuard` is an exact hash, not a perceptual one.** Photographing
  the same physical receipt twice (different angle, lighting, crop) produces
  different bytes and a different hash, so it is not caught. Closing that
  gap needs a perceptual hash (e.g. a difference hash tolerant of minor
  visual variation) plus a similarity threshold — deliberately left as a
  future extension rather than built now, since it trades the exact hash's
  zero-false-positive guarantee for a tunable one.
