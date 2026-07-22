# ADR-012 — Idempotent execution of EMAIL_SEND

**Status:** Accepted
**Date:** 2026-07-18
**Decision:** VS-012 — Idempotent Email Execution

## Context

After VS-011, Aurora is able to prepare and obtain persistent approval. It remains to transform this authorization into a real external capacity without repeating sends in case of failure, timeout or restart.

## Decision

Implement `EmailDraft`, `ExecutionRecord`, `SmtpEmailProvider` and the `Approval(APPROVED) → Executor.execute_approved` path. The record goes into `EXECUTING` before any I/O. No recovered `EXECUTING` records are automatically resent; is treated as `UNKNOWN` until reconciliation.

The SMTP provider is configured exclusively through environment variables. Without full configuration, it fails locally and auditably.

## Consequences

- Aurora executes a real external capability under approval and with full audit.
- Idempotence is guaranteed in the Kernel for completed or ambiguous operations; The key header allows collaboration with providers that support it.
- The ambiguous delivery problem is preferred to a potentially duplicate resubmission.

## Rejected alternatives

1. **Automatically repeat after crash** — can duplicate an email already accepted by the server.
2. **Saving SMTP secrets in the database** — violates the security model.
3. **Infer recipient and content freely by LLM** — does not provide a verifiable boundary for external action.