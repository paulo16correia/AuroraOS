# Aurora OS — RFC 022: Decision Engine

**Status:** Normative · **Depends on:** RFC 01, 021, 040

## Objective

Separate the decision to act from the generation of a response. The Decision Engine chooses a mode of action based on intention, evidence, risk, state of mind, policies and active context.

## Architecture

```text
Thought + Context + Attention + policies + goal state
                           │
                           ▼
option evaluation
      ┌───────┬─────────┬──────────┬─────────┬──────────┐
      ▼       ▼         ▼          ▼         ▼          ▼
answer ask plan wait tool refuse/silence
                           │
                           ▼
                   DecisionRecord → ciclo cognitivo
```

## Data structures

```text
Decision
  id, cycle_id, mode: RESPOND|ASK|CREATE_GOAL|PLAN|WAIT|TOOL_CALL|REFUSE|SILENT
  objective_ref, selected_option, alternatives_considered[]
  evidence_refs[], uncertainty[], risk_level, confidence
  policy_decision_ids[], approval_required, expiry_at
  status: PROPOSED|COMMITTED|EXECUTED|SUPERSEDED|EXPIRED

DecisionOption
  mode, rationale_summary, expected_effects[], cost_estimate
  risk, prerequisites[], blocking_reasons[]
```

## Interfaces

```text
DecisionEngine.evaluate(thought, context) -> Decision
DecisionEngine.commit(decision_id, policy_results) -> Decision
DecisionEngine.invalidate(decision_id, reason) -> Decision
```

## Mandatory rules

1. The engine MUST evaluate at least: relevance, evidence, risk, cost, permissions and reversibility.
2. `TOOL_CALL` without `ALLOW` policy or valid approval cannot be `COMMITTED`.
3. `SILENT` is only allowed by explicit channel rule, privacy, noise limit or absence of recipient; never to hide fault.
4. Decisions expire when context, approval, data or deadline are no longer valid.

## Limit and error cases

- **High confidence without source:** reduce to `ASK` or `RESPOND` with uncertainty; never increase privilege.
- **Two equivalent options:** prefer lower external effect and lower cost, or ask for preference.
- **New information invalidates decision:** mark `SUPERSEDED` before taking effect.
- **Motor unavailable:** block tools; Only allow approved static responses.

## Justification

Responding is a choice with social and operational effects. An explicit module allows you to test decisions and prevents the model language from determining the action policy.

## Future expansions

Configurable utility, impact simulation, collegial decisions and goal-based attention policies.