# Design 0012 — Specification baseline and conformance

**Status:** Accepted · **Date:** 2026-08-23
**Supersedes:** the "reference only" status of `docs/` recorded in `docs/adr/0001-mcp-pipeline-slice1.md`
**Depends on:** `docs/100-implementation-order.md`, `docs/090-architecture-review.md`, `docs/reviews/architecture-review-v1.0.md`

## Decision

The specification under `docs/` — the RFCs, the Laws, the Constitution, the
governance documents and the v1.0 architecture review — is **normative** for this
repository. Implementation follows the frozen implementation order in
`docs/100-implementation-order.md`, and its rule that no step may take a
dependency on a later one as a shortcut.

`docs/adr/0001` had recorded these documents as non-binding reference. That
status no longer applies. `docs/README.md` describes itself as "the normative
source for building Aurora OS", and from this ADR onward that is accurate.

## Recorded exception: SQLite

Step 2 of the implementation order names PostgreSQL. **This repository uses
SQLite**, by the owner's decision.

The properties the specification attaches to that step — a state manager,
verifiable auditing, WAL durability, versioned migrations, isolated snapshot and
restore — are all met by the SQLite implementation already in place. The choice
of engine is the exception; the guarantees are not.

This is the only exception. Any future one requires its own ADR, per ADR-000 and
the change-control process in `docs/adr/README.md` of the specification repo.

## What the first phase produced

Work to date was the minimal iterable slice that `docs/adr/0001` set out to
build: a secure skeleton, explicitly scoped as a prototype for validating the
security invariants before committing to the full platform. It delivered
components that satisfy gates the specification itself defines:

| Component | Gate satisfied |
| --- | --- |
| Keyed, hash-chained audit with an external head anchor | Steps 1–3 — *auditing is verifiable* |
| Idempotency with typed states and `UNKNOWN` reconciliation | Step 3 — *events are idempotent*; gate 8–9 — *reconciles `UNKNOWN`* |
| Fail-closed policy engine, no secrets in logs | Gate 4–5 — *permissions deny by default; secrets do not appear in logs* |
| Verified online backup with isolated restore | Gate 4–5 — *isolated restoration passes* |
| Persisted approvals, read-only consent sessions, operator passphrase, kill switch | Gate 10–12 — *automations are revocable, limited and observable*; LAW-006 |
| Sandbox path hardening | Step 8, when its prerequisites are in place |

These carry forward as components and are re-founded in the sequence at the step
that calls for them, rather than being rebuilt.

## Scope now blocked

Per the implementation order, the following are held until their prerequisites
exist:

- **Filesystem capabilities** (`files.write_sandbox`, `files.read_sandbox`) are
  step 8. They are not registered in the catalog; `Aurora:SandboxFilesEnabled`
  defaults to `false`. The code and its tests remain, so the path hardening stays
  covered and the capability can be offered unchanged once steps 3–7 exist.
- **`memory.remember` / `memory.recall`** store notes. The specification's Memory
  (RFC 03) is a record with provenance; the model is re-founded at step 6.
- **The MCP surface** is RFC 10 and belongs at step 10. It remains available as
  the development entry point and is not the architecture's front door.

## Resuming the sequence

Steps 0–2 are substantially in place, subject to the SQLite exception. Work
resumes at:

- **Step 3** — Event Bus, outbox, dead-letter queue. Idempotency and `UNKNOWN`
  reconciliation already exist and attach here.
- **Step 4** — Vault abstraction alongside the existing policy engine.
- **Step 5 onward** — Mind Manager, Genome, Mind State; then the Domain Model and
  the cognitive cycle.

Before any capability with external effects is offered again, the five mandatory
conditions in `docs/reviews/architecture-review-v1.0.md` apply, including Laws
001–007 as compliance tests and published event contracts and authorization
matrices.

## Consequences

The architecture's front door becomes the governed cognitive cycle
(`Event → Decision → Observation`) rather than a request pipeline. The first
vertical slice defined by the implementation order — a local conversation that
produces `Decision(RESPOND)`, records audit, creates an `Observation` and
survives restart, with no external tools — is the target that supersedes the
current execution path.
