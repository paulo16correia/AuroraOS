# Design 0046 — The development model

**Status:** Implemented, with one deliberate narrowing · **Date:** 2026-08-25
**Implements:** `docs/037-development-model.md`
**Unblocks:** `docs/038-life-history.md`

## The distinction the whole RFC turns on

"How an instance gains operational confidence over time **without learning converting into an
automatic increase in power**."

So development changes how much of *Aurora's own* caution sits on top of the rules, and never the
rules. A stage can decide Aurora stops double-checking something it has done reliably a hundred
times. No stage can decide Aurora may do something policy refuses.

`WantsConfirmationAsync` only ever **adds**. A `false` means development has nothing further to
ask — it does not mean the action may proceed, which stays the Kernel's answer to give. A test
holds that: at the top stage development is silent about a MEDIUM capability, and that capability
is still `ApprovalRequired`, so the Kernel still asks.

## The limit case this exists for

RFC 037 names it directly: **many low-risk successes do not justify financial autonomy, SSH or
public communication.**

That is not a check. It is the shape of `PromotionCriterion`, which is **scoped to a risk level**.
Evidence is counted separately at LOW and MEDIUM and never summed, so twenty successful clock
readings cannot satisfy a criterion about writing files — and neither can a hundred and twenty.
There is a test that adds a hundred more of the same and watches the assessment stay exactly where
it was.

Summing them would have been the obvious implementation and would have made the limit case
unreachable in the wrong direction: enough of anything would eventually buy anything.

## Evidence, not elapsed time

Rule 1. Nothing in the assessment counts days. It counts outcomes, read from the **audit journal**
— the one record that cannot be talked into saying something else. A year of doing nothing produces
the same assessment as a minute of it.

`Missing` is the useful half of the answer. "Not yet" is not something a person can act on; "8 more
successful Low actions" is.

## Confidence shrinks the way it grew

A failure is evidence too. The supervised stage allows none, and enough successes do not outweigh
them — the criteria have a floor on successes *and* a ceiling on failures, checked separately.

An incident restricts **the scope it touched**, not everything. A bad write means writes get
confirmed; it does not mean reading the clock does. Pulling everything back for one failure
discards what was earned everywhere else, and the evidence survives either way.

## Asymmetric approval, on purpose

Gaining autonomy needs the owner's approval and lands in the audit journal — rule 4 wants it visible
and reversible, and a change in how much Aurora does unasked belongs where a person reads rather
than in a table they would have to know to look at.

**Pulling autonomy back needs no approval at all.** Requiring permission to be more careful would
be the wrong way round.

## The narrowing

RFC 037 says later phases "may allow for narrow preauthorization rules". This implementation has
none. Development can remove the caution *it added* and can never remove the Kernel's, so the
top stage means "development is silent", not "policy is bypassed".

That is narrower than the RFC permits, and deliberately. A standing preauthorisation is the same
object as the perpetual consent ADR 0032 argued against for schedules — and if it is ever built, it
belongs in the approval ledger where a person can see and revoke it, not in a stage that quietly
raises a ceiling.

## Rule 3, which needed no code

"Constitutional values and base identity do not evolve by inference." Nothing here touches the
personality profile. A stage changes confirmation requirements; changing who Aurora is stays what
it was in ADR 0044 — a versioned profile, an owner's approval, and an entry in the identity history.
The two systems do not have a seam between them, which is the point.
