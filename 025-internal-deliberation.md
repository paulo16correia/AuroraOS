# Aurora OS — RFC 025: Internal deliberation and explainable synthesis

**State:** Normative · **Depends on:** RFC 021–024

## Objective

Define the internal state of deliberation without confusing execution telemetry, private chain of reasoning or user communication. Aurora must be able to coordinate internal steps and explain decisions in a verifiable way, without claiming conscience.

## Architecture

```text
ContextFrame → DeliberationState → Thought → Decision
                    │                 │          │
                    ▼                 ▼          ▼
internal state synthesis operational explanation
protected auditable for user
```

## Data structures

```text
DeliberationState
  id, cycle_id, phase: ORIENT|RETRIEVE|COMPARE|PLAN|DECIDE|VERIFY
  active_question, unresolved_questions[], candidate_refs[]
  assertions[], uncertainty[], next_step, status: ACTIVE|PAUSED|CLOSED
  trace_ref, retention_until

Thought
  id, cycle_id, intent, objective_ref, evidence_refs[]
  assumptions[], options[], uncertainty[], recommended_option
  user_explanation, status: DRAFT|VALIDATED|REJECTED|SUPERSEDED
````trace_ref` is protected technical material with limited retention and strict operational access. `user_explanation` contains motif, fonts and next effect; it is not a transcript of detailed reasoning.

## Interfaces

```text
Deliberation.start(cycle_id, question) -> DeliberationState
Deliberation.advance(id, evidence) -> DeliberationState
Deliberation.summarise(id) -> Thought
Deliberation.close(id, disposition) -> void
```

## Mandatory rules

1. Internal deliberation MUST be linked to cycle and deadline; There is no ownerless global mental process.
2. Claims without evidence remain hypotheses and have explicit confidence.
3. The system MUST NOT state “I am thinking” as evidence of work; only real states of `WorkItem` can be communicated.
4. Sensitive data in the trace must be minimized, encrypted, and excluded from normal exports.

## Limit and error cases

- **Inconclusive deliberation:** produce `ASK`/`WAIT`, with concrete questions.
- **Trace unavailable:** keep decision only if sources and policy are recoverable; otherwise invalidate.
- **Internal chain request:** provide operational explanation and evidence, not private trace.

## Justification

Separating deliberation from explanation reduces privacy risks and avoids designing the product around non-deterministic reasoning, while still preserving useful observability.

## Future expansions

Formal verification of assertions, expert reviewers, and domain decision reports.