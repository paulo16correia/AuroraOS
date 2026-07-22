# Aurora OS — Execution Trace Specification: VS-013 Capability SDK

**Status:** Authorized by ADR-013
**Use case:** the Kernel resolves a capability by stable contract, without knowing SMTP, files or future APIs.

## Objective

Transform the email runner into a first implementation of an extensible platform. The Kernel coordinates persistence, policies, correlation and auditing; each executor implements only the behavior of its capability.

## Stable contract

```text
CapabilityExecutor
  capability_name: str
  executor_id: str

  prepare(CapabilityRequest, Capability, payload)
      -> ExecutionPreparation

  execute(ExecutionPreparation, ExecutionRecord, payload)
      -> ExecutionOutcome

  recover(ExecutionRecord)
      -> RecoveryDecision
````payload` is a Kernel-validated domain agreement. In VS-013, `EMAIL_SEND` receives `EmailDraft`; Future capabilities can introduce their own payloads without changing the cognitive cycle.

## Flow

```text
CapabilityRequest
  → CapabilityExecutorRegistry.resolve(capability_name)
  → executor.prepare(...)
  → ExecutionPreparation
  → Approval boundary
  → executor.execute(...)
  → ExecutionOutcome
  → CapabilityResult

After restart:
  ExecutionRecord → executor.recover(...) → RecoveryDecision
```

## Ownership

| Component | Responsibility |
| --- | --- |
| Kernel/Dispatcher | persistence, events, idempotence, Entity boundary, Approval boundary |
| Registry | association capability → executor |
| Executor | operation semantics, outcome and recovery decision |
| External provider | Integration-specific I/O |

Executors do not receive repository, event bus, LLM, Mind, or global permissions.

## Mandatory rules

1. The Kernel never contains external vendor-specific conditionals.
2. An executor does not write directly to Aurora state or publish events.
3. An executor only receives a validated payload and the Entity already confirmed by the Dispatcher.
4. Each capability has a maximum of one executor registered per runtime.
5. `recover` is mandatory; the absence of a safe recovery decision is an implementation error.
6. `ExecutionOutcome` is not persisted by the executor; the Dispatcher turns it into `ExecutionRecord` and `CapabilityResult`.
7. Capability without executor resolves to blocked fallback, never to implicit execution.

## EMAIL_SEND migration

`EmailSendExecutor` implements the complete SDK. `SmtpEmailProvider` stands behind this executor. The Dispatcher no longer knows how to send email; it just calls `execute` and persists the returned outcome.

## Acceptance criteria

1. The mail flow continues to produce `SUCCESS`, `FAILED`, and `UNKNOWN` with preserved idempotence.
2. A controlled provider can replace SMTP in tests without changing the Kernel.
3. There is no direct access to an external provider outside of an executor.
4. All VS-000–VS-012 tests remain green.

## Next steps

VS-014 will introduce cross-cutting policies before `execute`. New capabilities, like `FILE_WRITE` or `CALENDAR_CREATE`, will be new executors that implement this contract, not new paths in the Kernel.