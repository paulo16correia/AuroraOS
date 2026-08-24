# Design 0022 — Attention and Working Memory (step 7a)

**Status:** Implemented · **Date:** 2026-08-23
**Implements:** RFC 023, RFC 024 · **Gate:** steps 6–7 — *context expires*

## Why these two first

They are the front of the cognitive cycle: attention decides what is worth thinking about, and the
working memory is where that thinking happens. Everything later in the cycle reads from the frame
they produce, so building the Decision Engine on top of an unbounded context would be building on
the wrong foundation.

They also close the *context expires* half of the step 6–7 gate.

## RFC 023 rule 4 is the reason rule 1 exists

*"High salience does not circumvent policy: a secret or hostile instruction does not gain access
because it is urgent."*

That guarantee comes from ordering, not from a check somewhere in the scoring function.
`IAttentionAuthorization` runs **before** any weight is applied, and an unauthorised candidate is
excluded with a reason code before relevance is computed at all. Urgency has nothing to bid with
because the auction is already over.

A test sets relevance and urgency to their maximum on a `SECRET` item and asserts it is still gone.
This is the shape prompt injection takes inside a cognitive system: something that shouts to be
attended to. The defence is structural.

There are two ceilings, and both apply: what the caller may see, and what the policy allows into a
cycle at all. An operator may hold clearance that a routine cycle should still not use.

## Rule 3: reasons for both sides

Excluded items are kept, each with a reason code — `below_threshold`, `item_limit_reached`,
`budget_exhausted`, `not_authorised`, `above_sensitivity_ceiling`, `expired`. They survive the round
trip to storage.

Recording only what was selected would make the system unauditable in exactly the case that matters:
when someone asks why Aurora did not notice something.

## Determinism

Ranking sorts by score, then recency, then confidence, then reference. RFC 023's tie-break rule
names the first three; the reference is added so two identical runs cannot disagree. A ranking that
shuffles under a tie makes every downstream record unreproducible.

## RFC 024: the frame refuses rather than truncates

When capacity is exhausted, `PutAsync` throws. The RFC's limit case says attention drops the least
useful item **or** the cycle asks for clarification, and explicitly forbids silently truncating
sensitive data. Neither of those choices belongs to the store, so it refuses loudly and says what
the caller may do about it.

## Rule 3: a hypothesis is never quietly promoted

Consolidating a working item produces a proposal, and a proposal derived from a `HYPOTHESIS` carries
`MustEnterAsCandidate`. It can be offered to memory, but only through the RFC 03 candidate flow —
which is the flow that requires confirmation before anything becomes an active fact.

The alternative, letting a guess made during one cycle harden into a remembered fact, is precisely
how a system develops persistent hallucinations.

## Rule 4: no raw drafts as "internal reasoning"

`DisposalReport` carries counts and an operational summary. A test writes a payload containing
recognisable draft content and asserts it does not appear in the summary.

## Rule 1: sharing is explicit

Frames do not share. Moving an item between cycles is a transfer with a reason, so one cycle's
context cannot leak into another's by being nearby.

## Tests

17 conformance tests: unauthorised excluded before scoring; urgency not buying access; policy
ceiling separate from caller ceiling; item limit and token budget with their reasons; reasons
recorded on both sides and surviving storage; expired candidates; empty candidate set producing an
empty set rather than an error; deterministic ranking; frame seeded from the selected set; a full
frame refusing loudly; an item above the ceiling refused; context expiring on TTL; a sealed frame
taking nothing more; a hypothesis consolidating only as a candidate; disposal defaulting to discard
and summarising without exposing drafts; and explicit transfer between frames.

## Deferred

RFC 023's phased hybrid search for very large candidate sets — the pruning is deterministic but
loads candidates the caller supplies rather than paging a store. Policy-driven urgency elevation for
real emergencies. RFC 024's encrypted short-term storage for sealed frames across a restart, and
multimodal frames.

## Next

Step 7b: the Decision Engine, then 7c: the cognitive cycle itself and the Planner.
