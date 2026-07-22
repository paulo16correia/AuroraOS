# ADR-019 — Own policy for Calendar Free/Busy queries

## Decision

`CALENDAR_FREEBUSY` is a separate reading capability from `CALENDAR_CREATE_EVENT`. The initial policy is `NOT_REQUIRED` for approval because the user's direct request authorizes a single query to their own calendar, without producing an external effect.

## Consequences

This decision maintains the natural conversational experience for availability questions, but preserves the policy as a security boundary: an installation can change the persisted policy to `REQUIRED`, restrict the principal, or disable the capability. Writing operations always remain subject to explicit approval.