# Aurora Platform — RFC 045: Aurora Kernel

**Status:** Normative · **Depends on:** RFC 011, 035, 039–040, 043, 050, 051

## Objective

Define the Aurora Kernel, the persistent control and cognition plane of Aurora OS. The Kernel is not an LLM and does not depend on one for identity or continuity. It exposes governed capabilities through the Model Context Protocol (MCP) to compatible LLM clients.

## Architecture

```text
User → LLM Client → MCP → Aurora Kernel
                           ├─ World Model and Memory
                           ├─ Mind State, Planner, Decision Engine, Scheduler
                           ├─ Event Bus, Policies, Approvals, Audit
                           ├─ Capability Registry and Executor
                           └─ Persistence and Lifecycle
```

The LLM client owns language understanding, conversation, clarification, tool selection, and response writing. The Kernel owns persistent identity, cognition, state, effects, and continuity.

## Structures and interfaces

```text
KernelService
  service_id, role, health, capability_refs[], lifecycle_state

KernelCommand
  id, issuer, command_type, payload_ref, idempotency_key
  policy_refs[], status: ACCEPTED|REJECTED|RUNNING|COMPLETED|FAILED

Kernel.start(instance) -> InstanceLifecycle
Kernel.dispatch(command) -> KernelCommand
Kernel.handle_mcp(tool_call) -> McpToolResult
Kernel.health() -> KernelHealth
```

## Mandatory rules

1. The Kernel, Mind, Applications, and MCP transport are separate planes with their own service identities and permissions.
2. The Kernel is the sole owner of lifecycle, persistent state, dispatch, execution resources, and the Event Bus.
3. The Kernel validates MCP ingress and applies Mind semantics, policies, versioning, isolation, and event delivery before returning a result.
4. The LLM client has no direct repository, event-bus, executor, secret, approval, or policy access.
5. An Application failure cannot compromise Kernel state or Mind integrity.
6. MCP is a capability interface; it neither bypasses approval nor grants arbitrary execution.

## Failure, rationale, and extensions

If the Kernel is unavailable, Mind enters a safe state with no further effects. If an LLM client disconnects or changes, the Kernel preserves state and audit history. This boundary permits model, connector, and interface replacement without rewriting Aurora. Future extensions include distributed Kernel control planes, high availability, and organization isolation.
