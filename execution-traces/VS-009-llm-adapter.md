# Aurora OS — Execution Trace Specification: VS-009 LLM Adapter

**Status:** Replaced by MCP-first boundary while retaining identifier compatibility.

## Purpose

VS-009 defines the boundary between an interchangeable LLM client and Aurora. The previous outbound language-provider adapter is replaced: Aurora does not call an LLM to obtain a proposal. A compatible LLM client (Claude, GPT, Gemini, or a local model) uses Aurora through MCP.

## Trace

```text
User → LLM Client → MCP.initialize → Aurora MCP Server
                         ↓
                  MCP.tools/call
                         ↓
              Perception → governed cognitive cycle
                         ↓
                MCP tool result → LLM Client → User
```

The LLM owns natural-language understanding, conversational turn management, clarification questions, tool selection, and natural-language response production. The Kernel owns persistent identity, memory, world model, planning, missions, goals, policies, approvals, execution, audit, and continuity.

## Contracts

```text
McpToolCall
  session_ref, tool_name, arguments, caller_metadata, correlation_id

McpToolResult
  correlation_id, status, content, state_refs[], approval_ref?, audit_ref?

McpClientContext
  client_id?, model_id?, locale?, transport_metadata
```

`McpClientContext` is transport context, not a persistence authority. The Kernel validates tool arguments independently and never treats model text as a trusted state mutation.

## Invariants

1. An LLM client never writes Aurora persistence or publishes Aurora events directly.
2. An LLM client never creates `Thought`, `Decision`, `Memory`, `Goal`, `Plan`, `Task`, `Approval`, `CapabilityRequest`, or `CapabilityResult` except through a governed MCP tool path.
3. No MCP tool exposes credentials, unrestricted repositories, arbitrary shell access, or bypasses policy.
4. Tool discovery and tool names are supplied by the MCP server contract; this specification does not hardcode a tool catalogue.
5. A disconnected, replaced, or failed LLM client does not change Kernel state merely by failing.
6. The Kernel records ingress, decisions, policy outcomes, effects, observations, and audit references independently of the client.

## Events

| Boundary event | Trace state |
| --- | --- |
| MCP session accepted | `MCP_SESSION(OPEN)` |
| Tool call validated | `MCP_TOOL_CALL(ACCEPTED|REJECTED)` |
| Cognitive cycle advances | normal RFC 021 stage records |
| Tool result emitted | `MCP_TOOL_RESULT(COMPLETED|FAILED|WAITING)` |

## Acceptance criteria

1. A normal request enters Aurora only as a validated MCP tool call and produces an auditable tool result.
2. Replacing the LLM client preserves identity, memory, plans, policies, and audit history.
3. An invalid or unauthorized tool call produces no persistent mutation or external effect.
4. A client can describe state only from MCP results; it cannot claim unprovided private state as Aurora fact.
5. Replay uses recorded MCP ingress and deterministic Kernel dependencies; no LLM network call is required.
