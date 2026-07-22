# Aurora OS — RFC 031: System Operational Needs

**Status:** Normative · **Depends on:** RFC 020, 026, 030, 033, 040

## Objective

Convert persistent operation conditions into explicit internal priorities: delayed backups, blocked critical tasks, untreated email queue, memory consolidation or health maintenance. Needs are not emotions, nor do they authorize actions; are focus candidates when Aurora is available.

## Architecture

```text
Self/Health + Goals + Signals + Scheduler + Resource Model
                              │
                              ▼
                  Need candidate e intensidade
                              │
                              ▼
      Attention → Decision Engine → criar/ordenar Goal ou Task autorizada
```

## Data structures

```text
Need
  id, kind: SAFETY|OBLIGATION|MAINTENANCE|CONSOLIDATION|COMMUNICATION|RECOVERY
  subject_ref, intensity: 0..1, priority, evidence_refs[]
  earliest_action_at, expires_at, recommended_goal_ref
  status: DETECTED|ACKNOWLEDGED|PLANNED|SATISFIED|DEFERRED|EXPIRED
  policy_constraints[], owner: SYSTEM|USER
```

## Interfaces

```text
Needs.detect(snapshot, signals) -> Need[]
Needs.rank(needs, situation, resources) -> Need[]
Needs.satisfy(need_id, evidence) -> Need
```

## Mandatory rules

1. Each Need requires evidence and a measurable satisfaction condition.
2. Internal needs can only create goals/tasks `DRAFT`; Execution continues to go through the cycle, policies and approvals.
3. Explicit user needs prevail over low-risk maintenance, barring security/recovery incidents.
4. Intensity decreases or expires in a defined way; there are no eternal “urgencies”.

## Limit and error cases

- **20 pending emails:** creates communication needs, does not automatically send responses.
- **Backup due and low resources:** Security needs gain priority, but the Resource Model can schedule a safe window.
- **Conflict between needs:** Decision Engine chooses by policy, impact, deadline and resources, recording the reason.

## Justification

Needs give direction to limited autonomy without inventing human desires. Make maintenance work visible, prioritizeable and auditable.

## Future expansions

SLOs, customizable priorities, attention negotiation, and degradation prediction.