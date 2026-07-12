# Agent Usage Log

This project was built with Claude Code (Anthropic) as a pair-programming tool
throughout. This log states plainly how it was used, so anyone reading the
repo — or grading it — knows exactly where the line between human decisions
and AI-assisted implementation sits, rather than guessing from commit
messages alone.

## Tooling

- **Claude Code**, primarily on the Claude Sonnet 5 model, with a small
  number of sessions on Claude Fable 5. Every commit that includes AI-assisted
  work carries a `Co-Authored-By: Claude ...` trailer; commits with no such
  trailer were hand-written. All commits are authored by the project owner —
  Claude never committed or pushed unsupervised.
- No other AI coding tool was used.

## How it was used

The working pattern was consistent across the whole build: the developer set
direction, priorities, and made every architectural call (which pattern to
use for the payment saga, the autonomy-ceiling posture in
`docs/PRODUCT-DILEMMA.md`, what counted as "done"); Claude Code implemented
against that direction, one feature at a time, and the developer reviewed and
tested before accepting.

A rule that held for the whole project: **a green build is not evidence of
correctness.** Features that touch the running system — Dapr pub/sub,
container startup, live decision paths — were verified against a real
`docker compose up` stack, not just `dotnet build`/`dotnet test`, before being
considered finished. Several real bugs were only ever found this way (a
cross-service state-sharing bug caught by an early version of
`scripts/verify.ps1`; an outbox-published event getting rejected with a 415
because of a CloudEvents content-type mismatch, only visible by submitting a
real invoice and checking it never reached the escalation queue). Test data
written into tracked config files during live verification was reverted
afterward as a matter of course.

## Timeline

| Date | Focus | Representative commits |
|---|---|---|
| 2026-07-08 | Core platform: three services wired through Dapr, the deterministic policy engine, the payment saga with idempotent claiming, rate limiting, the escalation queue and dashboard, the four worked-journey fixtures, `ARCHITECTURE.md`, OpenAPI, `verify.ps1`, and the first pass of hardened guardrails against `docs/policy.md`. | `b5cf9b4`, `d6eb8f8`, `f829469`, `e4ad34e`, `9a60e5a`, `c5f4fa9` |
| 2026-07-09 | Correctness and process hardening: submission idempotency and structured logging, send-back-for-info with a full audit trail, reworking the AI's role from classifying a category to judging coherence against a category resolved deterministically, restructuring the ADRs into one file per decision, and adding role-based JWT authentication. | `9e7859c`, `a91fbfe`, `3963db1`, `87ccc83`, `cf1aa0e` |
| 2026-07-10 | Reliability and observability: the Dapr outbox pattern and a bulkhead on the Gateway→DecisionEngine path, OpenTelemetry tracing and metrics wired to Jaeger and Prometheus, retrieval-augmented policy citation (`PolicyRetriever`) scoped so it can never surface a numeric ceiling, a dedicated integration-test tier, and a full sweep to make code comments read as plain engineering prose rather than referencing this project's own internal grading checklist. | `17cef67`, `3872857` |

## A specific, honest note on the ADRs

The seven records under `docs/adr/` were drafted collaboratively with Claude
Code during the 2026-07-09 and 2026-07-10 sessions, then reviewed by the
developer. The reasoning in each one reflects real decisions and trade-offs
made along the way — but the prose itself was AI-assisted, not written solo.
Anyone defending these decisions in person should treat that reasoning as a
starting point to make their own, not a script.

## Corrections and problems found along the way

Not everything worked on the first try. Listed here plainly, since a log that
only records what went right isn't an honest one.

- **Docker Desktop stuck containers, twice.** A single-file bind mount (first
  `vendor-directory.json`, later `observability/prometheus.yml`) left a
  container permanently stuck in `Created`, unkillable even with
  `docker rm -f`, requiring a Hyper-V VM restart to clear. The fix (mount the
  containing directory, not the file) was learned from the first incident but
  not applied preemptively to the second bind mount added later — it had to
  be hit again before it became a habit.
- **Wrong assumption about the Docker Desktop backend.** Early troubleshooting
  assumed a WSL2 backend; the developer corrected this to Hyper-V, which
  changes the actual remediation (Hyper-V Manager, not `wsl --shutdown`).
- **A green build was treated as proof more than once, and had to be
  re-corrected into a standing rule.** The rule stated above ("a green build
  is not evidence of correctness") exists because it was violated first —
  changes were reported as working based on `dotnet build`/`dotnet test`
  succeeding, before the practice of verifying against a live
  `docker compose up` stack was firmly established.
- **A real bug that only live testing caught:** the Dapr outbox publishes a
  CloudEvent without a `datacontenttype`, which made ASP.NET Core's
  `[FromBody]` binder reject it with a 415 — the endpoint executed, but model
  binding failed inside it. Invisible to any unit test; only found by
  submitting a real invoice and noticing it never reached the escalation
  queue.
- **A real bug in `PolicyRetriever`'s retrieval:** without word stemming,
  "subscription" (in a submitter's notes) and "Subscriptions" (in the policy
  rule text) scored as unrelated tokens, so retrieval silently returned
  nothing. Caught by a failing test, not by inspection.
- **Test data left in tracked config files after live verification.**
  `vendor-directory.json` and `policies.json` got test entries written into
  them during manual testing against the live stack; these had to be
  reverted before committing, which is why checking `git status` on both
  files became a standing step after any live test.
- **Code comments leaked this project's internal grading-checklist IDs**
  (`N1`, `M12`, `F4`, and similar) throughout the codebase — the developer
  flagged this directly: a comment referencing an ID from a rubric the reader
  has never seen isn't documentation, it's an assumption about the reader's
  context that a real project can't make. Fixing it took a dedicated sweep
  across 41 files, rewriting each comment into plain prose while carefully
  leaving the *business* policy's own rule IDs alone (`GLOBAL-FRAUD`,
  `MEAL-03`, `SAAS-01`, etc. — real domain vocabulary from `docs/policy.md`,
  not grading jargon, and easy to confuse with it at a glance).
- **`main` was allowed to fall behind the actual state of the project.** By
  the time a full requirement-by-requirement audit was done against the
  assignment brief, `main` was a full day and several commits behind: it had
  no authentication at all, none of the outbox/bulkhead/OpenTelemetry/RAG/
  integration-test work, and was missing the send-back-for-info flow and the
  audit-trail endpoint. Nothing was technically broken — it just hadn't been
  merged — but since the brief states evaluation happens on `main`, this was
  the single largest risk found in the whole project, and it was only
  surfaced by deliberately auditing against the brief rather than trusting
  that recent work would naturally make it there.
- **An unrelated file got quietly modified by tooling, not by intent.**
  Running `dotnet build`/`dotnet test` after merging dropped the
  `GatewayService.IntegrationTests` project reference from
  `ApprovalFlow.sln` on disk. Caught by treating `git status` as a checkpoint
  after routine commands, not just after edits.

See also the ADR-authorship note above — the same "don't just trust it,
check" standard applies there too.

## What this log is not

It is not a line-by-line transcript, and it does not claim precise attribution
below the commit level — inside a single commit, some lines were typed by the
developer and some by Claude Code, and the two were not tracked separately.
What can be stated reliably is what's above: which days covered which work,
which commits carry AI co-authorship, and the verification discipline applied
throughout.
