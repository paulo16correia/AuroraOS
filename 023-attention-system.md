# Aurora OS — RFC 023: Attention System

**State:** Normative · **Depends on:** RFC 03–04, 020–021

## Objective

Select a small set of signals, memories and goals that deserve processing in a cycle. Attention is resource management and relevance, not memory and not authorization.

## Architecture and flow

```text
Event + active objectives + scheduler signals + Mind state
                           │
                           ▼
eligibility by ACL/privacy/recency
                           │
                           ▼
score: relevance + urgency + novelty + impact + trust
                           │
                           ▼
             AttentionSet limitado → Working Memory
```

## Data structures

```text
AttentionItem
  ref, kind: EVENT|MEMORY|GOAL|OBSERVATION|ALERT
  relevance, urgency, novelty, impact, confidence, recency
  score, reason_codes[], expires_at, sensitivity

AttentionSet
  id, cycle_id, items[], token_budget, item_limit
  status: PROPOSED|LOCKED|RELEASED, selected_at

AttentionPolicy
  id, scope, weights_json, thresholds_json, max_items, version
```

## Interfaces

```text
Attention.rank(candidates, policy, budget) -> AttentionSet
Attention.focus(cycle_id, item_ref) -> AttentionSet
Attention.release(cycle_id) -> void
```

## Mandatory rules

1. Attention MUST filter authorization before calculating relevance.
2. The set is limited by items, time and budget; Recovering the entire file is prohibited.
3. The reason for selecting and excluding material items MUST be recorded.
4. High salience does not circumvent politics: a secret or hostile instruction does not gain access because it is urgent.

## Limit and error cases

- **300 thousand eligible memories:** use phased hybrid search and deterministic pruning; do not load complete objects before selection.
- **Actual emergency:** policy may elevate urgency, but continues to require source validation and authorized action.
- **Tie:** prefer more recent, confirmed evidence and directly linked to the objective.
- **No candidates:** produce empty set and lead to `ASK`, `WAIT` or limited answer.

## Justification

Without attention, the system mixes irrelevant context, increases cost and creates spurious associations. With explainable attention, it is possible to justify why 17 memories were used and the rest were not.

## Future expansions

Weight learning with evaluation, multimodal saliency, interruption queues and attention by user profile.