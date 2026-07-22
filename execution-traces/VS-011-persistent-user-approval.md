# Aurora OS — Execution Trace Specification: VS-011 Persistent User Approval

**Status:** Authorized by ADR-011
**Use case:** email request → pending preparation → persistent commit → no external execution

## Objective

Introduce explicit, persistent, user-assigned authorization. A confirmation message stops being a transient context: it becomes a `Approval` linked to a `ApprovalRequest`, which in turn references a concrete `ExecutionPreparation`.

## Normative flow

```text
CapabilityRequest(EMAIL_SEND, READY_FOR_APPROVAL)
        ↓
EmailSendExecutor.prepare
        ↓
ExecutionPreparation(PENDING_APPROVAL)
        ↓
ApprovalRequest(PENDING)
        ↓
Response: "I can prepare this shipment. Do you want to confirm?"

--- restart allowed; does not prepare or execute again ---

User: "Sim"
        ↓
Approval(APPROVED, actor_id)
        ↓
ApprovalRequest(APPROVED)
        ↓
Response: approval registered; external execution still disabled
```

VS-011 does not call `Executor.execute`, does not use SMTP, and cannot produce `CapabilityResult(SUCCESS)` for an external action.

## Domain model

```text
ExecutionPreparation 1 ─── 1 ApprovalRequest 0 ─── 1 Approval

ApprovalRequest.status: PENDING | APPROVED | REJECTED | EXPIRED
Approval.decision:       APPROVED | REJECTED
```

```text
ApprovalRequest
  approval_request_id: approval-request:{preparation_id}
  entity_id
  preparation_ref
  capability_ref
  operation_summary
  status, expires_at, version, created_at, updated_at

Approval
  approval_id: approval:{approval_request_id}
  entity_id
  approval_request_ref
  decision
  actor_id
  decided_at
```

## Mandatory rules

1. Only a `ExecutionPreparation(PENDING_APPROVAL)` can create a `ApprovalRequest`.
2. Confirmation is only accepted when there is exactly one approval pending for the active Entity.
3. Decision registers `actor_id`, instant and exact request; is not reusable by another Entity, preparation or capability.
4. A `Approval(APPROVED)` is not an execution nor does it grant new capabilities.
5. Retrying the order, restoring the Entity, or repeating the confirmation cannot recreate the preparation or approval.
6. `REJECTED` and `EXPIRED` terminate authorization; VS-011 does not automatically reopen the request.
7. The LLM can describe the state, but the Kernel is the only component that interprets the confirmation and persists the approval.

## Events and trace

| Situation | Event | Trace |
| --- | --- | --- |
| Order created | `ApprovalRequestCreated` | `APPROVAL_REQUEST(PENDING)` |
| Order restored | `ApprovalRequestLoaded` | `APPROVAL_REQUEST(LOADED)` |
| Decision persisted | `ApprovalRecorded` | `APPROVAL(APPROVED|REJECTED)` |

`MindState` includes `approval_request_refs` and `approval_refs` at shutdown.

## Limit cases

| Condition | Mandatory result |
| --- | --- |
| "Yes" message with no pending order | does not create Approval; response explains the absence |
| more than one pending order | textual confirmation is refused due to ambiguity |
| restart before confirming | reload the PENDING request; does not prepare or execute again |
| repeated confirmation | uses the already persisted Approval; does not duplicate decision |
| request rejected | does not invoke external executor |

## Acceptance criteria

1. EMAIL_SEND generates `ExecutionPreparation(PENDING_APPROVAL)` and `ApprovalRequest(PENDING)`.
2. After restart, the preparation, result and approval request are loaded without duplication.
3. `Sim` creates `Approval(APPROVED)` and updates the request to `APPROVED`.
4. There is no network, SMTP, secret, Action, ToolCall or capability execution.
5. All VS-000–VS-010 tests remain green.

## Deliberate limits

This version does not yet collect recipient, subject or email body, nor does it implement active time expiration or approval UI. VS-012 will add the actual mail executor only when the request has full preparation and valid `Approval(APPROVED)`.