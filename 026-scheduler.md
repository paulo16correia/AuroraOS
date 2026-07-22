# Aurora OS — RFC 026: Scheduler and Continuous Life

**State:** Normative · **Depends on:** RFC 01, 021–022, 040

## Objective

Reliably create temporal and recurring events. The Scheduler provides rhythm to Aurora, but does not receive direct access to tools or authorization to act outside the cognitive cycle.

## Architecture and flow

```text
ScheduleRule → next occurrence calculation → JobDue Event
                                            │
                                            ▼
                                  Cognitive Cycle completo
                                            │
decision: act | ask for approval | wait | notify
                                            │
                                            ▼
execution log and next occurrence
```

## Data structures

```text
Schedule
  id, owner_id, title, trigger: ONCE|CRON|INTERVAL|EVENT_CONDITION
  timezone, expression, next_run_at, last_run_at
  target: CYCLE_TEMPLATE|GOAL|TASK, payload_ref
  enabled, quiet_hours_policy, missed_run_policy
  status: ACTIVE|PAUSED|EXPIRED|FAILED

ScheduleRun
  id, schedule_id, due_at, started_at, finished_at
  status: DUE|STARTED|SKIPPED|SUCCEEDED|FAILED|MISSED
  cycle_id, result_ref, idempotency_key
```

## Interfaces

```text
Scheduler.create(schedule, approval) -> Schedule
Scheduler.tick(now) -> ScheduleRun[]
Scheduler.pause(id, actor) -> Schedule
Scheduler.reconcile(run_id) -> ScheduleRun
```

## Mandatory rules

1. Time zone is mandatory; DST and clock changes MUST be handled by reliable calendar library.
2. Each occurrence MUST have an idempotence key and go through the RFC 021 cycle.
3. Routines that send communication, change data or use a connector require normal policy/authorization; Scheduling is not perpetual consent.
4. The user can pause, edit or delete any schedule; deleting prevents future occurrences, does not delete past audits.

## Limit and error cases

- **Server shut down at 08:00:** apply `missed_run_policy` (`SKIP`, `RUN_ONCE`, `ASK`); Never run an avalanche by default.
- **Duplicate time in autumn:** use occurrence identifier and execute at most once unless explicit rule.
- **Invalid zone:** disable schedule and notify; do not assume UTC silently.
- **Cost limit exceeded:** mark `SKIPPED` correctly and request a change to the rule.

## Justification

Separating runtime keeps Aurora “alive” without creating a dangerous bypass for external actions.

## Future expansions

External calendars, availability windows, priorities, dependencies between schedules and distributed recovery.