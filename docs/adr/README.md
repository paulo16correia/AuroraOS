# ADR — Architecture Decision Records

ADRs record changes to the frozen baseline. An RFC is the stable contract; an ADR explains why, how and from what version this contract changes.

## Mandatory format

```text
ADR-<number>-<name>.md
Status: PROPOSED | ACCEPTED | REJECTED | SUPERSEDED
Date, deciders, affected RFCs
Context, decision, alternatives, consequences
Migration, compatibility, testing and rollback plan
```

## Process

1. Open ADR with concrete problem and evidence of implementation/review.
2. Evaluate Constitution, Laws, security, state, events and dependencies.
3. Explicitly accept or reject.
4. Only an ADR `ACCEPTED` authorizes changing affected RFCs, code or migrations.

## Index

| ADR | Decision | Status |
| --- | --- | --- |
| [ADR-000](ADR-000-freeze-and-change-control.md) | Freeze v1.0 and change control | Accepted |
| [ADR-001](ADR-001-entity-runtime-context.md) | Entity Runtime Context | Accepted |
| [ADR-002](ADR-002-identity-integrity-and-personal-continuity.md) | Identity Integrity and Personal Continuity | Accepted |
| [ADR-003](ADR-003-goals-and-controlled-agency-loop.md) | Goals and Controlled Agency Cycle | Accepted |
| [ADR-004](ADR-004-persistent-planning-simulation-only.md) | Persistent, deliberative, simulation-only planning | Accepted |
| [ADR-005](ADR-005-tasks-and-internal-execution.md) | Tasks and controlled internal execution cycle | Accepted |
| [ADR-006](ADR-006-capability-assessment-framework.md) | Non-Execution Capabilities Assessment Framework | Accepted |
| [ADR-007](ADR-007-capability-pipeline-alignment.md) | CapabilityRequest pipeline alignment | Accepted |
| [ADR-008](ADR-008-executor-interface-dry-run-only.md) | Executor Interface with results without external effect | Accepted |
| [ADR-009](ADR-009-llm-proposal-boundary.md) | LLM proposal boundary | Accepted (MCP-first migration) |
| [ADR-010](ADR-010-operation-specific-executor-boundary.md) | Boundary of specific executors per operation | Accepted |
| [ADR-011](ADR-011-persistent-user-approval.md) | Explicit and persistent user approval | Accepted |
| [ADR-012](ADR-012-idempotent-email-execution.md) | Idempotent execution of EMAIL_SEND | Accepted |
| [ADR-013](ADR-013-capability-sdk.md) | Stable capabilities SDK | Accepted |
| [ADR-014](ADR-014-capability-policies.md) | Persistent Policy Before External Capabilities | Accepted |
| [ADR-015](ADR-015-multi-step-task-dependencies.md) | Explicit dependencies between tasks in a plan | Accepted |
| [ADR-016](ADR-016-persistent-workflow-waits.md) | Persistent waits in the Workflow Engine | Accepted |
| [ADR-017](ADR-017-persistent-workflow-events.md) | Persistent events unblock workflows | Accepted |
| [ADR-018](ADR-018-google-calendar-capability-boundary.md) | Calendar Create Event as approved capability | _not recorded_ |
| [ADR-019](ADR-019-calendar-read-policy.md) | Own policy for Calendar Free/Busy queries | _not recorded_ |
| [ADR-020](ADR-020-independent-approval-for-workflow-branches.md) | Independent approval for workflow branches | _not recorded_ |
| [ADR-021](ADR-021-explicit-contact-provenance.md) | Explicit provenance for contacts | _not recorded_ |
| [ADR-022](ADR-022-conservative-workflow-compensation.md) | Conservative workflow compensation | Accepted |
| [ADR-023](ADR-023-explicit-calendar-event-mutations.md) | Calendar mutations require explicit identifier | Accepted |
| [ADR-024](ADR-024-world-assertions-are-evidence-first.md) | World Assertions are evidence-oriented | Accepted |
| [ADR-025](ADR-025-world-history-is-append-preserving.md) | The World Model preserves history by ending relationships | Accepted |
| [ADR-026](ADR-026-world-reactivation-creates-a-new-interval.md) | Reactivation creates a new time interval | Accepted |
| [ADR-027](ADR-027-temporal-queries-use-half-open-intervals.md) | Temporal queries use half-open ranges | Accepted |
| [ADR-028](ADR-028-disputes-preserve-evidence-without-inference.md) | Disputes preserve evidence without inference | Accepted |
| [ADR-029](ADR-029-explicit-world-assertion-corrections.md) | Corrections create new assertions and preserve disputes | Accepted |
