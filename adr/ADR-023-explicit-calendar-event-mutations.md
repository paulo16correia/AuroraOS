#ADR-023 - Calendar mutations require explicit identifier

**Status:** Accepted
**Date:** 2026-07-19

## Context

Changing or deleting an event by textual correspondence is dangerous: it can
there are several meetings with the same title, similar time or participants
equal. Natural language interpretation is not a sufficient authority
to choose a destructive remote object.

## Decision

VS-025 requires `Evento: <provider_event_id>` on all Calendar mutations.
For update, the first version also requires a full title, beginning and end.
The Kernel persists this intent as `CalendarEventMutation`, prepares it,
gets Approval and delegates only the approved call to the provider.

## Consequences

- Increases security and auditability of destructive operations.
- Prevents approximate searches and accidental changes.
- Forces the future UI to list events and display their identifiers.
- Keeps open a future evolution for `EVENT_RESOLUTION`, as long as this
  capability produces an explicit choice confirmed by the user.

## Rejected alternatives

- **Choose the first event with a similar title:** non-deterministic and
  insecure.
- **Delegate the choice to the LLM:** the model is not a source of truth for the state
  remote.
- **Allow silent partial update:** makes it difficult to explain the status
  final and increases the risk of removing event data.