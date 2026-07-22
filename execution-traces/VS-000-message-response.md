# Aurora OS — Execution Trace Specification: VS-000 Message Response

**Scope:** the smallest MCP-first, no-effect message cycle.
**Out of scope:** external connector, capability execution, browser, scheduler, autonomous work, and model-provider calls by Aurora.

## Objective

Prove the first Kernel path: an LLM client invokes a governed MCP tool with a user request; Aurora creates a persistent, auditable cycle and returns a state-backed result. The LLM client—not Aurora—understands the free-form message and writes the conversational response.

```text
User → LLM client → MCP tool call → Perception → Attention → Working Memory
     → Memory/World Model → Decision → Policy → MCP tool result → LLM client → User
```

## Boundaries

- The MCP ingress adapter validates caller metadata, arguments, correlation, and authorization.
- The Kernel owns `Thought`, `Decision`, `Response`, cycle records, and events.
- No tool in this trace writes memory, creates a goal, calls a capability, or creates an external effect.
- The client receives only the result authorized by the Kernel. It cannot write persistence or publish events directly.

## Determinism and replay

The trace uses a fixed clock and deterministic Kernel dependencies. Replay receives the recorded MCP ingress and authorized state snapshots, runs without effects, and compares event sequence, state transitions, decision mode, output hashes, and change sets. A difference emits `ReplayDiverged`; it never rewrites status or publishes on real channels.

## Acceptance criteria

1. A valid MCP call creates an auditable cycle and one controlled tool result.
2. Invalid or unauthorized ingress creates no persistent cognitive mutation.
3. The client cannot call internal Kernel services outside the MCP boundary.
4. No direct LLM API, model proposal, or network request is made by Aurora.
5. Replaying the same ingress produces the same Kernel result under deterministic dependencies.
