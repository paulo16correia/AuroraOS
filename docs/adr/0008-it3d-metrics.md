# Design 0008 — Operational metrics (It.3, fourth increment)

**Status:** Implemented · **Date:** 2026-08-23
**Depends on:** `docs/adr/0005-it3a-audit-hardening.md`, `docs/adr/0007-it3c-reconciliation.md`

## What is measured

The design 0001 list, turned into concrete counters:

| Metric | Type | Why it matters |
|---|---|---|
| `executionsByOutcome` | counter per outcome | A rise in `policy_denied` or `consent_denied` signals misconfiguration, or a client pushing |
| `pendingApprovals` | **real gauge** | Prompts waiting on a human; if it climbs and never falls, nobody is deciding |
| `consentLatency` (mean/max) | observation | How long a human decision takes |
| `idempotencyConflicts` | counter | A client reusing keys with different inputs |
| `executionsUnknown` | counter | Indeterminate effects; any value above zero deserves attention |
| `auditFailures` | counter | The security record is degrading |

## Two decisions

**The pending-approvals gauge is read from the database**, not counted in memory.
A created/resolved counter pair would look cheaper, but it drifts from the ledger
on every restart and expiry — and a gauge that lies about how many humans are
being waited on is worse than no gauge at all. The remaining counters are
process-lifetime, and `MetricsSnapshot` says so: a counter reset by a crash is
indistinguishable from a quiet period, and the reader has to know that.

**The endpoint is HTTP, not an MCP tool.** It sits at `GET /metrics`, behind the
same loopback and bearer guard as the MCP surface. Exposing this as a fourth tool
would give an untrusted reasoner a view of how often its own requests are being
refused — useful information for anyone probing where the policy boundary sits.
Metrics are for the operator.

Consent latency is computed from the approval record's own timestamps
(`created_at` → `decided_at`) rather than an in-memory stopwatch, so a decision
spanning a restart still counts. Negative values from clock skew are clamped to
zero instead of skewing the mean.

## Tests

9 new tests. Unit: an empty snapshot, counting per outcome, latency mean and max,
the max surviving a later smaller value, negative latency clamped to zero,
counters staying independent, and 200 concurrent updates losing nothing.
Integration: `/metrics` requires the bearer token, and reports real executions and
pending approvals.

## Deferred

Prometheus export; a proper latency histogram (mean and max only for now);
per-capability metrics; and persisting counters across restarts.
