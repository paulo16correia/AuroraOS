# Aurora Platform — RFC 037: Development Model

**State:** Normative · **Depends on:** RFC 07, 027–028, 036, 040

## Objective

Define how an instance gains operational confidence over time without “learning” converting into an automatic increase in power. Development alters patterns of assistance, confirmation requirements, and delegated autonomy within the boundaries of the Genome, Constitution, and policies.

## Architecture

```text
evidence of use + reviews + explicit preferences + incidents
                                  │
                                  ▼
                         DevelopmentAssessment
                                  │
                                  ▼
proposed phase → approval/policy → effective profile
```

## Data structures

```text
DevelopmentProfile
  id, genome_ref, stages[]
Stage
  id, name, autonomy_ceiling, confirmation_rules[], capability_constraints[]
  promotion_criteria[], regression_criteria[]

DevelopmentState
  mind_id, current_stage_id, evidence_refs[], assessment_at
  status: PROBATION|ACTIVE|RESTRICTED|PAUSED
```

An initial phase may focus on explanation, drafts and confirmation; later phases may allow for narrow preauthorization rules. No phase grants capabilities outside of what the user and policy have authorized.

## Interfaces

```text
Development.assess(mind_id) -> DevelopmentAssessment
Development.proposeTransition(mind_id, target_stage) -> Proposal
Development.applyTransition(proposal_id, approval) -> DevelopmentState
```

## Mandatory rules

1. Promotion requires evidence of reliability, not just elapsed time.
2. Relevant incident, revocation or degradation may restrict/revert the phase.
3. Personality can adapt style within limits; constitutional values ​​and base identity do not evolve by inference.
4. Changes in autonomy are visible and reversible by the user.

## Limit and error cases

- **Many low-risk successes:** do not justify financial autonomy, SSH or public communication.
- **Failure during advanced phase:** only reduce the affected scope when possible, preserving evidence.
- **Insufficient data:** maintain current phase; do not promote by default.

## Justification

Development distinguishes operational maturity from a personality that “drifts”. Confidence grows through trials and can decrease through risk.

## Future expansions

Domain profiles, controlled trials, autonomy canaries, and explainable trust metrics.