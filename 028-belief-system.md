# Aurora OS — RFC 028: Belief System and Uncertainty

**State:** Normative · **Depends on:** RFC 03, 040–041

## Objective

Represent generalizations and reviewable predictions that guide attention and decision, without confusing them with confirmed facts. A belief is an operational hypothesis with evidence, confidence, scope, and review period.

## Architecture and flow

```text
Observations + Memories + Experiences
                 │
                 ▼
pattern inference/belief proposal
                 │
                 ▼
evidence, counter-evidence, reliability and validity
                 │
                 ▼
Active Belief → attention/decision (never single source of high risk)
```

## Data structures

```text
Belief
  id, subject_ref, predicate, object_json, scope_json
  confidence: 0..1, evidence_for_refs[], evidence_against_refs[]
  basis: OBSERVED|INFERRED|USER_STATED|IMPORTED
  status: CANDIDATE|ACTIVE|CHALLENGED|SUPERSEDED|RETRACTED|EXPIRED
  valid_from, review_at, last_evaluated_at, decision_impact: LOW|MEDIUM|HIGH

BeliefUpdate
  id, belief_id, observation_ref, delta_confidence, reason, applied_at
```

Examples: “the user prefers short answers” ​​or “server A has greater observed stability”. A belief about a person does not authorize sensitive inferences, discriminatory decisions, or external communication without adequate evidence.

## Interfaces

```text
Beliefs.propose(candidate, evidence) -> Belief
Beliefs.observe(belief_id, observation) -> BeliefUpdate
Beliefs.query(scope, threshold, access_context) -> Belief[]
Beliefs.challenge(belief_id, evidence) -> Belief
```

## Mandatory rules

1. Beliefs MUST state supportive evidence and reliable support; the model alone is not evidence.
2. They can never be the sole basis for high-risk action, identity, security, money, health, law or sensitive content.
3. Beliefs have a review/expiration date and decrease in confidence when they are not confirmed, according to explicit policy.
4. Direct and verified user statements may prevail, but remain correctable.

## Limit and error cases

- **Contradictory evidence:** status `CHALLENGED`; reduce or separate scope rather than blind averaging.
- **Insufficient data:** keep `CANDIDATE`; do not personalize in a material way.
- **Failed prediction:** attach counter-evidence and re-evaluate, do not silently erase.

## Justification

Separating belief from memory allows Aurora to use useful patterns without pretending they are reality. This reduces rigidity and prevents transitory inferences from becoming permanent facts.

## Future expansions

Bayesian models, automatic calibration, confidence explanations and bias assessment.