# VS-024 — Recurring workflows

## Objective

Allow an Entity to maintain a persistent temporal intent, e.g.
“Remind me every Monday at 09:00”, and materialize each occurrence as a single
time through the VS-018 Scheduler.

## Limit

This slice delivers an auditable local notification. A future request for Email,
Discord or another connector creates a separate capability and traverses the Policies
and normal Approvals; a recurring rule is not permanent authorization.

## Flow

```text
User message
       ↓
RecurringWorkflow(ACTIVE, next_due_at)
       ↓
Task(RECURRING_NOTIFICATION, WAITING_FOR_TIME)
       ↓
Scheduler.tick()
       ↓
Task(READY)
       ↓
RecurringRun(DELIVERED) + local notification
       ↓
Task(COMPLETED) + next Task(WAITING_FOR_TIME)
```

## Objects

`RecurringWorkflow` has `weekday`, `time_of_day`, `timezone`,
`delivery_channel`, `next_due_at`, `last_triggered_at`, status and version.

`RecurringRun` represents a concrete occurrence, with the expected time, the Task
that materialized it, result and moment of delivery.

## Mandatory rules

1. Weekly recurrence uses `Europe/Lisbon` and calculates the next occurrence with
   a timezone library, including daylight saving time.
2. The identification of each Run is derived from `workflow_id + scheduled_for`; repeat
   a tick never duplicates a notification.
3. The Scheduler only releases the Task; Run creation completes local delivery and
   Schedule the next occurrence.
4. Pausing or canceling a rule prevents new occurrences, without deleting Runs
   previous ones.
5. Restarting the Entity preserves Workflow, pending Task and completed Runs.

## Error cases

- Day or time not recognized: do not create workflow and explain the accepted format.
- Delayed Tick: delivers at most one incident due and advances to the
  next week; never recovers a silent avalanche.
- Ambiguous date in the time transition: absolute `scheduled_for` guarantees a
  single occurrence.

## Acceptance criteria

- The example message creates a weekly rule and a waiting Task.
- When the time arrives, exactly one `RecurringRun` is created and the Task is completed.
- The next Task is persisted for the following week.
- A second tick does not duplicate the Run or the notification.