# Aurora OS — RFC 13: Roadmap, Milestones, and Acceptance

**State:** Normative · **Depends on:** RFC 00–12

## Objective

Transform vision into verifiable deliverables. This roadmap establishes quality doors; does not allow skipping controls to demonstrate features.

## Delivery architecture

```text
Specify → implement → test → evaluate safety → limited pilot
      ▲                                                    │
└────────── failure/regression ← measure ← operate ────────┘
```

## Data structures

```text
Milestone
  id, name, scope_refs[], entry_criteria[], exit_criteria[]
  risks[], owner, status: PLANNED|ACTIVE|AT_RISK|ACCEPTED|REJECTED

AcceptanceEvidence
  id, milestone_id, criterion_id, test_ref, result
  reviewed_by, reviewed_at, exceptions[]

RiskRegisterEntry
  id, description, likelihood, impact, mitigation, owner, status
```

## Interfaces

```text
Programme.start(milestone_id) -> Milestone
Quality.submitEvidence(milestone_id, evidence) -> AcceptanceEvidence
Architecture.accept(milestone_id) -> Decision
Risk.review() -> RiskRegisterEntry[]
```

## Milestones and exit criteria

| Milestone | Result | Minimum acceptance criteria |
| --- | --- | --- |
| M0 — Foundation | RFCs, ADRs and Test Environment | all RFCs reviewed; threat model and approved data |
| M1 — Core | conversation, WorkItem, Thought, audit | idempotent events; recoverable failures; replaceable model |
| M2 — Memory | fixable memory and initial graph | provenance/ACL; propagated elimination; visible conflicts |
| M3 — Planning | persistent goals and tasks | dependencies, approval, cancellation and acceptance criteria |
| M4 — Pilot tool | low risk connector | end-to-end policy, vault, reconciliation and logging |
| M5 — Operation | VPS and Control UI | backup restored; alerts; navigable permissions and audit |
| M6 — Expansion | gradual external channels | security tests per connector; revocable automation |

## Mandatory rules

1. No milestone is accepted without reproducible evidence, safety review and updated documentation.
2. External writing capabilities only advance after a pilot tool demonstrates policy, approval, idempotence, and reconciliation.
3. Exceptions MUST have an owner, expiration date and mitigation plan; they do not become permanent behavior by default.
4. Success metrics MUST include correct action rate, incidents, cost, latency, recovery and user satisfaction.
5. The architecture MUST be reviewed before each milestone to confirm that previous decisions are still valid.

## Limit and error cases

- **Failure milestone criteria:** state `REJECTED` or `AT_RISK`; Replanning is mandatory before trying again.
- **Incomplete evidence:** does not equate to approval; keep landmark open.
- **Incident during pilot:** pause expansion, investigate, correct and replay affected evidence.
- **Vendor/model change:** perform compatibility, privacy and regression assessment before promoting.

## Justification

Milestones defined by evidence combat “feature rush” and allow us to deliver value without normalizing technical risk. The project grows through demonstrated trust, not through a promise of autonomy.

## Future expansions

Connector certification, independent testing, public reliability metrics, enterprise releases, and a formal RFC/ADR process for contributors.