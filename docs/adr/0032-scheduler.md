# Design 0032 — The Scheduler: rhythm without authority

**Status:** Implemented · **Date:** 2026-08-25
**Implements:** `docs/026-scheduler.md`
**Step:** 11a of `docs/100-implementation-order.md`

## The one thing a scheduler must not be

RFC 026 puts it plainly: the Scheduler "does not receive direct access to tools or authorization to
act outside the cognitive cycle." Nothing in `SqliteScheduler` can call a capability. A tick turns
elapsed time into due runs and events, and stops. Whether any of them actually happens is decided
afterwards, by the cycle, against the same policy and approval checks as a request a person typed.

That is not fastidiousness. A timer that could act would be a way around every check Aurora has:
schedule it once, and it runs forever without anyone being asked again. Rule 3 says so directly —
scheduling is not perpetual consent — and it is enforced twice: an approval is required for a
routine that reaches outside Aurora to *exist*, and the per-occurrence checks still apply on top.

## Cron, written here

Five fields, the classic semantics, about a hundred and eighty lines. Taking a package was the
alternative; the syntax is small and closed, and a scheduling bug is easier to find in code that
can be read than in a dependency that has to be trusted. It handles `*`, a value, `a-b`, lists and
`/` steps, and applies the Vixie rule where day-of-month and day-of-week are OR-ed when both are
restricted.

It deliberately works in **wall-clock time and knows nothing about offsets**. Which UTC instant a
wall time corresponds to — and whether it exists at all, or exists twice — is a question about a
time zone, and belongs to the caller that has one.

## Daylight saving, which is where schedulers actually break

Three distinct cases, all covered by tests rather than by intent:

- **Ordinary transition.** "Every day at 09:00" means 09:00, not a fixed offset. Occurrences are
  computed in local time and then converted, so 09:00 Lisbon is `08:00Z` in summer and `09:00Z` in
  winter.
- **The hour that does not exist.** 01:30 never happens on the spring-forward day. The occurrence
  moves to the next real match rather than being invented at an offset nobody asked for.
- **The hour that happens twice.** In autumn, 01:30 occurs at two different instants. The
  occurrence key is the schedule plus the **local wall time**, so the second pass produces the same
  key and the unique index makes it a no-op. "At most once" is enforced by the database, not by
  remembering to check.

An unresolvable zone disables the schedule and publishes an event. It never falls back to UTC:
running at the wrong hour without saying so is worse than not running.

## Never an avalanche

A machine that was off for four days must not wake up and fire four mornings at once. Every missed
occurrence is **recorded as MISSED** — so the gap is visible rather than silently absent — and at
most one is offered to run, the most recent. `SKIP` is the default and offers none. `ASK` records
them and publishes an event, leaving the decision with the person rather than taking it for them.

Past 500 missed occurrences in a single tick, the schedule is marked FAILED with the reason. Half a
million rows recording that a machine was off for a year is its own kind of failure.

## A bug this found

Due times were being compared in SQL with `<=`, where they are text. Stored with their local offset,
`01:30+01:00` sorts *after* `00:30+00:00` even though it is the earlier instant — so a schedule in
any zone ahead of UTC would simply never come due. Instants are now normalised to UTC on the way in.
The DST test caught it; nothing else would have, because every other timestamp in the system comes
from the clock already in UTC.

## Deleting lands in EXPIRED, and here is why

Rule 4 says deleting prevents future occurrences and does not delete past audits, so this is not a
`DELETE`: the row stays and so does every run it produced. But RFC 026 freezes the status set at
`ACTIVE|PAUSED|EXPIRED|FAILED`, which has no value for "the person ended it". Rather than add a
status the RFC does not have, a deleted schedule lands in `EXPIRED` and its reason records who
ended it. Noted here because the status alone does not tell the whole story.

Resuming picks up from **now**, not from where the pause started. Otherwise resuming replays the
whole pause as a backlog, which is the avalanche again wearing a different hat.

## Reconciliation does not guess

A run left `STARTED` by a crash is settled from the cycle it started: completed is a success, failed
is a failure, no cycle on record is a failure. A run whose cycle is still running is **left alone**
— calling it either way would be inventing an outcome.
