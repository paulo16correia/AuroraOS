# Aurora OS — Architecture Specification

This directory is the normative source for building Aurora OS. The RFCs use the words **MUST**, **MUST NOT**, **SHOULD**, **SHOULD NOT** and **MAY** in the mandatory, prohibited, recommended, advised against and optional sense.

## MCP-first architecture

Aurora OS is a Cognitive Operating System, not an LLM wrapper. A compatible LLM client uses the Aurora Kernel through the Model Context Protocol (MCP). The LLM handles natural-language understanding, conversation, clarification, tool selection, and response writing. Aurora remains the persistent digital entity and owns identity, memory, world model, planning, decisions, policy, approvals, execution, scheduling, audit, and persistence.

```text
User → LLM Client → MCP → Aurora Kernel → MCP result → LLM Client → User
```

RFC 021 defines the governed cognitive cycle, RFC 045 defines the Kernel boundary, RFC 10 defines MCP and API integration contracts, and VS-009 preserves the migration trace identifier.

## Reading order and dependencies

```text
Normative foundation
000 Philosophy → 00 Vision → 01 Principles → 035 Constitution → laws/
                                  │
                                  ▼
036 Genome → 037 Development → 038 History → 039 Life cycle
                                  │
                                  ▼
Aurora Platform
├─ Aurora Kernel: 045 Kernel → 050 Event Bus → 051 Capabilities
├─ Aurora Mind: 010 Map → 011 Layers → 020 Mind → 040 Domain
│ ├─ 021 Cycle → 022 Decision → 023 Attention → 024 Work → 025 Deliberation
│ ├─ 027 Self → 028 Beliefs → 029 Relationships/Preferences
│ ├─ 030 Signs → 031 Needs → 032 Curiosity → 033 Resources → 034 Situation
│ └─ 03 Memory → 04 Graph → 041 World → 042 Time → 043 Mind State
└─ Aurora Applications: 06 Tools → 060 SDK and capability providers

Strategy and operation
052 Missions → 05 Objectives/Plans/Tasks → 07 Personality → 08 Learning
                         │
                         ▼
09 Security → 10 API → 11 UI → 13 Roadmap → 090 Review Gate
                                                                    │
                                                                    ▼
Freeze v1.0 → 100 Implementation order → 105 Tests → 110 Standards → 120 Done
                                                                    │
                                                                    ▼
Execution Trace VS-000 → first Kernel code
```

A later RFC cannot contradict a previous one without creating an architectural decision (ADR) that explains the change, the impact on migration, and the version from which it takes effect.

## Common conventions

- Identifiers are UUIDv7; times are UTC in ISO-8601; amounts are integers in the smallest monetary unit.
- All persisted data includes `id`, `created_at`, `updated_at`, `version`, and `tenant_id` when applicable.
- All commands with external effect include `correlation_id`, `idempotency_key`, author, applied policy and result.
- `CONFIDENTIAL` and `SECRET` are never shipped to a model or connector without express policy authorization.
- The first version is single-user, but the data model preserves tenant boundaries for future evolution.

## Index

| RFC | Theme | Direct dependencies |
| --- | --- | --- |
| 000 | Architectural philosophy | — |
| 00 | Vision | 000 |
| 01 | Principles and governance | 00 |
| 035 | Constitution of Aurora | 000–01, Laws |
| Laws | Architectural invariants | Constitution |
| LAW-007 | Event-mediated communication | Constitution |
| LAW-008 | Identity integrity through the Self Model | RFC 027, RFC 040 |
| 036 | Genome | 035, 040 |
| 037 | Development model | 07, 027–028, 036, 040 |
| 038 | Life story | 020, 036–037, 040, 043 |
| 039 | Instance lifecycle | 011, 026–027, 033, 036, 043 |
| 010 | Mind Master Map | 000–01 |
| 011 | Cognitive layers and boundaries | 000, 010 |
| 020 | Mind — cognitive state model | 000, 01, 010 |
| 021 | Cognitive cycle | 020, 040 |
| 022 | Decision engine | 01, 021, 040 |
| 023 | Attention system | 03–04, 020–021 |
| 024 | Working memory | 03, 021, 023, 040 |
| 025 | Internal deliberation | 021–024 |
| 026 | Scheduler | 01, 021–022, 040 |
| 027 | Self — operational self-awareness | 011, 020, 040 |
| 028 | Belief system and uncertainty | 03, 040–041 |
| 029 | Relationships and preferences | 04, 028, 040–042 |
| 030 | Signal system | 011, 021, 040, LAW-001 |
| 031 | Operational needs | 020, 026, 030, 033, 040 |
| 032 | Curiosity ruled | 028, 030–031, 033–034, LAW-006 |
| 033 | Resources and operational energy | 027, 040 |
| 034 | Operational situational awareness | 027, 030–033, 042–043 |
| 040 | Domain Model | 020 |
| 041 | World Model | 04, 040 |
| 042 | Temporal model and validity | 040–041 |
| 043 | Mind State and continuity | 020, 024, 027–029, 040–042 |
| 045 | Aurora Kernel | 011, 035, 039–040, 043 |
| 050 | Event Bus | LAW-007, 040, 045 |
| 051 | Capacity system | 06, 045, 050 |
| 052 | Missions, objectives and tasks | 05, 035, 040, 051 |
| 02 | Cognitive core (implementation orchestrator) | 00–01; refined by 021–025 |
| 03 | Memory | 01–02 |
| 04 | Knowledge graph | 03 || 05 | Objectives and planning | 02–03 |
| 06 | Tool system | 01–02, 05 |
| 07 | Personality and identity | 01–03 |
| 08 | Learning and reflection | 02–07 |
| 09 | Security | 01–08 |
| 10 | API and events | 02–09 |
| 11 | User interface | 10 |
| 13 | Roadmap and acceptance | 00–11 |
| 060 | Plugins and Extensions SDK | 06, 09, 040 |
| 090 | Architecture Review Gate | 000–060 |
| 100 | Implementation order | Architecture Freeze v1.0 |
| 105 | Testing strategy | Laws, 090, 100 |
| 110 | Coding Standard | 040, 050, 090, 100 |
| 120 | Definition of Done | 100, 105, 110 |

