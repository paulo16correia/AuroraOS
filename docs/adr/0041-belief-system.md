# Design 0041 — The belief system

**Status:** Implemented · **Date:** 2026-08-25
**Implements:** `docs/028-belief-system.md`
**Unblocks:** `docs/029-relationship-model.md`

## Why this is not memory

A memory is something that was recorded. A belief is a pattern Aurora thinks it sees across them —
"the owner prefers short answers", "server A is more stable". Different tables, different rules,
different lifetimes.

Keeping them apart is what lets Aurora act on a useful pattern without pretending it is reality. Put
a belief in the memory store and it becomes a fact by adjacency: same shape, same provenance fields,
same confidence column, and nothing to tell a reader that one was observed and the other was
guessed at.

## The model is not evidence for itself

RFC 028 rule 1 ends with that phrase and it is the sharpest line in the document. A belief whose
entire case is something a reasoner said has no case: a pattern the model asserts about its own
reasoning is not a second opinion, it is the same one restated.

So `ProposeAsync` refuses when *every* supporting reference is model-derived — `model/`,
`reasoner/`, `inference/`, `llm/`, `thought/`, `deliberation/`. The rule is that the model cannot be
the **whole** case, not that it may not appear in one, and a single real observation alongside it is
enough.

## Rule 2 travels with the answer

A belief may never be the sole basis for identity, security, money, health, law or sensitive
content. That could have been a check a caller remembers to make. Instead `SupportAsync` returns
`BeliefSupport(Beliefs, MayBeSoleBasis, Reason)` — so a caller **cannot obtain the beliefs without
also obtaining the fact that they are not enough**. The answer is the same however confident the
belief is; a 0.99 belief about someone's identity is still a guess about a person.

The purpose list is closed, taken from the rule word for word. Closed because the interesting
failure is not misjudging one of these — it is a purpose nobody classified quietly getting the
benefit of the doubt.

## Contradiction narrows scope; it does not average

RFC 028's limit case is explicit and it is the most interesting instruction in the document:
contradictory evidence produces `CHALLENGED` and a **reduced or separated scope**, not a blended
confidence.

So `ChallengeAsync` leaves the confidence exactly where it was and stops the belief being usable.
Answering the contradiction means `NarrowAsync` — saying where the belief still holds — and
narrowing to the same scope it already had is refused, because reactivating without narrowing is
answering a contradiction by ignoring it.

Averaging would have been easier and is worse: it turns two incompatible observations into one
lukewarm claim that describes neither. "Prefers short answers, 0.55" is not a smaller version of the
truth, it is a different and false claim.

## Nothing is erased

A failed prediction attaches counter-evidence to the side it argues for and is re-evaluated.
Retraction sets a status. Every movement writes a `BeliefUpdate` with its reason. The record of
having believed something wrong is the part worth keeping — a system that deletes its mistakes
cannot be calibrated, and cannot be argued with.

## Beliefs weaken on their own

Confidence halves weekly and beliefs expire after thirty days without confirmation. That is rule 3's
"explicit policy", and the reason is definitional rather than practical: **a belief that never
weakened would be a fact**, which is the confusion this whole system exists to prevent.

User-stated beliefs decay and can be challenged too. Rule 4 says a direct statement may prevail, not
that it stops needing to be true.

## Not yet wired into attention

The RFC's flow ends at "Active Belief → attention/decision", and this stops one step short: nothing
consults beliefs when ranking attention or pricing a decision option yet. That is a deliberate
seam — the belief store is complete and testable on its own, and wiring it into the cycle changes
what Aurora decides, which deserves its own change rather than riding along inside this one.
