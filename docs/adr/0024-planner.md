# Design 0024 — Objectives, plans and tasks (step 7d)

**Status:** Implemented · **Date:** 2026-08-23
**Implements:** RFC 05 · **Completes:** step 7 of `docs/100-implementation-order.md`

## A goal is not a task

RFC 05 separates lasting intention from a concrete attempt at execution, and the v1.0 architecture
review kept `Mission | Goal | Plan | Task` deliberately apart. The split is what makes progress
measurable: a goal states what "done" means, a plan is one revisable strategy for reaching it, and a
task is a unit that either succeeded or did not.

## Rule 1: "deal with it" produces discovery, not a guess

A goal request without an outcome or without success criteria does **not** get decomposed. It gets a
single human task asking for them, the goal stays `DRAFT`, and the plan records why:

> The outcome is not yet defined; nothing is decomposed on a guess.

Decomposing an underspecified request is how a system produces confident work that solves the wrong
problem. Asking is cheaper than being wrong at length.

## Rule 2: the state machine is written down

`TaskState.Allowed` is an explicit map rather than logic inferred at each call site, so an illegal
transition is a lookup failure and not an oversight. `READY` does not jump to `SUCCEEDED`.

Every transition attaches evidence, and a transition with none is refused. A state change without
evidence is an assertion, not a record.

## Rule 3: running needs its dependencies, or an explicit rule

A task cannot enter `RUNNING` while a dependency has not succeeded. The RFC allows an exception
"unless explicitly ruled", so the override exists — and it is a named rule recorded on the
transition, never a flag someone can set silently.

## Invalid output does not become success

Marking a task `SUCCEEDED` requires its acceptance tests to have been evaluated and passed. Two
separate failures are caught:

- A test that **failed** turns the transition into `FAILED` with a diagnosis naming it.
- A test that was **not evaluated** does the same. Silence is not a pass, and treating an unreported
  check as satisfied is the most comfortable way to lie about an outcome.

Dependents are not unlocked in either case.

## A failed task holds its dependents

RFC 05 forbids marking anything complete by inference. The mirror of that is not letting anything
proceed by inference: when a task fails, its `READY` dependents drop back to `DRAFT` with a
diagnosis naming the dependency, and replanning is offered rather than performed.

## Rule 4: automatic repetition is narrow

A retry needs both halves — an idempotency key, because repeating without one may do the thing
twice, and budget headroom. Missing either is refused with the reason.

## Rule 5: assumptions are not lost at a revision

`ReplanAsync` carries the previous plan's assumptions into the new revision. Dropping them is how a
plan quietly stops explaining itself, and by revision three nobody remembers what it was resting on.

## Limit cases

**A blocked goal is not replanned.** Policy said no; producing a different decomposition for the
same outcome is working around the rule rather than respecting it. The refusal names the reason the
goal was blocked.

**A deadline is never quietly moved.** `HandleOverdueAsync` notifies, pauses or continues per the
configured action, and a test asserts the stored deadline is unchanged afterwards. There is no API
that shifts it as a side effect.

**A dependency on a task nobody described is refused** rather than silently dropped, so a plan
cannot appear to have fewer constraints than it was given.

## One type, three interfaces

`SqlitePlanner` implements `IPlanner`, `ITaskService` and `ITaskScheduler`. They share the same
tables, and splitting them into three objects would mean three things arguing over the same rows
while each holding half the invariants.

## Tests

19 conformance tests: discovery for a goal with no criteria and for one with no outcome, a complete
goal decomposing, assumptions surviving a replan, illegal transitions and evidence-free transitions
refused, running blocked by an unmet dependency and permitted by an explicit rule, a failed
acceptance test and an unevaluated one both becoming failure with a diagnosis, passing acceptance
succeeding, a failed task holding its dependents, retry refused without idempotency and without
budget and allowed with both, a blocked goal refusing replanning, an overdue goal paused with its
deadline untouched, the scheduler releasing tasks only as dependencies succeed, and an unknown
dependency refused.

## Step 7 is complete

Attention, Working Memory, Decision Engine, the cognitive cycle and the Planner. The gate for steps
6–7 — *memory has provenance, cycle does not skip Decision/Policy and context expires* — now holds
in all three parts.

## Deferred

RFC 05's future expansions: probabilistic estimation, calendars, priority negotiation, approved
multi-agent teams and resource optimisation. Plan approval is modelled in the status vocabulary and
not yet wired to the approval ledger — that belongs with the cycle's Policy stage at step 10.

## Next

Step 8: Capability Resolver and the Tool Manager sandbox, where the frozen filesystem capabilities
from `docs/adr/0012` are re-founded on everything built since.
