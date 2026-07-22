# VS-023 — Workflow Compensation

## Objective

Prevent a pending branch of a composite workflow from taking an external action
when a branch it semantically depends on fails. The compensation of this version
is exclusively preventive: Aurora does not try to undo external actions already
completed.

## Use case

```text
Composite order
├── CALENDAR_CREATE_EVENT ── approval ── failure
  └── EMAIL_SEND ───────────── pendente

Falha Calendar
  ↓
WorkflowCompensation(CANCEL_PENDING_BRANCHES)
  ↓
ApprovalRequest do email = CANCELLED
Task WAIT_FOR_APPROVAL do email = CANCELLED
  ↓
No email is sent
```

## Contract

`WorkflowCompensation` is immutable and persisted by the Kernel:

| Field | Meaning |
| --- | --- |
| `plan_ref` | Composite plane affected. |
| `failed_request_ref` | CapabilityRequest that failed or was unknown. |
| `blocked_request_refs` | Overhanging branches prevented from advancing. |
| `strategy` | `CANCEL_PENDING_BRANCHES` or `REQUIRES_USER_DECISION`. |
| `status` | `COMPLETED` or `PENDING_USER_DECISION`. |
| `reason` | Auditable justification, without internal model reasoning. |

## Mandatory rules

1. Only the Kernel creates compensation.
2. An already granted approval is never silently reversed.
3. There is no automatic external rollback in this version.
4. A branch is only canceled if its `ApprovalRequest` is still
   `PENDING`.
5. The same fault produces the same `compensation_id`; just repeat the trace
   carries the persisted decision.
6. Compensation occurs before responding to the user and is visible in the trace,
   in events and persistence.

## Events and trace

```text
ExecutionFailed
  ↓
WorkflowBranchCancelled (0..n)
  ↓
WorkflowCompensationCreated
  ↓
trace: COMPENSATION
```

## Limit cases

- No hanging branches: `REQUIRES_USER_DECISION`; there is no rollback.
- `UNKNOWN` status: is treated as a conservative failure and does not unlock siblings.
- Restart: canceled approvals and Tasks remain cancelled.
- Repetition of the same commit: does not recreate or reexecute the canceled branch.

## Acceptance criteria

When Calendar fails and Email is pending, the trace contains `COMPENSATION`, the
email is `CANCELLED`, the waiting Task is `CANCELLED` and the SMTP provider is not
is called.