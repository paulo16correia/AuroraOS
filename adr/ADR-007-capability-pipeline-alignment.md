# ADR-007 — CapabilityRequest pipeline alignment

**Status:** ACCEPT
**Date:** 2026-07-18
**RFCs affected:** RFC 021, RFC 022, RFC 040, RFC 051, ADR-006

## Context

VS-007 introduced `CapabilityRequest`, but an Entity that already had a Task `COMPLETED` could load that Task for auditing only. In this situation, the email request stopped reaching the Capability Registry and the trace incorrectly registered `CAPABILITY(NOT_REQUIRED)`.

## Decision

VS-007.1 separates two collections of Tasks in the Kernel:

- `completed_tasks_this_cycle`: only feeds `GoalEvaluation`, preventing duplicate progress;
- `capability_source_tasks`: can include a completed and persisted Task, to support a `CapabilityRequest` of the current request.

For any email intent, the Kernel MUST evaluate the catalog and create or load the corresponding request before creating Thought and Decision. `NOT_REQUIRED` is only allowed when the intent does not require capability.

## Consequences

- `EMAIL_SEND` always goes by `CAPABILITY_REQUEST` and `CAPABILITY_ASSESSMENT(UNAVAILABLE)` when the user requests it.
- A historical Task does not increase Goal progress again.
- The response and Decision always receive a request validated by the Kernel for email requests.

## Migration and rollback

No data migration. Existing requests are loaded by the same idempotent key; Missing requests can be created from the persisted Task. Rollback preserves auditing and only removes this source recovery.