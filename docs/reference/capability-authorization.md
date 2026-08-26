# Reference — capability authorization and event contracts

**Generated from the running registry. Do not edit by hand.**
`AuthorizationMatrixTests` fails when this file and the code disagree.

Closes condition 5 of `docs/reviews/architecture-review-v1.0.md`.

## Capability authorization

| Action | Risk | Effects | Approval | Consent path |
| --- | --- | --- | --- | --- |
| `clock.now` | Low | none — reads only | not required | automatic (LOW, effect-free) |
| `echo.say` | Low | none — reads only | not required | automatic (LOW, effect-free) |
| `files.organise_sandbox` | High | files.read, files.write, files.move | required | persisted approval, one-time, scoped to this exact input |
| `files.read_sandbox` | Medium | none — reads only | required | persisted approval, one-time, scoped to this exact input |
| `files.write_sandbox` | Medium | files.write | required | persisted approval, one-time, scoped to this exact input |
| `memory.recall` | Low | none — reads only | not required | automatic (LOW, effect-free) |
| `memory.remember` | Medium | memory.write | required | persisted approval, one-time, scoped to this exact input |

## Declared events (LAW-007)

| Type | v | Producer | Class | Payload | Consumers |
| --- | --- | --- | --- | --- | --- |
| `ExternalObservationReported` | 1 | `api` | PRIVATE | what an outside surface observed, as reported; unverified by construction | perception |
| `BeliefChallenged` | 1 | `beliefs` | PRIVATE | belief_id and that it was contradicted; never the claim itself | ui, attention, review |
| `DevelopmentStageChanged` | 1 | `development` | PRIVATE | the stage moved from and to, and whether autonomy grew or shrank | ui, audit, review |
| `IdentityActivated` | 1 | `identity` | PRIVATE | which profile version became active, and who approved it | ui, audit, review |
| `ApprovalDecided` | 1 | `kernel` | PRIVATE | approval_id, the decision, and the action it was for | ui, audit, review |
| `KernelCommandAccepted` | 1 | `kernel` | PRIVATE | action_id and how it was resolved | audit, review |
| `PluginQuarantined` | 1 | `kernel` | PRIVATE | plugin_id and why it was held; never what the plugin returned | ui, audit, review |
| `LifeEpisodeVerified` | 1 | `life-history` | PRIVATE | episode_id and its kind; never the narrative | ui, review |
| `MaintenancePassCompleted` | 1 | `maintenance` | PRIVATE | counts of what one upkeep pass expired, noticed and reconciled | review, metrics |
| `MemoryForgotten` | 1 | `memory` | PRIVATE | memory_id and what the retraction actually removed; never the content | ui, audit, reflection |
| `MemoryRevised` | 1 | `memory` | PRIVATE | memory_id, the operation and who asked for it; never the content | ui, audit, reflection |
| `MissionChanged` | 1 | `missions` | PRIVATE | mission_id and the status it moved to; never the purpose text | ui, review |
| `ConversationTurnReceived` | 1 | `pilot` | PRIVATE | length of the turn; the words themselves stay in the conversation record | attention, audit |
| `GoalDrafted` | 1 | `planner` | PRIVATE | goal_id and its status; never the outcome text | ui, needs, review |
| `RelationshipEnded` | 1 | `relationships` | PRIVATE | relationship_id and that its interval closed; never who it was with | ui, world, review |
| `ReviewRequested` | 1 | `review` | PRIVATE | the audit cursor the review started from | audit |
| `JobDue` | 1 | `scheduler` | PRIVATE | run_id and the schedule's target; the run itself is not started by this | cycle, review |
| `ScheduleDisabled` | 1 | `scheduler` | PRIVATE | the status it moved to and why it stopped firing | ui, needs, review |
| `ScheduleRunsMissed` | 1 | `scheduler` | PRIVATE | how many occurrences were missed and under which policy | ui, needs, review |
| `SecurityIncidentOpened` | 1 | `security` | PRIVATE | the severity, the kind, and how many things were revoked; never the evidence | ui, review, audit |
| `OperationalStateChanged` | 1 | `self` | PRIVATE | the operational state moved from and to; published on transition, never on every reading | ui, review, metrics |

An event type absent from this table cannot be published: the outbox
refuses it, whichever producer asks. `api` is the only producer reachable
from outside Aurora, and it may emit exactly one type.

