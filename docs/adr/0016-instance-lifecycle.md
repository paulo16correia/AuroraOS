# Design 0016 — Instance lifecycle (step 5a, closing the step 1 gate)

**Status:** Implemented · **Date:** 2026-08-23
**Implements:** RFC 039, RFC 040 (`InstanceLifecycle`) · **Baseline:** `docs/adr/0012-specification-baseline.md`

## Why this comes first in step 5

Step 5 is Mind Manager, Genome and Mind State. The lifecycle belongs to step 1, and
`docs/adr/0012` recorded it as the piece of steps 0–2 still missing. Two things make it the right
place to start:

- The gate for steps 1–3 reads *"lifecycle recovers, events are idempotent and auditing is
  verifiable"*. Idempotency and auditing were satisfied; recovery was not, so the gate was open.
- RFC 043 requires that restore starts the Mind in `RECOVERING`. Mind State cannot be built
  correctly on top of a lifecycle that does not exist.

## The state machine

The RFC 039 diagram is implemented as written. Three sets of edges are added because the rest of the
RFC requires them, and each is recorded here rather than left as an unexplained liberty:

- **Working states return to `READY`.** `DELIBERATING`, `EXECUTING` and `MAINTAINING` reach `WAITING`
  in the diagram; without a way back, finishing a piece of work would be a one-way trip toward
  shutdown.
- **`PAUSED`, `BACKING_UP` and `UPDATING` return to `READY`.** This is what `Lifecycle.resume` is
  for, and rule 3 describes `PAUSED` as preventing new effects rather than ending the instance.
- **`RECOVERING` is reachable from any live state.** The RFC says abrupt failure is observed as
  `RECOVERING`, not `READY`, and a crash can strike in any state.

`STOPPED` and `RETIRED` are terminal, including for recovery: an instance that was deliberately
stopped does not resurrect by restarting the process.

## Mandatory rules

**Rule 1 — no `STOPPED` without a verified snapshot or an audited emergency.** Enforced. The
emergency path records its reason on the row, so "we stopped without a snapshot" is always
accompanied by why.

**Rule 2 — `BACKING_UP` and `UPDATING` drain first.** Enforced as: no active cycles. Incomplete
external calls may remain, carried as pending action refs — that is what the RFC means by marking
them for reconciliation rather than waiting for them.

**Rule 3 — `PAUSED` prevents new effects.** Expressed as `InstanceState.AllowsNewEffects`, so the
Kernel has one place to ask rather than each call site reasoning about states.

**Rule 4 — only the Kernel transitions.** A transition carries its actor and refuses anything that
is not the Kernel. `ProposeAsync` gives the Mind the channel the RFC allows it: a recorded proposal
that changes nothing.

## Refusals, not force

Every rejection returns a typed `TransitionRefusal` and leaves the state untouched. A lifecycle that
can be pushed into any state records nothing worth reading, and this record is what an operator
consults after an incident.

Transitions use the `version` column as a compare-and-set, so a concurrent mover is reported rather
than overwritten.

## Tests

14 conformance tests: a new instance starts `CREATED`; the boot path reaches `READY`; boot may not
skip `RECOVERING`; only the Kernel transitions; a Mind proposal is recorded and changes nothing;
abrupt failure reaches `RECOVERING` from a working state; stopping without a snapshot is refused;
stopping with one is allowed; an audited emergency stop is allowed and keeps its reason; backing up
is refused while work is active; `PAUSED` prevents new effects; resume returns to `READY`; a retired
instance goes nowhere; and the shutdown plan reports pending work and snapshot state.

## Deferred

Hibernation, hot migration and multiple instances, which RFC 039 lists as future expansions. Wiring
the Kernel's execute path to consult `AllowsNewEffects`, which belongs with the cognitive cycle at
step 7. Publishing lifecycle transitions as domain events, which belongs with producer wiring.

## Next

Step 5b: Genome — resolution, override validation and the bootstrap plan.
