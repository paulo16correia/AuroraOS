# Aurora OS — Execution Trace Specification: VS-012 Idempotent Email Execution

**Status:** Authorized by ADR-012
**Use case:** valid email draft → approval → SMTP → persistent, non-repeatable result

## Objective

Run a single real end-to-end capability: `EMAIL_SEND`. Execution requires structured drafting, valid preparation, and `Approval(APPROVED)`. The result is persisted as `SUCCESS`, `FAILED`, or `UNKNOWN`; recovery never automatically resubmits an operation whose delivery may be ambiguous.

## Normative flow

```text
User: "Send an email to person@domain | Subject: ... | Body: ..."
  → EmailDraft(VALID)
  → ExecutionPreparation(PENDING_APPROVAL, payload_ref=EmailDraft)
  → ApprovalRequest(PENDING)
  → Approval(APPROVED)
  → ExecutionRecord(EXECUTING) persistido antes da rede
  → SmtpEmailProvider.send(draft, idempotency_key)
       ├─ aceite → ExecutionRecord(COMPLETED) → CapabilityResult(SUCCESS)
       └─ erro → ExecutionRecord(FAILED) → CapabilityResult(FAILED)

Se o processo terminar enquanto `EXECUTING`:
  → restauro → CapabilityResult(UNKNOWN)
→ no automatic repetition; requires explicit reconciliation.
```

## New objects

```text
EmailDraft
  draft_id, entity_id, request_ref
  recipient, subject, body
  status: VALID|INVALID
  validation_error, created_at

ExecutionRecord
  execution_id: execution:{preparation_id}
  entity_id, preparation_ref, approval_ref
  provider_id, idempotency_key
  status: EXECUTING|COMPLETED|FAILED
  external_reference, failure_code
  started_at, completed_at
```

## SMTP Provider

The provider is opt-in. You can only open an SMTP connection if the five variables are present in the process:

```text
AURORA_SMTP_HOST
AURORA_SMTP_PORT       (opcional; 587 por defeito)
AURORA_SMTP_USERNAME
AURORA_SMTP_PASSWORD
AURORA_SMTP_FROM
```

The secret is never persisted or included in events or traces. Without configuration, the result is `FAILED(EMAIL_PROVIDER_NOT_CONFIGURED)` without network connection.

## Idempotence and recovery rules

1. `ExecutionRecord(EXECUTING)` is persisted **before** the call to the provider.
2. Each execution has a deterministic key derived from the preparation and is sent in the `X-Aurora-Idempotency-Key` header; `Message-ID` uses the same key.
3. `COMPLETED` returns the existing result and never resends it.
4. `FAILED` is not automatically repeated.
5. `EXECUTING` recovered becomes result `UNKNOWN`; Resending without human or provider reconciliation is prohibited.
6. Only `Approval(APPROVED)` connected to the same preparation can start execution.
7. Email without recipient, subject or body is `EmailDraft(INVALID)` and blocks before approval.

## Events and trace

| Status | Event | Trace |
| --- | --- | --- |
| Draft | `EmailDraftCreated` | `EMAIL_DRAFT(VALID|INVALID)` |
| Before the network | `ExecutionStarted` | `EXECUTION(EXECUTING)` |
| Delivery confirmed | `ExecutionCompleted` | `EXECUTION(COMPLETED)` |
| Failure confirmed | `ExecutionFailed` | `EXECUTION(FAILED)` |
| Ambiguous recovery | `ExecutionUnknown` | `EXECUTION(UNKNOWN)` |

`MindState` now contains `execution_record_refs`.

## Acceptance criteria

1. A controlled provider receives an approved draft and returns `CapabilityResult(SUCCESS)`.
2. The same confirmation cannot invoke the provider a second time.
3. Without SMTP configuration, there is no network and the result is `FAILED(EMAIL_PROVIDER_NOT_CONFIGURED)`.
4. Failure or crash in `EXECUTING` never triggers automatic resending.
5. Tests VS-000–VS-011 remain green.

## Deliberate limits

VS-012 implements only SMTP with initiating TLS and only a plain text message. There are no attachments, HTML, threads, input boxes, automatic retry, OAuth, multiple providers or assisted reconciliation. These elements require subsequent ADRs.