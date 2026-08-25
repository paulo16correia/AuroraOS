# Aurora Platform — Architecture Review v1.0

**Status:** ACCEPT WITH CONDITIONS
**Scope:** frozen v1.0 specification
**Result:** suitable for incremental implementation, subject to the gates below.

## Method

The review evaluated each domain against unique responsibility, state ownership, state machine, lifecycle, events, dependencies, Laws, and recoverability. It is not an implementation validation; it is the baseline that code repositories must meet.

## Consistency result

| Area | Review decision |
| --- | --- |
| Mission/Goal/Plan/Task | Kept separate: lasting purpose, measurable result, revised strategy and executable unity. |
| Memory / Knowledge / World Model / Belief | Kept separate: memory is a record with provenance; knowledge is affirmation; World Model is relational temporal projection; Belief is a reviewable hypothesis. |
| Signal / Need / Attention / Situation | Kept separate: stimulus, operational condition, focus selection and temporal context assessment. |
| Self / Mind State / Lifecycle | Kept separate: operational self-model, serializable snapshot and macro state of the instance. |
| RFC 02/021–025 | Controlled overlap: RFC 02 is implementation coordination; RFC 021–025 are normative for cycle, decision, attention, working memory and deliberation. |
| Tool / Capability / Application | Kept separate: concrete connector, functional intent and packaging/supplier capacity. |

## Mandatory conditions before implementing external writing

1. Implement and test Laws 001–007 as compliance tests.
2. Demonstrate that Kernel does not import or depend on Mind's internal semantics; it only knows interfaces/versioning/process state.
3. Demonstrate outbox, idempotence, Dead Letter Queue and reconciliation of `UNKNOWN` on the Event Bus.
4. Demonstrate isolated snapshot/restore before any connector with effects.
5. Publish event contracts and authorization matrices for each capability.

### Status in the reference implementation

| # | Closed by |
| --- | --- |
| 1 | `docs/adr/0027` — Laws 001–007 as 20 compliance tests. |
| 2 | `docs/adr/0027` — LAW-002 boundary test over Mind-layer signatures; `docs/reviews/dependency-matrix-v1.0.md`. |
| 3 | `docs/adr/0016` — outbox, idempotence, DLQ; `UNKNOWN` reconciliation at startup and in the upkeep pass. |
| 4 | `docs/adr/0018` — isolated snapshot/restore, verified before any connector with effects. |
| 5 | `docs/adr/0036` — `docs/reference/capability-authorization.md`, generated from the registry and held true by `AuthorizationMatrixTests`; undeclared events are refused by the outbox. |

All five are demonstrated. External writing is no longer blocked by this review;
the remaining gate on the sandbox capabilities is the residual TOCTOU risk
recorded in `docs/adr/0003` and closed in `docs/adr/0036`.

## Findings

| ID | Severity | Found | Resolution v1.0 |
| --- | --- | --- | --- |
| AR-01 | Medium | Risk of treating RFC 02 and RFC 021 as different cycles. | ADR-000 sets normative precedence. |
| AR-02 | Medium | Kernel can couple with Mind details during implementation. | Dependency matrix and mandatory boundary tests. |
| AR-03 | Medium | Direct communication may return for convenience. | LAW-007 + outbox/mandatory contracts. |
| AR-04 | Low | Biological metaphors can be interpreted as claims of consciousness. | Documents use “operational”, evidence and observable state. |

## Decision

No flaws were found that justify new concepts. Architecture is frozen; the conditions must be demonstrated in the reference implementation and tracked in the Definition of Done.
