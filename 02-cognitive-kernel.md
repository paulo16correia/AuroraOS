# Aurora OS — RFC 02: Cognitive Core

**State:** Normative · **Depends on:** RFC 00–01

## Objective

Define the orchestrator that transforms events into responses, plans, or action requests. The nucleus coordinates components; It is not a template conversation nor a place for access rules.

## Responsibilities

- Normalize intent, build minimal context and start a unit of work.
- Decide whether to respond, ask, create objective, prepare action or refuse.
- Ensure persistence, correlation, idempotence and subsequent reflection.

## Architecture and flow

```text
Evento autenticado
      │
      ▼
Perception → risk screening → context construction
      │                              │
      ▼                              ▼
interpretation ─────────────→ Thought (proposal)
                                     │
                     ┌───────────────┼───────────────┐
                     ▼               ▼               ▼
response planner tool manager
                     │               │               │
                     └──────→ persistir/reflectir ←──┘
                                      │
                                      ▼
exit to channel
```

## Data structures

```text
WorkItem
  id, correlation_id, causation_id, event_id, status
  status: RECEIVED|CONTEXTUALIZED|DELIBERATING|WAITING_APPROVAL|
          EXECUTING|COMPLETED|FAILED|CANCELLED
  deadline_at, retry_count, idempotency_key

Thought
  id, work_item_id, intent, requested_outcome
  context_refs[], memory_refs[], graph_refs[], goal_refs[]
  proposed_mode: RESPOND|ASK|PLAN|TOOL_CALL|REFUSE
  proposed_actions[], confidence: 0..1, uncertainty[]
  policy_requirements[], user_explanation, model_trace_ref
  status: DRAFT|VALIDATED|SUPERSEDED|REJECTED

ResponsePlan
  audience, channel, language, content_outline
  citations[], disclose_actions[], needs_user_input: boolean
````model_trace_ref` points to protected telemetry; does not contain or expose detailed reasoning to the user. `user_explanation` is a short, factual, and auditable explanation.

## Interfaces

```text
Kernel.handle(event_id) -> WorkItem
ContextBuilder.build(work_item, budget) -> ContextBundle
Deliberator.propose(context) -> Thought
PolicyEngine.evaluate(thought) -> PolicyDecision[]
Kernel.cancel(work_item_id, actor) -> status
````

ContextBundle` contains only authorized references, summary, maximum rating allowed, token/cost budget, and timeout.

## Mandatory rules

1. Each event MUST generate a maximum of one active `WorkItem` per idempotence key.
2. The core MUST validate all fields of `Thought` before executing a proposal.
3. `Thought` is NOT an authorization; any tool requires independent policy decision.
4. The context MUST be minimal, temporally limited and traceable by references, not by indiscriminate copies.
5. The response to the user MUST indicate failure, material uncertainty or need for approval.

## Limit and error cases

- **Ambiguous request:** produce `ASK`, without creating factual memory or action.
- **Model unavailable:** forward to authorized alternative model or return transient failure; never invent results.
- **Repeated event:** return already completed result or current state, without repeating effects.
- **Cancellation during tool:** ask the connector for cancellation; mark result as `CANCELLED` or `UNKNOWN` until reconciliation.

## Justification

The `Thought` object creates a verifiable boundary between probabilistic interpretation and deterministic execution. A persistent unit of work allows you to recover from restarts and prevent duplication.

## Future expansions

Multi-model deliberation, plan simulation, competing priorities, multimodal working memory and continuous quality assessment.