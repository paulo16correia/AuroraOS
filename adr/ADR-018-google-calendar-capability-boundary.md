# ADR-018 — Calendar Create Event as approved capability

## Context

The Google Calendar provider already authenticates through local OAuth, but a provider should not be invoked directly by the Kernel or by an MCP tool call.

## Decision

Enter `CALENDAR_CREATE_EVENT` as your own external capability. Creation requires an active policy, a valid `CalendarEventDraft` and a persisted `ApprovalRequest`. `CalendarCreateEventExecutor` is the only adapter that can call `GoogleCalendarProvider.create_event`.

## Consequences

The stream is auditable, recoverable, and idempotent. The first language interface requires explicit ISO 8601 values, preserving the boundary between natural language interpretation and calendar effects. The `freeBusy` query will be a future reading capability, with its own policy.