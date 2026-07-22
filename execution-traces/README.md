# Canonical catalog of Vertical Slices

This file is the source of truth for the numbering of Vertical Slices (VS).
A feature can only use a number after being registered here. A VS is made up of
an execution trace, code in `aurora-kernel`, automated tests and, when
changes an architectural decision, an ADR.

## Numbering rule

- A number identifies a single end-to-end behavior and is never reused.
- A corrective adjustment uses the suffix `.1` (for example, `VS-007.1`).
- If a proposal is abandoned, the number becomes `RESERVED` or `REPLACED`; is not renumbered.
- This catalog takes precedence over planning messages, notes and branch names.

## Implementation line

| VS | Canonical behavior | Status | Documentary evidence |
| --- | --- | --- | --- |
| 000–017 | Core, Entity, Mind, Tasks, capabilities, approval, Event Engine | Implemented | `VS-000` to `VS-017` |
| 018 | `WAIT_FOR_TIME` Idempotent Scheduler | Implemented | `VS-018-persistent-scheduler.md` |
| 019 | Provider Google Calendar and local OAuth | Implemented | `VS-019-google-calendar.md` |
| 019.1 | Calendar in the capability pipeline | Implemented | `VS-019.1-calendar-capability-integration.md` |
| 020 | Query Google Calendar Free/Busy | Implemented | `VS-020-calendar-freebusy.md` |
| 021 | Calendar Orchestration + Email with independent approvals | Implemented | `VS-021-multi-capability-orchestration.md` |
| 022 | Explicit entity resolution for recipients | Implemented | `VS-022-entity-resolution.md` |
| 023 | Conservative compensation of failed branches | Implemented | `VS-023-workflow-compensation.md` |
| 024 | Recurring workflows | Implemented | `VS-024-recurring-workflows.md` |
| 025 | Event lifecycle Google Calendar | Implemented | `VS-025-calendar-event-lifecycle.md` |
| 026 | World Model: explicit relationships with provenance | Implemented | `VS-026-world-model-foundation.md` |
| 027 | Temporal World Model: transitions to history | Implemented | `VS-027-temporal-world-assertions.md` |
| 028 | Temporal World Model: reactivation of relationships | Implemented | `VS-028-world-relationship-reactivation.md` |
| 029 | Temporal World Model: as-of queries | Implemented | `VS-029-temporal-as-of-queries.md` |
| 030 | World Model: relations in dispute | Implemented | `VS-030-world-assertion-disputes.md` |
| 031 | World Model: explicit correction of disputed relationship | Implemented | `VS-031-world-assertion-corrections.md` |

## History of resolved collisions

During planning, `VS-021` was temporarily used for Calendar
Update/Delete and then for multi-capability orchestration. The meaning
canonical is **multi-capability orchestration**. Changing and removing eventsof Calendar are a future expansion and will receive another number.

## Next slice

VS-031 closes the explicit creation of a surrogate relationship after a dispute.
The catalog waits for the next behavior to be prioritized. Any new VS
it must be registered here before receiving implementation in the Kernel.