# Reference — capability authorization and event contracts

**Generated from the running registry. Do not edit by hand.**
`AuthorizationMatrixTests` fails when this file and the code disagree.

Closes condition 5 of `docs/reviews/architecture-review-v1.0.md`.

## Capability authorization

| Action | Risk | Effects | Approval | Consent path |
| --- | --- | --- | --- | --- |
| `clock.now` | Low | none — reads only | not required | automatic (LOW, effect-free) |
| `echo.say` | Low | none — reads only | not required | automatic (LOW, effect-free) |
| `files.read_sandbox` | Medium | none — reads only | required | persisted approval, one-time, scoped to this exact input |
| `files.write_sandbox` | Medium | files.write | required | persisted approval, one-time, scoped to this exact input |
| `memory.recall` | Low | none — reads only | not required | automatic (LOW, effect-free) |
| `memory.remember` | Medium | memory.write | required | persisted approval, one-time, scoped to this exact input |

## Declared events (LAW-007)

| Type | v | Producer | Class | Payload | Consumers |
| --- | --- | --- | --- | --- | --- |
| `ExternalObservationReported` | 1 | `api` | PRIVATE | what an outside surface observed, as reported; unverified by construction | perception |
| `ApprovalDecided` | 1 | `kernel` | PRIVATE | approval_id, the decision, and the action it was for | ui, audit, review |
| `KernelCommandAccepted` | 1 | `kernel` | PRIVATE | action_id and how it was resolved | audit, review |
| `MaintenancePassCompleted` | 1 | `maintenance` | PRIVATE | counts of what one upkeep pass expired, noticed and reconciled | review, metrics |
| `MemoryForgotten` | 1 | `memory` | PRIVATE | memory_id and what the retraction actually removed; never the content | ui, audit, reflection |
| `MemoryRevised` | 1 | `memory` | PRIVATE | memory_id, the operation and who asked for it; never the content | ui, audit, reflection |
| `ConversationTurnReceived` | 1 | `pilot` | PRIVATE | length of the turn; the words themselves stay in the conversation record | attention, audit |
| `ReviewRequested` | 1 | `review` | PRIVATE | the audit cursor the review started from | audit |
| `JobDue` | 1 | `scheduler` | PRIVATE | run_id and the schedule's target; the run itself is not started by this | cycle, review |
| `ScheduleDisabled` | 1 | `scheduler` | PRIVATE | the status it moved to and why it stopped firing | ui, needs, review |
| `ScheduleRunsMissed` | 1 | `scheduler` | PRIVATE | how many occurrences were missed and under which policy | ui, needs, review |

An event type absent from this table cannot be published: the outbox
refuses it, whichever producer asks. `api` is the only producer reachable
from outside Aurora, and it may emit exactly one type.

