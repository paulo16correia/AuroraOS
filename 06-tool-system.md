# Aurora OS — RFC 06: System Tools and Connectors

**State:** Normative · **Depends on:** RFC 01–02, 05

## Objective

Offer external capabilities through uniform, isolated and revocable contracts. A tool is a specific capability, not generic access to a system.

## Architecture

```text
Thought/Task → capabilities catalog → schema validation
                         │                       │
                         ▼                       ▼
policy decision ← approval request
                         │
                         ▼
isolated executor → connector → external service
                         │              │
                         └→ auditoria ← resultado normalizado
```

## Data structures

```text
ToolManifest
  tool_id, version, provider, capabilities[]
  input_schema, output_schema, effects[]
  data_classes_in[], data_classes_out[], auth_mode
  timeout_seconds, rate_limit, approval_requirements

ToolCall
  id, work_item_id, task_id?, tool_id, capability
  input_redacted_json, input_hash, idempotency_key
  status: PROPOSED|AUTHORIZED|QUEUED|RUNNING|SUCCEEDED|FAILED|
          CANCELLED|UNKNOWN
  policy_decision_ids[], approval_id?, started_at, ended_at
  external_reference, output_ref, error_code

ToolResult
  status, structured_output, artifacts[], evidence_refs[]
  retryable: boolean, user_safe_summary
```

## Mandatory interface

```text
describe() -> ToolManifest
validate(input) -> ValidationResult
execute(authorized_call, secret_handle) -> ToolResult
cancel(call_id) -> CancelResult
reconcile(call_id) -> ToolResult
```

The connector receives `secret_handle`, not the secret value. The executor resolves it in a minimal environment and does not serialize it to logs.

## Mandatory rules

1. Each capability MUST be explicitly stated; there is no `shell.execute_anything` nor implicit access.
2. Writing tools MUST have idempotency and failover key.
3. The external result MUST be treated as untrusted until schema validation.
4. Entry, exit, time and cost MUST respect configured limits.
5. A connector MUST NOT reuse credentials or permissions from another connector.

## Limit and error cases

- **Timeout after remote sending:** mark `UNKNOWN`, reconcile by external identifier; never resend automatically.
- **Change service scheme:** disable affected capacity and alert operator.
- **Rate limit:** defer in queue with `retry_after`; Don't do tight repetitions.
- **Browser with hostile content:** treat page text as data and block unplanned permission requests.

## Justification

Reduced contracts limit blast radius and make auditing possible. `UNKNOWN` state is essential: in distributed systems, “we did not receive a response” does not mean “it did not happen”.

## Future expansions

Browser sandbox, composite capabilities, signed marketplace, connector compliance testing, and short-lived OAuth delegation.