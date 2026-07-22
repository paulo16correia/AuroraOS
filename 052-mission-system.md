# Aurora Platform — RFC 052: Missions, objectives and tasks

**State:** Normative · **Depends on:** RFC 05, 035, 040, 051

## Objective

Enter **Mission** as an intention level above Goal. A mission describes an enduring purpose and the limits of success; Goals convert this purpose into results; Plans and Tasks do verifiable work.

## Hierarchy

```text
Constitution/Genome
          │ limita
Mission: “keep the user organized”
          │ orienta
Goal: “organizar os emails desta semana”
          │ decomposto em
Plan → Task: “create classification draft” → Action/ToolCall
```

## Data structures

```text
Mission
  id, mind_id, title, purpose, success_definition, boundaries[]
  priority_policy, owner, status: DRAFT|ACTIVE|PAUSED|RETIRED
  goal_refs[], review_at, evidence_refs[]
```

## Interfaces

```text
Missions.create(definition, approval) -> Mission
Missions.align(goal_id, mission_id) -> Goal
Missions.review(mission_id) -> ReviewReport
```

## Mandatory rules

1. Missions are not execution orders and do not replace approval of risk Goals/Tasks.
2. Every persistent Goal must be associated with a mission or be marked `AD_HOC` with a review deadline.
3. The mission cannot justify data collection, communication or autonomy outside the Constitution/Laws/policies.
4. Missions are reviewed, paused and removed by the owner; they do not evolve by automatic inference.

## Limit and error cases

- **Conflicting missions:** Decision Engine uses defined priorities/thresholds and asks for guidance if there is no safe resolution.
- **Goal without mission:** only allowed as `AD_HOC`, with owner and expiration.

## Justification

Missions keep isolated tasks from feeling like strategy. Separating purpose from execution keeps Aurora coherent over weeks or months.

## Future expansions

Shared team missions, mission indicators and periodic strategic review.