# Design 0033 — Signals and Needs: priority without permission

**Status:** Implemented · **Date:** 2026-08-25
**Implements:** `docs/030-signal-system.md`, `docs/031-needs-system.md`
**Step:** 11b of `docs/100-implementation-order.md`

## The property both of these have to have

Urgency is the most tempting shortcut in a system like this. Something is on fire, so surely the
rules bend a little. RFC 030 rule 2 says the opposite, and says it flatly: urgency "does not grant
permission or call for tools; it only changes attention and order of evaluation."

So neither `ISignalService` nor `INeedsService` names a capability, a tool or an approval anywhere
in its surface. A CRITICAL signal reaches the front of the queue and then waits for exactly the same
decision and the same permission as a request someone typed. The most a need can do on its own is
**draft a goal** — which then goes through the cycle like any other work.

That is what "limited autonomy" has to mean if it is to mean anything. Aurora can notice, prioritise
and propose. It cannot promote its own noticing into authority.

## Signals: an opinion, not a fact

An event is a standardised, immutable fact. A signal is an opinion about how much that fact matters
right now, and it expires. Three rules do the work:

**A signal needs a source that actually happened.** `EmitAsync` checks the reference against
`domain_event`, `schedule_run` and `observation` — the three things RFC 030 accepts as verifiable.
Without the check a classifier could invent the urgency and the evidence for it in the same breath,
and nothing downstream could tell the difference.

**A signal must expire.** A lifetime of zero is refused. Anything that never expires is a permanent
claim on attention, which is precisely what a signal must not become.

**Interrupting takes a policy threshold, and preserves what it interrupted.** The threshold lives in
`SignalPolicy`, not in the signal — the same alert is worth stopping for on a quiet evening and not
worth it mid-incident. When an interruption does happen, the running cycle is parked with
`WaitAsync` and can be resumed. An urgent alert that destroyed whatever Aurora was in the middle of
would cost more than the thing it interrupted for.

Storm control deduplicates by kind, source and target within a window, and rate-limits beyond that.
Held-back signals are recorded as `SUPPRESSED` with a reason code rather than dropped — a signal
nobody can find afterwards is indistinguishable from one that was never raised.

## Needs: what is waiting on Aurora

Every need is derived from something counted or measured — dead letters, pending approvals, overdue
goals, missed schedule runs, backup age, unreconciled reservations. Not a mood.

**Rule 1 is enforced from both ends.** A need carries the evidence that raised it *and* a stated
condition that would end it. Satisfying one without an evidence reference is refused. A need with no
measurable end never stops being urgent, and a need that can be declared met without proof is how
maintenance quietly stops happening.

**Rule 3, the ordering, is where the judgement is.** Safety and recovery first — those are the
system failing to keep its own promises. Then what the person asked for. Then maintenance, which is
the work that can always wait one more hour and must therefore never be allowed to push in front,
no matter how much of it has piled up. An incident cannot be deferred at all.

**Rule 4: intensity halves every 24 hours.** A day of nobody acting is evidence that something was
less urgent than it claimed. Decaying it is what keeps the loud ones meaningful.

The needs table is a **state, not a log**: the same condition seen twenty times is one need with an
updated intensity, not twenty rows.

## Where they join

A signal at HIGH or above becomes a SAFETY need referencing it. That is the mechanism by which
something that deserved attention still deserves it after the signal itself has expired — otherwise
rule 4 would quietly turn "this matters" into "this never happened".

## A constraint I got wrong and removed

The first schema had `UNIQUE (subject_ref, status)` on `need`, meaning to express "one open need per
subject". It does not express that: it also forbids a subject from ever having two *satisfied* needs,
so the second time the dead-letter queue was drained the insert would fail. The invariant is kept by
the upsert instead, and the column pair is now just an index.
