# Aurora OS — RFC 032: Governed Curiosity Engine

**State:** Normative · **Depends on:** RFC 028, 030–031, 033–034, LAW-006

## Objective

Identify potentially useful knowledge gaps and propose low-risk investigation. Curiosity is a governed capacity for discovery, not an excuse to collect data, browse without limits or contact third parties.

## Architecture

```text
Recurring patterns + World Model gaps + authorized objectives
                              │
                              ▼
                   CuriosityProposal
                              │
                 valor × risco × recursos × consentimento
                              │
                              ▼
discard | schedule search allowed | ask for approval
```

## Data structures

```text
CuriosityProposal
  id, question, rationale_refs[], expected_value, scope
  allowed_sources[], sensitivity_limit, resource_budget
  status: CANDIDATE|APPROVED|SCHEDULED|RESEARCHING|LEARNED|REJECTED|EXPIRED
  approval_required, result_refs[], review_at
```

## Interfaces

```text
Curiosity.detect(mind_snapshot) -> CuriosityProposal[]
Curiosity.evaluate(proposal, situation, resources) -> DecisionOption
Curiosity.schedule(proposal_id, approval) -> Task
```

## Mandatory rules

1. Curiosity can only use explicitly permitted data sources and classification.
2. You cannot access private accounts, send messages, make purchases, change external status or infer sensitive personal data without your own objective and approval.
3. Autonomous search is limited by budget, time window, most urgent needs and user preference.
4. Results cover Perception and LAW-001; researching does not automatically create knowledge.

## Limit and error cases

- **User mentions Rust repeatedly:** research proposal may appear, never automatic subscription/purchase/external action.
- **System busy:** proposal is `DEFERRED`/`EXPIRED`, without competing with security or user tasks.
- **Hostile source:** content is treated as untrustworthy observation and does not change policies.

## Justification

Very limited curiosity allows useful evolution without turning Aurora into an indiscriminate collection machine.

## Future expansions

Catalogs from trusted sources, project-based learning, usefulness reviews, and banned topics lists.