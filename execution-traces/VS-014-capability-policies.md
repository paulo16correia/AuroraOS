# VS-014 — Capabilities Policies

## Objective

Introduce a persistent authorization boundary between `CapabilityRequest` and the executor. An available capability does not constitute authorization to use it. The Kernel must evaluate a policy before preparing an operation and again before performing a persisted pass.

## Scope

Included: policies by entity and capability, mandatory approval, authorization by principal and session, daily limit, auditable decision and secure deny.

Excluded: in-interface policy editor, cross-entity delegation, per-device quotas, complex temporal policies, and multi-step plans.

## Normative flow

```text
CapabilityRequest
       |
       v
CapabilityPolicyService.evaluate
       |
       +-- DENIED --> CapabilityResult(POLICY_DENIED) --> Response
       |
       +-- ALLOWED --> Executor.prepare --> ApprovalRequest (se exigida)
                                             |
                                             v
                                           Approval
                                             |
                                             v
                              CapabilityPolicyService.evaluate
                                             |
                                             +-- DENIED --> CapabilityResult(POLICY_DENIED)
                                             +-- ALLOWED --> Executor.execute
```

## Domain objects

### `CapabilityPolicy`

| Field | Meaning |
|---|---|
| `policy_id` | Deterministic identifier by entity and capability. |
| `capability_ref` | Capability protected. |
| `status` | `ACTIVE` or future inactive state. |
| `approval_requirement` | `REQUIRED` or `NOT_REQUIRED`. |
| `allowed_principal_ids` | Authorized principals; `*` represents everyone. |
| `allowed_session_ids` | Authorized sessions; `*` represents them all. |
| `daily_limit` | Maximum executions completed on UTC day; `null` means no limit. |
| `version` | Augmented by future changes via administrative API. |

### `PolicyEvaluation`

It is immutable and preserves the evidence of a decision: policy used, request analyzed, `ALLOWED` or `DENIED` result, reason, approval requirement and daily count at the time of evaluation.

## Mandatory rules

1. No executor interprets permissions, sessions, or quotas.
2. Non-existent policy is equivalent to `DENIED`.
3. The policy is checked before `prepare()` and before `execute()`.
4. A denial produces `CapabilityResult(POLICY_DENIED)` and never a preparation, approval or external action.
5. Human approval does not supersede a policy that has been revoked.
6. Limits only count `COMPLETED` runs; failures and uncertain operations do not consume quota.
7. Each evaluation publishes `CapabilityPolicyEvaluated` and a `CAPABILITY_POLICY` trace entry.

## Initial Policies

At the entity's birth, the runtime creates a policy per registered capability. External capabilities require approval; `EMAIL_SEND` receives an initial daily limit of 10. These values ​​are explicit defaults, not rules hidden in the executor.

## Limit cases

- **Restart between approval and submission:** the policy is reevaluated; if changed to `DENIED`, no sending occurs.
- **Different principal:** a commit from an unauthorized principal creates an auditable deny.
- **Limit reached:** the request does not prepare the executor and does not create pending approval.
- **Policy does not exist due to incomplete migration:** failed closed with `POLICY_NOT_FOUND`.

## Acceptance criteria

1. An authorized email follows the existing flow without changing the executor.
2. An unlisted principal receives `POLICY_DENIED` before `ExecutionPreparation`.
3. A policy and each evaluation survive shutdown/restore.
4. The idempotent send regression test remains green.

## Future expansions

- policies by device, channel, time window and data classification;
- quotas by cost, supplier and destination;
- Versioned and signed administrative API;
- declarative engine to combine multiple policies without changing the Kernel.