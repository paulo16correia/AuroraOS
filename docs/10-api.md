# Aurora OS — RFC 10: API, events, MCP, and integration contracts

**Status:** Normative · **Depends on:** RFC 02–09, RFC 045

## Objective

Define stable integration surfaces for UI, channels, operators, and LLM clients. MCP is the LLM-facing capability surface. The API exposes resources, commands, and events; neither surface exposes secrets or bypasses cognition, policy, approval, or execution controls.

## Architecture

```text
LLM client → MCP server → Aurora Kernel
UI/channel/operator → API gateway → Aurora Kernel
                                      ├─ commands
                                      ├─ queries
                                      └─ authorized event stream
```

## Interfaces

```text
POST /v1/events                 accepts a normalized event
POST /v1/goals                  creates a DRAFT goal
GET  /v1/goals/{id}             reads state and authorized plan
POST /v1/approvals/{id}/decide  approves or rejects the exact scope
GET  /v1/memories               searches with ACL
PATCH /v1/memories/{id}         corrects memory
DELETE /v1/memories/{id}        requests forgetting
GET  /v1/audit                  paginated auditable query
GET  /v1/stream                 authorized WebSocket/SSE event stream
```

MCP tool discovery and schemas are published by the Kernel. They expose governed capability families such as memory, world, planning, scheduling, tasks, missions, communication, approvals, capabilities, identity, and relationships, without this RFC hardcoding tool names.

```text
ApiEnvelope
  request_id, correlation_id, api_version, data, errors[], links[]
ApiError
  code, message, retryable, field?, support_reference
DomainEvent
  id, type, subject_ref, occurred_at, producer, schema_version
  correlation_id, causation_id, payload_ref|payload_json, sensitivity
```

## Mandatory rules

1. Write commands MUST accept `Idempotency-Key`; repeated requests return the same logical result.
2. API and MCP contracts MUST version incompatible changes and publish event schemas.
3. Queries MUST apply authorization on the server; clients never receive everything to filter locally.
4. MCP calls MUST be validated as untrusted ingress and MUST NOT expose secrets, internal details, or existence of sensitive resources to an unauthorized caller.
5. Events are immutable facts; corrections are new events referring to previous ones.
6. MCP results are not natural-language authority: the LLM client may present them conversationally, while Aurora remains authoritative for state and effects.

## Failure cases and extensions

Outdated clients receive a supported version and migration instruction. Failed webhook delivery uses signed backoff and an auditable dead queue. Streams resume by cursor with bounded retention. Future extensions include SDKs, read-only GraphQL, event federation, a segregated admin API, and partner contracts.
