# ADR-003 — Goals and Controlled Agency Cycle

**Status:** ACCEPT
**Date:** 2026-07-17
**RFCs affected:** RFC 05, RFC 031, RFC 033, RFC 040, RFC 043, LAW-006

## Context

VS-003 proved that an Entity preserves external facts between sessions. The operational reason that guides continuity remains: active objectives, verifiable progress, operational status and explicit needs.

## Decision

VS-004 creates, at the birth of the Entity, a continuous Goal `ASSIST_USER` derived from the purpose approved in `Identity`, an operational `EntityState` and a Need `UNDERSTAND_USER`. On each completed cycle, the Kernel evaluates the Goal using Observation/Reflection and updates the state. Needs are just internal proposals: they do not create Actions, Tasks, Plans, messages or external calls.

## Consequences

- A provider only receives active Goals loaded by the Kernel and can only describe them when they have been persisted and validated for the active Entity.
- VS-004 progress is an assistance evidence counter, not a percentage of completion. A continuous Goal remains `ACTIVE` even after accumulating evidence.
- The initial version does not create Goals from conversation, schedule work or run tools. Creating additional Goals requires a later slice and explicit request/approval.

## Migration and rollback

VS-001–VS-003 entities receive audited compatibility Goal, EntityState, and Need on first VS-004 startup. Rollback disables the evaluation and display of objects, but preserves them for auditing; does not delete existing Identity, memory or history.