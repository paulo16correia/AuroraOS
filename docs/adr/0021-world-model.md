# Design 0021 — World Model (step 6c)

**Status:** Implemented · **Date:** 2026-08-23
**Implements:** RFC 041 · **Completes:** step 6 of `docs/100-implementation-order.md`

## Rule 4 is the one worth building carefully

*"The World Model MUST support ignorance: absence of an edge does not prove absence in the world."*

Most systems cannot say this. A query returns rows or no rows, and no rows silently becomes "no".
`WorldAnswer` carries a four-valued `WorldKnowledge` instead:

- `Unknown` — nothing recorded. The explanation says so in words a caller can repeat: *this is not
  evidence that it is untrue.*
- `Asserted` — a current, evidenced claim covers the instant asked about.
- `OnlyHistorical` — there are records, but none covering that instant.
- `Disputed` — parallel claims disagree and no choice was inferred.

The absence of a record is a fact about Aurora, not about reality, and the type makes it impossible
to lose that distinction by accident.

## Rule 5: a tool observes, it never concludes

`ObserveAsync` produces `PROPOSED` and nothing else. `ValidateAsync` promotes to `CURRENT` and
refuses a tool actor outright, as does version activation. The boundary lives in the status and in
the actor check rather than in a convention someone has to remember.

An observation without evidence is refused at the door.

## Rule 1: two different timestamps, and half-open windows

`observed_at` and `asserted_at` are separate fields, because when something happened and when Aurora
concluded it are different facts and conflating them makes an "as of" query meaningless.

Intervals are half-open, `[valid_from, valid_to)`. A boundary instant belongs to exactly one
interval, so a handover reads as neither two simultaneous truths nor a gap. A test asks at the
boundary and one second before it, and gets a different answer each time.

## The distinction that took two attempts

RFC 041 lists reassociation and contradiction as separate limit cases, and the difference turns on
**time**, not on content:

- A claim starting **later** than an open one is a **reassociation**. The previous relationship ends
  at the new start and becomes `HISTORICAL`. History is not rewritten.
- A claim about the **same period** is a **contradiction**. Both stay, both `DISPUTED`, and nothing
  is chosen. RFC 041 is explicit that the choice must not be made on which phrasing looks more
  popular.

The first implementation treated every overlap as a contradiction, so changing employer produced two
disputed claims instead of a history. This is the same shape as the memory/graph interaction in
`docs/adr/0020-knowledge-graph.md`: a temporal succession is not a disagreement, and a model that
cannot tell them apart is unable to represent an ordinary change.

## Rule 3: access is not ownership

Assertions carry a category — ownership, social, access, permission, attribute — and
`HasAccessAsync` consults **only** access assertions.

This is the rule that stops the model concluding it may act. RFC 041 puts it plainly: "the person
has Discord" does not imply that Aurora can read that Discord. A test asserts ownership of a server
and then asks about access, and the answer is `Unknown` — not `false`, because Aurora does not know
that it lacks access either.

## Rule 2: resolution defers rather than guesses

A `MATCH` needs both a suggested entity and a score above the threshold and enough evidence. Missing
any of those defers. `DEFER` is a real answer that records what was seen and what was not decided,
which is what allows a person to settle it later with the same evidence in front of them.

## Partial imports

An import runs into a `DRAFT` version, and queries only read assertions belonging to `ACTIVE`
versions. A half-finished import cannot become a source for decisions by accident — it has to be
activated deliberately, and a tool cannot do it.

## Deleted external entities

`MarkInaccessibleAsync` changes the status and keeps every evidence reference. Deleting would
destroy the history that explains why Aurora ever believed anything about that thing.

## Tests

17 conformance tests: observation entering as proposed; a tool refused at validation and at version
activation; evidence required; observed/asserted separated; half-open boundaries answering
differently either side; reassociation closing the previous window; unknown reported as unknown with
its explanation; only-historical distinguished from nothing-recorded; ownership not implying access;
evidenced access asserted with its grant; strong match, weak match deferring, and creation;
contradictions staying parallel and disputed; a deleted entity keeping its evidence; and a draft
version answering nothing until activated.

## Step 6 is complete

Domain Model, Memory, Knowledge Graph and World Model. The gate for steps 6–7 reads *"memory has
provenance, cycle does not skip Decision/Policy and context expires"* — the memory half holds
(`docs/adr/0019`). The other two belong to the cognitive cycle at step 7.

## Deferred

RFC 041's future expansions: inventory status, infrastructure digital twins, impact simulation,
verified sources, declarative consistency rules. `World.reconcile` is represented by version
activation and does not yet merge evidence across versions.

## Next

Step 7: the cognitive cycle — Attention, Working Memory, Decision and Planner.
