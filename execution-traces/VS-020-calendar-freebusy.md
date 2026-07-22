# VS-020 — Calendar Free/Busy

## Objective

Enter `CALENDAR_FREEBUSY` as external reading capability. The intention is to respond to availability queries without transforming a read into an external write effect.

```text
User message
        ↓
CapabilityRequest(CALENDAR_FREEBUSY, READY)
        ↓
Policy(NOT_REQUIRED)
        ↓
CalendarAvailabilityQuery
        ↓
ExecutionPreparation(READY)
        ↓
CalendarFreeBusyExecutor
        ↓
GoogleCalendarProvider.freebusy
        ↓
ExecutionRecord(COMPLETED)
        ↓
CapabilityResult(SUCCESS)
```

## Rules

- The message requesting availability authorizes only the current read query.
- `CALENDAR_FREEBUSY` never creates, changes or deletes events.
- The policy is persisted and can be changed to require approval if the entity owner so decides.
- The execution has `approval_ref = approval:not-required`, making the absence of explicit approval auditable, not implicit.
- The result and `CalendarAvailabilityQuery` are persisted; repeating the same message with the same idempotence key does not query the API again.

## Supported intents

```text
Am I free tomorrow at 3pm?
When do I have a free hour this week?
```

The first consultation evaluates a one-hour window. The second searches for the first free window of 60 minutes, on weekdays, between 09:00 and 18:00 local time, over a seven-day horizon.

## Known limits

The VS-020 only queries the `primary` calendar. Name resolution, multiple calendars, time preferences and time zones per entity belong to later slices.