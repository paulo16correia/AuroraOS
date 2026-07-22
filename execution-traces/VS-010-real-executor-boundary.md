# Aurora OS — Execution Trace Specification: VS-010 Real Executor Boundary

**Status:** Authorized by ADR-010
**Use case:** email request unavailable → preparation by email executor → explicit blocking → no external effects

## Objective

Replace VS-008's inert executor with a per-operation execution boundary. VS-010 introduces specific executors who know how to prepare the type of work that belongs to them, but prohibits external execution until there is user approval in VS-011.

## Normative flow

```text
CapabilityRequest(EMAIL_SEND, UNAVAILABLE)
        ↓
ExecutorRegistry.resolve(EMAIL_SEND)
        ↓
EmailSendExecutor.prepare(...)
        ↓
ExecutionPreparation
  operation: EMAIL_SEND
  status: BLOCKED
  block_reason: CAPABILITY_UNAVAILABLE
  approval_required: true
        ↓
CapabilityResult(BLOCKED)
        ↓
Response: execution was blocked; no email was sent
```

## Contracts

```text
CapabilityExecutor.prepare(CapabilityRequest, Capability) -> ExecutionPreparation

ExecutionPreparation
  preparation_id: execution-preparation:{request_id}
  entity_id, request_ref, capability_ref, executor_id
  operation
  status: BLOCKED|PREPARED
  block_reason: str|null
  approval_required: bool
  created_at
````

ExecutionPreparation` is a prepared intention, never an authorization and never an action. A `CapabilityResult(BLOCKED|AWAITING_APPROVAL)` explicitly states that external execution did not happen.

## Architecture

```text
Kernel
  → ExecutorDispatcher
      → ExecutorRegistry
          ├─ EMAIL_SEND → EmailSendExecutor
          └─ restantes → UnsupportedCapabilityExecutor
      → ExecutionPreparation persistida
      → CapabilityResult persistido

There is no Executor path for SMTP, HTTP, browser, shell, filesystem or vault.
```

## Mandatory rules

1. The Dispatcher resolves the capability to an executor; the Kernel does not know details of the operation.
2. VS-010 exclusively allows `prepare`; There is no external execution method invokeable by the cognitive cycle.
3. Blocked preparations must register a structured ratio.
4. Even a `PREPARED` preparation requires human approval; does not produce an external effect.
5. Preparation and result are idempotent by `CapabilityRequest` and survive shutdown/restore.
6. Each preparation belongs to the same Entity as the request and capability.
7. `NOOP_EXECUTOR` is removed; an executorless capability uses a blocked and auditable fallback.

## Events and trace

| Situation | Event | Trace |
| --- | --- | --- |
| Preparation created | `ExecutionPrepared` | `EXECUTION_PREPARATION(BLOCKED|PREPARED)` |
| Preparation recharged | `ExecutionPreparationLoaded` | `EXECUTION_PREPARATION(LOADED)` |
| Security blocks | `ExecutionBlocked` | `EXECUTION_BLOCKED` |
| Limit result | `CapabilityResultCreated` | `CAPABILITY_RESULT(BLOCKED|AWAITING_APPROVAL)` |

## Limit cases

| Condition | Result |
| --- | --- |
| Capability unavailable | `BLOCKED/CAPABILITY_UNAVAILABLE` |
| Capability without executor | `BLOCKED/NO_EXECUTOR_REGISTERED` |
| Capability of another Entity | integrity error; does not persist preparation |
| Repetition of the same request | recharge existing preparation and result |
| Entity restored | preparation references are included in `MindState` |

## Acceptance criteria

1. An email request produces `ExecutionPreparation(EMAIL_SEND, BLOCKED)` and `CapabilityResult(BLOCKED)`.
2. The trace contains `EXECUTION_PREPARATION` and `EXECUTION_BLOCKED` before the response.
3. There are no network calls, plugins, SMTP, tools, `Action` or credentials.
4. Staging is persisted, auditable, and restorable.
5. All VS-000–VS-009 tests remain green.

## Deliberate limits

VS-010 does not activate email, collect recipient/body, create approval, perform operations or provide additional tools. VS-011 will introduce the approval object and limit; VS-012 could introduce a real tool under this boundary.