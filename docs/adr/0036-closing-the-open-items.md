# Design 0036 — Closing the open items

**Status:** Implemented, with two items honestly not done · **Date:** 2026-08-25
**Closes:** condition 5 of `docs/reviews/architecture-review-v1.0.md`; the residual risk in
`docs/adr/0003`; the growth recorded in `docs/adr/0031` and `docs/adr/0033`; LAW-007 producer wiring

## Condition 5: contracts that are checked, not just written

The review's last open condition asked for event contracts and authorization matrices published per
capability. A published contract that nothing enforces is a description of intentions, so both are
now load-bearing:

**Events.** `EventCatalogue` is a closed, compile-time list: type, version, producer, sensitivity,
what the payload carries and who consumes it. The outbox refuses anything not on it — wrong type,
wrong producer, wrong classification. LAW-007's verifiable control ("each producer declares events")
stops being documentary.

The sharpest consequence is at the edge. `POST /v1/events` used to accept **any type from any
caller**, which meant a surface outside Aurora could assert anything about anything on the bus. It
now publishes exactly one declared type, `ExternalObservationReported`: what the caller saw goes in
the payload, where it reads as a report rather than a fact. `api` is the only producer reachable
from outside, and it may emit that one type.

**Capabilities.** `docs/reference/capability-authorization.md` is generated from the live registry —
action, risk, effects, approval, consent path — and `AuthorizationMatrixTests` fails when the file
and the code disagree. It includes the frozen sandbox capabilities on purpose: a capability left out
of the table because it happens to be switched off is exactly the one somebody turns on without
reading anything.

## LAW-007: two state changes that were only ever local

The bus contract was enforced but the interesting state changes never reached it. Two now do:
`ApprovalDecided` when a person decides what Aurora may do, and `MemoryRevised` / `MemoryForgotten`
when the owner corrects or retracts something. Identifiers travel; content does not — a memory the
owner just asked to forget must not be copied into the bus on its way out.

## The TOCTOU race: narrowed, and made detectable

ADR 0003 stated the residual risk plainly and said the mitigation was operational: the sandbox root
"should be writable only by the Aurora process's own user". **Should is not a control.** Aurora now
applies it — `RestrictToOwner` sets owner-only mode on the root and on every directory it creates.

.NET still has no portable `openat`/`O_NOFOLLOW`, so a directory component swapped between the check
and the rename cannot be *prevented*. It is now detected: containment is re-verified immediately
before the rename and again after it, and a file that landed outside the sandbox is deleted and the
call fails. That is the difference between a contained failure and a silent escape. The reader got
the same check, because reading through a swapped component discloses a file outside the sandbox
just as surely.

Unfreezing `files.read_sandbox` / `files.write_sandbox` is now a configuration flag and a decision,
not a missing control. It stays **off by default** — that call belongs to whoever runs the instance.

## Retention: forgetting the by-products, and only those

ADR 0031 recorded that cycle history grows without bound; ADR 0033 the same for signals and
proposals. `SqliteRetentionService` removes closed cycles and their stages (90 days), settled
schedule runs, resolved signals and dead questions (30 days), as part of the upkeep pass.

The shape matters more than the numbers. It touches **closed working records only**. The audit
chain, memories, goals and missions are out of reach, and there are tests that say so: a system that
tidies away its own history on a schedule is one whose history cannot be relied on, and the audit
chain would stop verifying if a single record vanished. A cycle still running never ages out however
old it is — an old unfinished cycle is the most interesting row in the table, not the least.

## A lost signing key had no way forward

The audit chain is keyed, and the server refuses to start when verification fails. Correct — but if
the key is lost or replaced, that refusal is permanent and there is nothing an operator can do.
**That is not fail-closed, it is bricked.**

`seal-audit-break <reason>` on the server console records the discontinuity and starts a new chain.
It repairs nothing and says so, twice: at the prompt, and permanently in the log. Records before the
seal stay exactly as they are and can never be verified again.

Two properties make it safe:

- The seal is **signed with the current key** like any other record, so someone with write access to
  the database but no key cannot forge one to make a rewritten history verify. There is a test that
  plants exactly that forgery and watches verification refuse it.
- Verification finds the newest *genuine* seal first and walks from there, reporting
  `AcknowledgedBreakAt`. The honest answer to "does the audit log verify" is sometimes "from record
  N onwards", and that is now the answer it gives.

It lives on the console rather than over HTTP for the same reason passphrase enrolment does: the
bearer token belongs to the agent, and an agent able to declare its own audit trail unverifiable
would be able to erase its own history.

## Two things not done, and why

**A trusted desktop passphrase dialog.** Approvals are guarded by an operator passphrase, but it
arrives as a parameter on an MCP tool call — so the agent's client handles it in transit. The fix is
a dialog Aurora owns, outside the agent's reach, on each platform. That is a real cross-platform UI
component, not an afternoon, and building a bad one would be worse than the current arrangement,
which at least documents its own limitation (`docs/adr/0011`). Still open.

**Azure OpenAI against the real service.** The reasoner adapter has never run against a live
deployment; there is no endpoint or key here, and there is no honest way to test that from this
machine. Objective mode degrades to the keyword fallback when no model is configured, which is what
every test exercises. Whoever first configures a real deployment is running that path for the first
time, and should know it.
