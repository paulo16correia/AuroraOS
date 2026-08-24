# Design 0028 — Action, Observation, Reflection and Learning (step 9a)

**Status:** Implemented · **Date:** 2026-08-24
**Implements:** LAW-003, RFC 040 (`Action`, `Observation`, `Reflection`, `LearningProposal`)
**Gate:** steps 8–9 — *each action generates Observation*

## A correction to `docs/adr/0026`

That ADR said wiring the tool manager into the cycle was step 10 work. It is not: step 9 of the
frozen implementation order is *"a low-risk pilot application and end-to-end observation"*, so the
closing half of the loop belongs here. This increment builds it; the pilot that runs it end to end
follows.

## LAW-003 is enforced by refusal

The law's justification is the design brief: *without observation, Aurora doesn't know if she acted,
if she failed, or if she should learn.* Every rule below exists to make it impossible to close an
action while any of those three is unanswered.

- An action cannot reach `OBSERVED` **without a validated observation attached**. A `RAW` one does
  not count, because closing the loop on something unread is not closing the loop. A `REJECTED` one
  does not count either.
- An action that was never dispatched cannot be observed at all. An observation of something that
  never left is a fiction.
- An **unknown outcome moves the action to `UNKNOWN`** rather than leaving it dispatched, so it
  surfaces as pending reconciliation instead of looking merely slow.
- An action whose every observation reports `UNKNOWN` **cannot be closed**. "We never found out" is
  not a completed action, and the state exists precisely so it does not have to be rounded to one.
  It closes later, when a reconciling observation actually reports something.

`UnobservedAsync` exposes dispatched and unknown actions, which is what the law means by the
scheduler and UI showing what never came back.

## Observations are untrusted until read

An observation lands `RAW`. Validation moves it to `VALIDATED` or `REJECTED`, and a rejection must
carry a reason — a rejected observation with no explanation tells a later reader nothing except that
somebody was unhappy.

## Reflection with nothing to say is still reflection

RFC 021 rule 5 requires reflection after every execution *even when it concludes "no learning"*, and
`ReflectAsync` accepts an empty lesson list as a complete record. A system that only writes down
interesting outcomes has no baseline against which anything is interesting.

Reflecting on an unvalidated observation is refused: there is nothing yet to reflect on.

## Learning applies only what was approved

A `LearningProposal` moves `PROPOSED → APPROVED → DEPLOYED`, and `ApplyLearningAsync` refuses
anything that is not approved — including one that was explicitly rejected.

A system that deploys its own suggestions is not learning; it is drifting. The distinction is the
approval, and the approval is a separate act by someone else.

## Tests

17 tests: the action state machine and its refusals; an undispatched action not observable; no close
without an observation, without a validated one, or with a rejected one; a validated observation
closing the action; rejection recording why; an unknown outcome moving to `UNKNOWN` and refusing to
close; closing once something is actually learned; unobserved actions exposed; an unrecognised
outcome refused; reflection refused on an unvalidated observation; an empty reflection still
recorded; unapproved and rejected changes not applied; and a reflection decided once.

## Deferred

`GoalEvaluation`, which RFC 040 says is created only after Observation and Reflection, and belongs
with goal progress tracking. Observation `CONSOLIDATED` and `EXPIRED` — retention is carried in the
state vocabulary and nothing yet sweeps it.

## Next

Step 9b: the low-risk pilot, running the first vertical slice end to end.
