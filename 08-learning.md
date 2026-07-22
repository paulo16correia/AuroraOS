# Aurora OS — RFC 08: Learning, reflection and evaluation

**State:** Normative · **Depends on:** RFC 02–07

## Objective

Allow operational improvement based on observed results without transforming each conversation into a permanent change in behavior. Learning means proposing, evaluating and consolidating knowledge or procedures under governance.

## Responsibilities and architecture

```text
WorkItem/Task Completion
             │
             ▼
collect evidence and metrics
             │
             ▼
Candidate reflection → validation → learning action
             │                                │
             ▼                                ▼
memory/procedure/policy report
```

The component produces brief reflections on success, failure, uncertainty, cost, and safety. Does not rewrite the profile, policies, or tools directly.

## Data structures

```text
Reflection
  id, work_item_id, task_id?, outcome: SUCCESS|PARTIAL|FAILURE|UNKNOWN
  evidence_refs[], observations[], root_causes[], lessons[]
  confidence, proposed_change_refs[], reviewer_required, status
  status: DRAFT|ACCEPTED|REJECTED|IMPLEMENTED|EXPIRED

LearningProposal
  id, type: MEMORY|PROCEDURE|PROMPT_CONFIG|POLICY_SUGGESTION
  change_json, expected_benefit, risk, evidence_refs[]
  evaluation_plan, rollback_plan, status: PROPOSED|APPROVED|TESTING|
    DEPLOYED|REJECTED|ROLLED_BACK

EvaluationRun
  id, proposal_id, dataset_ref, metrics_json, verdict, executed_at
```

## Interfaces

```text
Reflector.create(work_item_id) -> Reflection
Learning.propose(reflection_id) -> LearningProposal
Evaluator.run(proposal_id, test_scope) -> EvaluationRun
Learning.apply(proposal_id, approval) -> ChangeRecord
```

## Mandatory rules

1. A reflection MUST cite concrete evidence; a printout of the model is not confirmed root cause.
2. Only low-risk memory changes can be automatically committed and still comply with RFC 03.
3. Changes to personality, policy, tools, templates, or automation MUST be approved, tested, and reversible.
4. Assessments MUST also measure security regression, cost and privacy, not just textual quality.
5. The system MUST NOT train models with user data without specific consent and appropriate data agreement.

## Limit and error cases

- **Unknown result:** create reflection `UNKNOWN`; do not infer success from the absence of error.
- **Proposal conflicts with a policy:** reject and keep reason; never attempt an alternative detour.
- **Metric improves in one case and worsens in another:** keep in test and require human decision according to defined threshold.
- **Failure during application:** execute rollback or mark partial status, block new application and open incident.

## Justification

Separating observation, proposal, testing and application prevents the silent drift of behavior. Learning is traceable and can be reversed, an essential condition for a persistent assistant.

## Future expansions

Private evaluation sets, canary experimentation, federated learning with consent, and predictive maintenance recommendations.