### Withdrawn

| RFC | Theme | Withdrawn by |
| --- | --- | --- |
| 12 | Implementation and operation | `adr/0045-local-only.md` |

RFC 12 assumed a deployed installation — a VPS, a container, a reverse proxy, a release pipeline.
Aurora runs on the owner's own machine and nowhere else, so the RFC was withdrawn rather than left
in the index as an obligation nobody intends to meet. The operational half it also covered — backup,
restore verification, health, metrics — was kept and is implemented; it is a single machine's
operations, not a deployment's.

## Post-freeze governance

- [Architecture Freeze v1.0](governance/architecture-freeze-v1.0.md)
- [Architecture Review v1.0](reviews/architecture-review-v1.0.md)
- [Dependency matrix](reviews/dependency-matrix-v1.0.md)
- [ADRs](adr/README.md)
- [Repository topology](governance/repository-topology.md)
- [Execution Trace VS-000](execution-traces/VS-000-message-response.md)
- [Execution Trace VS-001 — Resurrection](execution-traces/VS-001-resurrection.md)
- [Execution Trace VS-002 — Entity Birth + Self Model](execution-traces/VS-002-entity-birth-self-model.md)
- [Execution Trace VS-003 — Memory Retrieval + Personal Continuity](execution-traces/VS-003-memory-retrieval-personal-continuity.md)
- [Execution Trace VS-004 — Goals + Controlled Agency Loop](execution-traces/VS-004-goals-controlled-agency.md)
- [Execution Trace VS-005 — Persistent Planning Layer](execution-traces/VS-005-persistent-planning-simulation.md)
- [Execution Trace VS-006 — Controlled Internal Execution Loop](execution-traces/VS-006-controlled-execution-loop.md)
- [Execution Trace VS-007 — Capability Framework](execution-traces/VS-007-capability-framework.md)
- [Execution Trace VS-007.1 — Capability Pipeline Alignment](execution-traces/VS-007.1-capability-pipeline-alignment.md)
- [Execution Trace VS-008 — Executor Interface](execution-traces/VS-008-executor-interface.md)
- [Aurora OS — Execution Trace Specification: VS-009 LLM Adapter](execution-traces/VS-009-llm-adapter.md)
- [Aurora OS — Execution Trace Specification: VS-010 Real Executor Boundary](execution-traces/VS-010-real-executor-boundary.md)
- [Aurora OS — Execution Trace Specification: VS-011 Persistent User Approval](execution-traces/VS-011-persistent-user-approval.md)
- [Aurora OS — Execution Trace Specification: VS-012 Idempotent Email Execution](execution-traces/VS-012-idempotent-email-execution.md)
- [Aurora OS — Execution Trace Specification: VS-013 Capability SDK](execution-traces/VS-013-capability-sdk.md)
- [VS-014 — Capabilities Policies](execution-traces/VS-014-capability-policies.md)
- [VS-015 — Controlled Multi-Step Planes](execution-traces/VS-015-multi-step-plans.md)
- [VS-016 — Persistent Workflow Engine](execution-traces/VS-016-workflow-engine.md)
- [VS-017 — Event Engine](execution-traces/VS-017-event-engine.md)
- [VS-018 — Persistent Scheduler](execution-traces/VS-018-persistent-scheduler.md)
- [VS-019 — Google Calendar](execution-traces/VS-019-google-calendar.md)
- [VS-019.1 — Capability Calendar Integration](execution-traces/VS-019.1-calendar-capability-integration.md)
- [VS-020 — Calendar Free/Busy](execution-traces/VS-020-calendar-freebusy.md)
- [VS-021 — Multi-capability orchestration](execution-traces/VS-021-multi-capability-orchestration.md)
- [VS-022 — Entity resolution](execution-traces/VS-022-entity-resolution.md)
- [VS-023 — Workflow Compensation](execution-traces/VS-023-workflow-compensation.md)
- [VS-024 — Recurring workflows](execution-traces/VS-024-recurring-workflows.md)
- [VS-025 - Calendar Event Lifecycle](execution-traces/VS-025-calendar-event-lifecycle.md)
- [VS-026 - World Model: explicit relationships with provenance](execution-traces/VS-026-world-model-foundation.md)
- [VS-027 - Temporal World Model: transitions to historical](execution-traces/VS-027-temporal-world-assertions.md)
- [VS-028 - Temporal World Model: reactivation of relationships](execution-traces/VS-028-world-relationship-reactivation.md)
- [VS-029 - Temporal World Model: as-of queries](execution-traces/VS-029-temporal-as-of-queries.md)
- [VS-030 - World Model: relations in dispute](execution-traces/VS-030-world-assertion-disputes.md)
- [VS-031 - World Model: explicit correction of disputed relationships](execution-traces/VS-031-world-assertion-corrections.md)
- [Canonical catalog of Vertical Slices](execution-traces/README.md)
