# VS-019 — Google Calendar

The `GoogleCalendarProvider` provider uses local OAuth, queries `freeBusy`, and creates events in the `primary` calendar. `credentials.json` and the OAuth token are local and ignored by Git. Creation receives an Aurora idempotence key as event ID; the executor and the policy remain the authorization boundaries before any integration into the workflow.