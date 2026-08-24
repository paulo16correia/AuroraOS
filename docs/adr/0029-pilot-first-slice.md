# Design 0029 — The low-risk pilot: first vertical slice (step 9b)

**Status:** Implemented · **Date:** 2026-08-24
**Implements:** the *First vertical slice* of `docs/100-implementation-order.md`
**Completes:** step 9 · **Gate:** steps 8–9, now met end to end

## What the order asked for

> A local conversation creates an `Event`, opens loop, reclaims empty/allowed memory, produces
> `Decision(RESPOND)`, stores audit, creates `Observation` from the response, and survives restart.
> **Do not use external tools.**

All of it, in one path, with no connector. This is the first time the pieces run together rather
than each in its own test.

## Why a conversation with no tools is the right first thing

It is the smallest thing that exercises the entire governed path. Every stage of RFC 021 has to be
accounted for, the decision has to be committed against a policy, the response has to be observed,
and the whole thing has to be findable after a restart — and none of it can hide behind a connector
doing the interesting part.

If this slice cannot be built cleanly, nothing involving an external effect should be attempted.

## Every stage is accounted for, run or omitted

Perception, Attention, Working Memory, Memory, World Model, Decision, Policy, Executor, Observation
and Reflection **run**. Planner, Capabilities and Learning are **omitted with reasons**:

- Planner — a single conversational turn carries no explicit goal.
- Capabilities — responding needs no external capability.
- Learning — the reflection proposed no change.

A test asserts the count of recorded stages equals the count of stages in RFC 021, and that every
omission carries a note. Nothing is absent; the difference between "did not apply" and "was skipped"
is written down.

## The decision that had to be re-priced

The pilot first offered *respond* and *ask* at the same cost, and the engine chose `ASK`. It was
right to: both were internal, both cost the same, and the tie broke on reversibility — answering
cannot be unsaid, asking can.

The engine's preference for the reversible option among genuine equals is sound and stayed. What
was wrong was the pilot's pricing: **a needless clarification costs the person a round trip**, so
asking is no longer free. Reversibility now decides only between options that really are equal.

This is the kind of thing an end-to-end slice exists to surface. Each component was behaving
correctly and the composition still produced the wrong answer.

## Aurora does not write the reply

`Compose` produces an operational summary — the mode chosen and what was recalled — not a sentence.
RFC 021 puts natural language with the LLM client, and a pilot that produced prose would quietly
move the boundary the whole architecture is built around.

When nothing is recalled the summary says *nothing recorded on this*, and when the search was
degraded it says absence is not established. No memory is invented to fill the gap.

## Survives restart

The test builds a full set of services, runs a turn, then builds a **second** set over the same
database sharing nothing in memory, and recalls the turn by its cycle id: the status, the stage
record, the decision, action, observation and reflection are all there.

That is what makes it a slice rather than a demo. The Kernel keeps continuity without an LLM present
and without anything held in a process.

## The gate is now met end to end

Steps 8–9 read *"capability does not couple to provider; each action generates Observation and
reconciles UNKNOWN"*. `docs/adr/0026` recorded the gate as half-wired because the pieces held
individually and not across a real path. They now do: this slice dispatches an action, observes it,
validates the observation, closes the action, reflects, and leaves nothing pending reconciliation —
asserted by a test that checks `UnobservedAsync` is empty afterwards.

## Tests

10 tests: the whole path deciding to respond; every stage accounted for with reasons for omissions;
no external capability used; the turn published as an event with its classification and producer;
the response existing as an observed action with a validated observation and nothing left pending;
the turn audited and the chain still verifying; the working memory frame discarded so a turn leaves
no growing transcript; the turn recalled after a restart; nothing recalled reported honestly; and an
empty utterance rejected with no persistent cognitive mutation at all.

## Deferred

The pilot is a direct call, not yet reachable over MCP — the tool surface is RFC 10 and belongs to
step 10. Producer wiring for the rest of the platform (LAW-007) is still open; this slice publishes
its own event, which is one producer rather than all of them.

## Next

Step 10: API and control UI for approvals and audit, where the MCP surface is rebuilt on this cycle
rather than beside it.
