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
└─ Aurora Applications: 06 Ferramentas → 060 SDK e providers de capacidades

Strategy and operation
052 Missions → 05 Objectives/Plans/Tasks → 07 Personality → 08 Learning
                         │
                         ▼
09 Security → 10 API → 11 UI → 12 Operation → 13 Roadmap → 090 Review Gate
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
| 12 | Implementation and operation | 09–11 |
| 13 | Roadmap and acceptance | 00–12 |
| 060 | Plugins and Extensions SDK | 06, 09, 040 |
| 090 | Architecture Review Gate | 000–060 |
| 100 | Implementation order | Architecture Freeze v1.0 |
| 105 | Testing strategy | Laws, 090, 100 |
| 110 | Coding Standard | 040, 050, 090, 100 |
| 120 | Definition of Done | 100, 105, 110 |

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
