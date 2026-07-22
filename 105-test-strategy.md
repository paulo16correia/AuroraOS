# Aurora Platform — 105: Testing Strategy v1.0

**Type:** quality and compliance · **Depends on:** Laws, RFC 090, document 100

## Objective

Define tests before each module. Tests validate contracts and invariants, not just isolated functions or language responses.

## Testing pyramid

```text
Law Compliance/E2E Recovery and Few, Critical Actions
Integration: events, persistence, policy, moderated providers
Contract: schemas, APIs, manifests, many migrations
Unity: state machines and pure rules many
```

## Mandatory suite

| Area | Minimum cases |
| --- | --- |
| LAW-001 | attempt to write directly to Mind is rejected; candidate without provenance does not consolidate. |
| LAW-002 | Mind does not have a tool/secret client; ToolCall without Decision/Policy fails. |
| LAW-003 | each Action ends in Observation; timeout generates `UNKNOWN` and reconciliation. |
| LAW-004 | orphan memory does not enter `ACTIVE`; relationships retain evidence. |
| LAW-007 | event duplication does not double effect; incompatible consumer goes to controlled failure. |
| LAW-008 | provider cannot describe an identity whose `entity_id` or `identity_ref` has not been validated by the Kernel. |
| Persistence | create memory, restart, restore isolated snapshot, commit data and audit. |
| Goals/Needs | Goal is only updated by Observation/Reflection; Need does not create Action/Task/Plan without later decision and approval. |
| VS-005 Planning | explicit request creates `Plan(PROPOSED, SIMULATION_ONLY)` with valid references; shutdown/restore preserves it; the trace does not contain `ActionCreated`, `TaskCreated` or capacity events. |
| Tasks VS-006 | Explicit Plan creates `Task(INTERNAL_ANALYSIS)` and only accepts `READY → RUNNING → COMPLETED|FAILED`; restore does not rerun; completion generates Goal progress; external request does not create Action, ToolCall or CapabilityRequest. |
| VS-007 Capabilities | active internal catalog and disabled external catalog is persisted; email request creates `CapabilityRequest(EMAIL_SEND, UNAVAILABLE)` without Action, ToolCall, provider or external effect. |
| VS-007.1 Alignment | email request after a historical Task creates/loads request before Thought and ends `CAPABILITY(UNAVAILABLE)`; the historical Task does not increase the Goal again. |
| VS-008 Executor | eligible request produces a single idempotent `CapabilityResult(NOT_IMPLEMENTED|DRY_RUN)`; result does not change request or produce Action, ToolCall, provider or external effect. |
| States | every valid transition is accepted; each invalid hop is rejected with an error code. |
| Security | default denial, immediate revocation, redaction of secrets and injection into external content. |
| Capabilities | intent resolve provider allowed; fallback does not change recipient/effect without decision. |
| Scheduler/Signals | critical priority trumps low work; quiet hours do not block authorized critical procedures. |

## Data rules and AI

- Fixtures are synthetic; Real data requires authorization and a segregated environment.
- LLMs are replaced by deterministic doubles in contract tests; Model evaluations are separate, versioned suites and do not authorize effects.
- Tests are deterministic whenever possible; time, UUID, network and providers are controlled.

## Failure criteriaAny violation of Law, loss of auditability, duplication of effect or exposure of secrets blocks release. Numerical coverage does not replace risk and recovery cases.