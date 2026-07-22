# ADR — Architecture Decision Records

ADRs record changes to the frozen baseline. An RFC is the stable contract; an ADR explains why, how and from what version this contract changes.

## Mandatory format

```text
ADR-<number>-<name>.md
Status: PROPOSED | ACCEPT | REJECTED | SUBSTITUTED
Data, decisores, RFCs afetadas
Context, decision, alternatives, consequences
Migration, compatibility, testing and rollback plan
```

## Process

1. Open ADR with concrete problem and evidence of implementation/review.
2. Evaluate Constitution, Laws, security, state, events and dependencies.
3. Explicitly accept or reject.
4. Only an ADR `ACCEPTED` authorizes changing affected RFCs, code or migrations.