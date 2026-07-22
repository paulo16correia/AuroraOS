# VS-021 — Multi-capability orchestration

## Objective

Demonstrate that a single Goal can materialize several capability branches without coupling executors together.

```text
Composite order
       ↓
INTERNAL_ANALYSIS
       ↓
PLAN
  ┌────┴──────────────────────────┐
  ↓                               ↓
CALENDAR_CREATE_EVENT         EMAIL_SEND
  ↓                               ↓
ApprovalRequest(Calendar)    ApprovalRequest(Email)
  ↓                               ↓
Executor Calendar             Executor Email
  └───────────────┬───────────────┘
                  ↓
            Final Response
```

## Mandatory rules

- Each branch creates its own `CapabilityRequest`, `ExecutionPreparation`, `ApprovalRequest`, `ExecutionRecord` and `CapabilityResult`.
- A Calendar approval can never authorize an Email, and vice versa.
- When there are multiple pending orders, `Yes` does not choose arbitrarily. The user indicates `I approve calendar`, `I approve email` or `I approve everything`.
- Completion or failure of one branch does not re-execute the other.
- The order in which branches are created is stable: Calendar and then Email. This preserves deterministic replay.

## Initial entry contract

```text
Set up a meeting | Title: Review | Start: 2026-07-20T15:00:00+01:00 | End: 2026-07-20T16:00:00+01:00 | Send an email to joao@example.com | Subject: Meeting | Body: Do you confirm?
```

The Email recipient is explicit. Resolving “João” for a contact belongs to VS-022 and is not inferred in this slice.