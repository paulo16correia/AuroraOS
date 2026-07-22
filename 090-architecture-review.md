# Aurora Platform — RFC 090: Architecture Review Gate

**State:** Mandatory before product implementation · **Depends on:** RFC 000–060

## Objective

Set the revision that freezes the architecture sufficiently to start code. It is not a declaration of perfection; it is an informed decision that the interfaces, invariants, and responsibilities are stable enough to implement and evolve with ADRs.

## Process

```text
RFC inventory → responsibility matrix → dependency analysis
        │                         │                         │
        ▼                         ▼                         ▼
Law/Constitution tests → threats and data → interfaces/events
        │                         │                         │
└──────────────→ decisions/ADRs and risks ←───────────┘
                                      │
                                      ▼
                         ACCEPT | ACCEPT WITH CONDITIONS | REWORK
```

## Checklist per component

1. What do you stand for and what is your sole responsibility?
2. Who owns the state, who can change it and how does it come into being/end?
3. What is the public interface and what are the input/output contracts?
4. What events does it produce/consume and what are the guarantees of idempotence?
5. What laws, constitutional articles and data classifications do you apply?
6. Is there overlap, dependency cycle, Kernel bypass or vendor coupling?
7. How does it fail, recover, be observed and tested?

## Structures and interfaces

```text
ArchitectureReview
  id, scope, rfc_versions[], findings[], risks[], adr_refs[]
  decision: ACCEPT|CONDITIONAL|REWORK, approvers[], reviewed_at

Review.run(scope) -> ArchitectureReview
Review.blockImplementation(finding) -> GateStatus
```

## Mandatory rules

1. No external writing module goes into implementation without revision `ACCEPT` or condition explicitly accepted.
2. Critical security findings, undefined state owner or violation of Law block the gate.
3. The output is a versioned baseline; later changes use ADR and regression testing.

## Justification and expansions

This phase prevents documentation from becoming just inspiration. Review turns RFCs into implementation contracts and reveals overlaps before they become code. In the future, it may include independent review and certification of plugins.