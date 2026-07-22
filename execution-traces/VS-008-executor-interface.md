# Aurora OS — Execution Trace Specification: VS-008 Executor Interface

**Status:** Authorized by ADR-008
**Use case:** external request unavailable → reference executor → persistent result no effect

## Objective

Define contractual bridge to execution without executing actual capabilities. VS-008 proves that the Kernel can dispatch a request to a separate component and receive a result that can be audited.

## Normative flow

```text
User: "Send an email for me"
  → Task(INTERNAL_ANALYSIS, COMPLETED)
  → CapabilityRequest(EMAIL_SEND, UNAVAILABLE)
  → Executor(NoopExecutor)
  → CapabilityResult(NOT_IMPLEMENTED)
  → Thought / Decision(RESPOND)
→ Response: no email was sent
  → Observation / Reflection

No Action, ToolCall, provider, network, secret or external call.
```

## Minimum contracts

```text
Executor.execute(request, capability) -> CapabilityResult

CapabilityResult
  result_id: "capability-result:{request_id}"
  entity_id
  request_ref: CapabilityRequest
  executor_id: NOOP_EXECUTOR
  status: DRY_RUN|NOT_IMPLEMENTED
  summary
  created_at
```

## Mandatory rules

1. An Executor receives only a persisted `CapabilityRequest` and the Capability referred to by the same Entity.
2. VS-008 only allows `NOOP_EXECUTOR`; any external executor fails to compose the Kernel.
3. `NOT_IMPLEMENTED` and `DRY_RUN` are never proof of external effect, success, shipping or authorization.
4. The request does not change state when the executor returns results.
5. Each result MUST reference the request, the executor and the trace that originated it.
6. The result is idempotent by `request_ref`; repeating the cycle loads it, does not recreate or rerun it.

## Events and trace

| Situation | Event | Trace |
| --- | --- | --- |
| Internal dispatch | `ExecutorInvoked` | `EXECUTOR(NOT_IMPLEMENTED)` |
| Result created | `CapabilityResultCreated` | `CAPABILITY_RESULT(CREATED)` |
| Existing result | `CapabilityResultLoaded` | `CAPABILITY_RESULT(LOADED)` |

## Acceptance criteria

1. An email request creates/retrieves a `CapabilityRequest(UNAVAILABLE)` and a `CapabilityResult(NOT_IMPLEMENTED)`.
2. The result survives shutdown/restore and is not duplicated.
3. Response and trace distinguish `UNAVAILABLE` from `NOT_IMPLEMENTED`.
4. There is no Action, ToolCall, provider, plugin, network, file or secret.
5. VS-000–VS-007.1 remain green.

## Deliberate limits

There is no LLM Adapter, email executor, approval workflow, SDK plugin, scheduler, external queue, retry or temporal autonomy. VS-008 only defines the contract of passing from intention to inert result.