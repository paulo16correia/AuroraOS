# ADR-009 — LLM proposal boundary

**Status:** Accepted (MCP-first migration)
**Decision:** VS-009 — LLM Adapter

## Context

Aurora has continuity, memory, goals, plans, tasks, capability assessment, and controlled execution. Its former language-provider adapter described the Kernel creating a request and calling an LLM provider. That direction conflicts with the MCP-first architecture.

## Decision

Replace the outbound provider boundary with an MCP server boundary. A compatible LLM is an external MCP client. It understands natural language, manages the conversation, decides which exposed capability to call, asks clarifying questions, and writes the user-facing answer. Aurora exposes governed capabilities through MCP and remains the persistent cognitive entity.

```text
User → LLM client → MCP → Aurora Kernel → MCP result → LLM client → User
```

The Kernel remains the only writer to its repository and the only publisher of Aurora events. MCP transport context is untrusted until validated. Every mutation, approval, policy decision, capability execution, and audit record follows the existing Kernel contracts.

## Consequences

- Aurora is compatible with hosted and local LLMs without coupling cognition to a model vendor.
- No LLM SDK, API key, model request, or model proposal is required inside the Kernel.
- Tool schemas remain capability contracts, not a substitute for policy or approval.
- Deterministic replay uses recorded MCP ingress and Kernel test doubles.
- VS-000–VS-008 state ownership and execution invariants remain in force; their language-provider references are interpreted as the external MCP client boundary.

## Rejected alternatives

1. **Keep a direct LLM SDK in the Kernel** — reverses the required boundary and couples Aurora to a vendor.
2. **Let the LLM write Memory, Goal, or Plan** — violates state ownership, idempotence, and the Constitution.
3. **Make MCP a thin LLM-wrapper API** — removes persistent cognition from Aurora and conflicts with its role as a Cognitive Operating System.
