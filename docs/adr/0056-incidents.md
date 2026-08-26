# Design 0056 — A high-risk incident revokes, records and notifies

**Status:** Implemented · **Date:** 2026-08-26
**Closes:** RFC 09 rule 5, and RFC 08's failed-application limit case
**Found by:** `docs/reviews/rule-conformance-2026-08-26.md`

## The gap

RFC 09 rule 5 asks for three things at once: "High risk incidents MUST revoke affected capacity,
preserve evidence, and notify owner." Aurora had all three, separately, and nothing that raised the
event: the development model restricted a stage after an incident, life history had an `INCIDENT`
episode kind, maintenance notified about one, plugins quarantined themselves. `SecurityEvent` and
`Incident.open` did not exist.

## The order is the design

Revoke, then record, then notify — and not because it reads well. A notification that goes out
before containment tells the owner about something that is still happening, and the seconds spent
drawing a dialog are seconds the thing is still running.

**Revoke.** Consent sessions always: a standing permission to act without asking again is exactly
what should not still be true during an incident. Then, targeted by the event's `resource_ref`, the
named tool or plugin. Total containment for an event that named a plugin would punish the owner for
the plugin; containing nothing because the event named no resource would be worse.

Every step is best-effort and every outcome is written down, **including the failures**. A step that
threw and abandoned the two after it would leave the system in a state nobody recorded, so a
revocation that fails is a line on the incident rather than an exception.

**Record.** The audit chain, because it is the one record here that cannot be edited without being
detected, plus the incident row. An event that cites no evidence is refused outright: rule 5 asks for
evidence to be preserved, and an incident that cites none leaves a reader with an assertion and no
way to check it.

**Notify.** Only at `HIGH` and `CRITICAL`. An alert per recorded event is an alert people turn off,
and then the one that mattered arrives silenced. `INFO`, `LOW` and `MEDIUM` are recorded and nothing
else — a log nobody can write to below the alarm threshold is a log that gets written somewhere else
instead.

## Who raises one

The periodic maintenance pass, for the two conditions a sweep is the right place to notice:

- **The audit chain does not verify** — `CRITICAL`, because every other guarantee Aurora offers is
  checked against that log, so a chain that fails is not one failure among several.
- **The clock went backwards** — `HIGH`. Approvals expire, consent sessions expire, signals expire;
  a clock that moved turns every one of those promises into something else.

Both were already detectable and neither revoked anything. A broken chain was a health check that
read FAIL and a wrong clock was a verdict nobody acted on.

An incident that cannot be opened does not take the maintenance pass down with it. The pass still
has signals to expire and schedules to reconcile, and losing those as well is strictly worse.

## RFC 08's limit case, closed with the same mechanism

"Failure during application: execute rollback or mark partial status, block new application and open
incident." All four. `RollBackLearningAsync` moves the proposal to `ROLLED_BACK`, which
`ApplyLearningAsync` does not accept — so getting that change back in takes a new proposal, a new
decision and a new evaluation — and opens a `HIGH` incident citing the rollback plan as evidence, so
whoever arrives can tell whether the undoing was possible at all.

`HIGH` rather than `CRITICAL`: something Aurora changed about its own behaviour did not work. That is
serious, and it is not an attack.

## What is not here

Nothing detects secret exposure, privilege escalation or authentication abuse yet, though the types
exist for them. The plugin registry detects a credential-shaped output and quarantines the plugin
without raising an incident, because the incident service disables plugins and the reverse edge
would be a dependency cycle. Breaking it properly means an event consumer, and nothing consumes
events in-process yet (`docs/adr/0059`).
