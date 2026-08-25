# Design 0043 — Self: what Aurora knows about itself

**Status:** Implemented · **Date:** 2026-08-25
**Implements:** `docs/027-self-model.md`
**Unblocks:** `docs/037-development-model.md` (with RFC 07)

## The sentence this exists to make possible

RFC 027's justification is one line: an agent that does not know what it can do makes wrong promises
and failed attempts. Self is what lets Aurora say **"I can prepare this, but not send it"** on the
strength of something observed.

## Three questions, three answers

Rule 2 is the whole design: *installed capacity*, *permission granted* and *currently safe action*
are separate and none implies another. `CapabilityAssessment` therefore has three booleans and not
one.

A connector can be installed and revoked. A capability can be permitted and out of budget. Something
can be perfectly safe and not installed at all. Collapsing them gives one word — "no" — where the
useful answer is which of the three failed:

- Not installed → nothing to configure, it was never here.
- Installed, not permitted → a policy question, and a person can change it.
- Installed, permitted, not safe now → wait, or degrade the plan.

Saying "I cannot do that" when the truth is "I am not allowed to" is a different answer to a
different question, and it sends whoever asked to the wrong place.

## Consulted before an option exists, not after

Rule 1 says a decision that proposes a tool must consult the Self. In `KernelDispatcher` an action
the Self reports as unsafe becomes a **blocking reason** on the option rather than a lower price on
it. An option the Self says cannot run now is not an option — offering it and letting cost decide
would be deciding to do something impossible.

## Health is observed, dated, and not presumed

Rule 4. `HealthObservedAtUtc` travels with the summary, and a reading older than two minutes is
refreshed rather than trusted — permissions and health are exactly what moves between readings, so
a stale Self is not even a starting guess.

**Only FAIL degrades.** A WARN means something warrants a look — a dead letter, a sealed audit chain
— and treating that as diminished capacity would stop Aurora acting for reasons that have nothing to
do with whether it can. The warning still reaches the health summary, where a person sees it.

## RECOVERING is a real state, not a formality

An instance with reservations left in `UNKNOWN` by a restart is `RECOVERING`, and nothing is safe
while it is. A restart that left calls in an indeterminate state has not finished starting, whatever
the process thinks about itself.

## What Aurora will say about itself

`SafeSelfDescription` is a **separate type**, not a filtered `SelfModel`. Filtering is something
somebody forgets; a type with nowhere to put a secret cannot leak one by omission. There is no field
for an identity reference, a resource snapshot or a provider name — not because they are stripped,
but because there is nowhere to put them.

## Pausing is a decision, and an observation does not overturn it

A paused instance stays paused through a refresh. Resuming takes a **fresh reading** and reports
whatever it says — which may be `DEGRADED` or `RECOVERING`. Resuming means "look again", not
"everything is fine".

## Two test-design faults this surfaced

The integration tests began failing on my machine, correctly: a disk at 99% made the Self degraded,
which blocked every effectful capability. The behaviour was right and the test setup was not —
`AuroraAppFactory` was reading the real host through `SystemResourceProbe`, so a developer with a
full drive would watch Aurora refuse everything and conclude the tests were broken. The factory now
injects a deterministic probe, the same fix ADR 0034 applied to the unit tests.

Separately, `InMemoryIdempotencyStore.ReconcileStaleAsync` returns zero because the fake keeps no
timestamps. A test about what a restart leaves behind was reading that zero as a finding. It now
uses the real store, and the fake says in its own documentation that its answer is not one.
