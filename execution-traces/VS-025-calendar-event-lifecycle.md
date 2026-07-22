# VS-025 - Calendar Event Lifecycle

## Objective

Allow you to change or cancel a Google Calendar event
auditable, user approved and idempotent. This slice introduces the
capabilities `CALENDAR_UPDATE_EVENT` and `CALENDAR_DELETE_EVENT`.

## Functional limit

Kernel does not search for events by title, participant, or similarity. Each
write operation must receive an explicit identifier of the
provider: `Evento: <google_event_id>`. A change also requires `Titulo`,
`Start` and `End` in ISO 8601. This choice prevents an ambiguous message
change the wrong meeting.

## Execution flow

```text
Message
  |
  v
Plan + Task(INTERNAL_ANALYSIS)
  |
  v
CapabilityRequest(UPDATE_EVENT | DELETE_EVENT)
  |
  v
Policy -> ExecutionPreparation(PENDING_APPROVAL)
  |
  v
ApprovalRequest(PENDING)
  |
+-- user rejects --> no call to provider
  |
+-- user approves --> ExecutionRecord(EXECUTING)
                                 |
                                 v
                       Google Calendar API
                                 |
                                 v
                  CapabilityResult(SUCCESS | FAILED)
```

## Contracts

`CalendarEventMutation` represents the validated intent, never the event
full remote. Contains `mutation_id`, `entity_id`, `event_id`, `operation`,
proposed event fields, `status`, `validation_error` and `created_at`.

Supported operations:

- `CALENDAR_UPDATE_EVENT`: replaces fields explicitly provided in the
  remote event; In this first version title, beginning and end are mandatory.
- `CALENDAR_DELETE_EVENT`: removes the event whose `event_id` was provided.

## Mandatory rules

1. An invalid mutation is persisted for auditing, but becomes `BLOCKED` and not
   creates approval request.
2. All Calendar writing requires Policy Enabled and Explicit Approval.
3. `ExecutionRecord` uses the request idempotent key. An approved request
   that is already `COMPLETED` returns the saved result and does not call the
   provider again.
4. `EXECUTING` or unknown states are never automatically repeated
   after restart. Require reconciliation with the provider.
5. The provider receives `sendUpdates=none`; guest notifications are off
   of this slice and require their own capability.
6. No approximate title matches are allowed in this slice.

## Accepted input formats

```text
Reschedule the event | Event: abc123 | Title: Review | Start: 2026-07-20T15:00:00+01:00 | End: 2026-07-20T16:00:00+01:00

Cancela o evento | Evento: abc123
```

Field names without accents are recommended in the Windows CLI to avoid
terminal code page dependency.

## Error cases

- Event without identifier: `EVENT_ID_REQUIRED`.
- Change without title, beginning or end: `TITLE_START_END_REQUIRED`.
- Invalid range: `INVALID_EVENT_INTERVAL`.
- Provider not configured: `GOOGLE_CALENDAR_NOT_CONFIGURED`.
- API failure: persist `FAILED`; do not repeat automatically.

## Acceptance criteria

1. Update and delete create a persisted `CalendarEventMutation`.
2. No Google calls occur before `Approval(APPROVED)`.
3. Each approved mutation calls the provider exactly once.
4. Restart, replay or repeated confirmation do not duplicate writing.
5. The trace includes `CAPABILITY_REQUEST`, `CALENDAR_EVENT_MUTATION`,
   `APPROVAL_REQUEST`, `EXECUTION` and `CAPABILITY_RESULT`.