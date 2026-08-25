# Design 0042 — Relationships and preferences

**Status:** Implemented · **Date:** 2026-08-25
**Implements:** `docs/029-relationship-model.md`

## Two things that look adjacent and are not

A **relationship** is a fact about the world: this person works for that company. A **preference**
is a habit of the person: they like short answers. Neither is a permission.

RFC 029 rule 1 keeps relationship, permission and identity as three separate objects, and the way
to keep them separate is to have **no method that crosses between them**. A reflection test asserts
`IRelationshipModel` never mentions `Principal`, `CapabilityDescriptor` or `ApprovalEvaluation` —
there is no bridge to walk across, so none gets walked across by accident.

## "You are a client" is not "act for me"

`AuthorityScope` defaults to `NONE` and claiming anything more is refused without its own approval.
The evidence that shows a relationship — a contract, an email thread — shows the tie and says
nothing about anybody agreeing to Aurora acting on it. Those are different facts and only one of
them is in the evidence.

This is the single point where relationship and permission would otherwise blur, and it blurs
quietly: a system that inferred authority from a contractual tie would be doing something that
sounds reasonable in every individual case and is wrong in all of them.

## Someone else's relationships are someone else's

Rule 3: a third party's tie is stored only with an authorisation and a bounded retention. Aurora's
owner's ties are theirs to record; everyone else appears in them without having agreed to. Past its
retention, the tie stops being in force — and the row stays, because rule 4 keeps the history and
rule 3 only bounds how long it is acted on.

## A bug the tests found in my own design

`InForceAsync` originally filtered on the **current status**, so a relationship that ended in June
returned nothing when asked about March. That makes an ended relationship never have existed, which
is exactly what rule 4 forbids — the beginning *and* the end both have to survive.

Being in force is a question about the interval, not about now. It is half-open
`[valid_from, valid_to)`, the same shape the world model already uses: a tie that ended at noon was
not in force at noon and was in force the day before. What is excluded is contested and withdrawn —
`DISPUTED` means contradicted, `RETRACTED` means it should never have been asserted, `PROPOSED`
means nobody accepted it. `ENDED` is not excluded, because the interval already handles it.

## What the person said outranks what Aurora worked out

An explicit preference displaces every inference in that dimension, and an inference never displaces
an explicit one. The direction is asymmetric on purpose. Displaced inferences are marked `REJECTED`
rather than deleted: what Aurora guessed, and that the person corrected it, is worth being able to
read later.

## Where the line is drawn on acting

`ResolveAsync` returns the preferences and `MayActWithoutConfirmation` together, so a caller cannot
obtain one without the other — the same shape as `BeliefSupport`, for the same reason.

An inferred preference may shape **presentation** — tone, format, ordering — and may not trigger a
purchase, an external message, sensitive data or a persistent change. The line is drawn where the
cost of being wrong changes kind: getting a format wrong costs a sentence, getting a purchase wrong
costs money.

Explicit preferences may act unasked, because being asked twice for something you already said is
its own failure.

## Not yet wired

Nothing consults preferences when composing a response, and nothing consults relationships when
deciding who may be told what. Both are real and both change behaviour rather than record-keeping,
so they are separable — the same seam left after the belief system, and named here rather than
implied.
