# Design 0027 — Laws 001–007 as compliance tests

**Status:** Implemented, with two named gaps · **Date:** 2026-08-24
**Implements:** `docs/laws/LAW-001` … `LAW-007`
**Closes:** condition 1 of `docs/reviews/architecture-review-v1.0.md`

## Why these are tests and not prose

The v1.0 review names this as the first mandatory condition before any capability with external
effects is offered. A law that exists only as a document is a hope; a law with a failing test is a
defect report.

Each test is titled after the law's **verifiable control** rather than after the code it exercises,
so a reader can check the law against the test without knowing the implementation. Twenty tests,
grouped by law.

## The law that found a real gap

**LAW-004 — no memory is born in isolation** requires a memory to be tied to at least one contextual
anchor, and its control says links hold *reason and evidence*, not vector similarity.

Aurora required provenance but **not an anchor**. Writing the test exposed it, and the fix was to
implement the law rather than soften the test:

- `MemoryProvenance` now requires at least one `MemoryAnchor`.
- An anchor of an unknown kind is refused, so the vocabulary stays the one the law names: entity,
  goal, conversation, observation, interval, tool, document, memory.
- An anchor without a **reason** is refused. A bare pointer is not a relation anyone can later
  explain, which is exactly what the control is guarding against.
- Anchors are stored on the record, so the law is auditable after the fact and not only at write
  time. A rule checked once and never recorded cannot be re-checked.

This is what compliance testing is for: it found the place where the implementation had drifted from
the normative text, in a direction nobody noticed.

## What each law now asserts

**LAW-001 — nothing enters Mind directly.** A memory without provenance is rejected; a world
assertion without evidence is rejected; a tool's observation lands as `PROPOSED` and the tool cannot
promote it; an unconfirmed memory yields only a `PROPOSED` graph relation.

**LAW-002 — Mind never communicates directly with tools.** A reflection test asserts that no
Mind-layer interface — memory, knowledge, world, decision, planner, attention, working memory, mind
state — mentions `ToolCall`, `ToolManifest`, `ToolResult`, `IToolConnector`, `IToolManager`,
`EphemeralSecretHandle` or `IVault` in any signature. If it ever fails, the boundary moved rather
than the test being wrong. A second test asserts a `ToolCall` cannot be authorized without both a
policy decision and, where required, an approval.

**LAW-003 — every action generates an observation.** An executed cycle cannot close without
Observation; a timeout produces `UNKNOWN` rather than presumed success, and the call is visible as
pending reconciliation rather than quietly forgotten.

**LAW-005 — every state has an owner, lifecycle and border.** Working memory, consent sessions and
decisions all expire; persistent memory names its access policy, its origin and its classification
on the record rather than implying them from where it sits.

**LAW-006 — no silent autonomy.** A session has scope, validity, an action ceiling and a pause
switch, each asserted separately. Its scope is read-only, so a write is never covered silently. A
decision that is reporting a failure can never be `SILENT`.

**LAW-007 — event-mediated communication.** Every event carries identity, correlation, producer,
timestamp, classification and idempotency key; an event without a correlation id is refused; a
consumer declares a schema version and pauses with a diagnosis on one it does not understand.

## Two gaps, named rather than glossed

**LAW-005 asks for `tenant_id`.** Aurora is single-tenant by construction — loopback transport, one
local OS user, one principal. There is no tenant to identify, and inventing a constant field to
satisfy a checklist would be worse than recording the reason. Revisit if Aurora is ever hosted for
more than one owner.

**LAW-007 is only half-satisfied.** The bus enforces its contract, and every test above passes. But
the law says components *communicate* through the bus, and Aurora's components still call each other
directly — the producers do not yet publish events when state changes. The exception clause allows
direct synchronous queries, but it also says resulting changes publish events, and they do not.

Producer wiring belongs with step 10, and until it lands **LAW-007 is enforced at the bus and not
across the platform**. Saying the law is satisfied because its unit tests pass would be the exact
failure this ADR exists to prevent.

## What this means for the frozen capabilities

`files.write_sandbox` and `files.read_sandbox` stay off. Of the review's five conditions:

| Condition | State |
| --- | --- |
| Laws 001–007 as compliance tests | **done**, with the two gaps above recorded |
| Kernel independent of Mind semantics | holds |
| Outbox, idempotence, DLQ, `UNKNOWN` reconciliation | done |
| Isolated snapshot and restore | done |
| Published event contracts and authorization matrices per capability | **partial** |

One condition remains partial and one law is not yet enforced platform-wide. That is a smaller gap
than before and it is still a gap.

## Next

Per-capability event contracts, which is the last open condition, and producer wiring, which closes
LAW-007 properly. Both sit naturally with step 10.
