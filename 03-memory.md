# Aurora OS — RFC 03: Persistent Memory

**State:** Normative · **Depends on:** RFC 01–02

## Objective

Specify useful, correctable memory with provenance. Memory is a set of domain records, not an unlimited archive of conversations.

## Architecture

```text
Events/documents → candidate extraction → policy validation
                                              │
                      ┌───────────────────────┼──────────────┐
                      ▼                       ▼              ▼
episodic procedural semantics
└─────────→ index + provenance ←─────┘
                                             │
Query → ACL/sort filter → hybrid search → context
```

## Data structures

```text
Memory
  id, kind: EPISODIC|SEMANTIC|PROCEDURAL|WORKING
  subject_ref, predicate, object_json, summary
  source_refs[], evidence_refs[], confidence: 0..1
  status: CANDIDATE|ACTIVE|DISPUTED|SUPERSEDED|RETRACTED|EXPIRED
  sensitivity: PUBLIC|PRIVATE|CONFIDENTIAL|SECRET
  access_policy_id, valid_from, valid_to, retention_until
  embedding_ref, created_by: USER|SYSTEM|IMPORT

MemoryRevision
  id, memory_id, operation: CREATE|CONFIRM|CORRECT|MERGE|RETRACT|EXPIRE
  actor, reason, prior_hash, new_hash, at
````

WORKING` is ephemeral and does not enter into lasting research. Facts inferred without confirmation start at `CANDIDATE`; they can only guide questions or suggestions, not high-impact actions.

## Interfaces

```text
MemoryService.record(candidate, provenance) -> Memory
MemoryService.search(query, access_context, filters) -> RankedMemory[]
MemoryService.revise(memory_id, operation, actor) -> MemoryRevision
MemoryService.forget(memory_id, actor) -> tombstone
```

## Mandatory rules

1. A memory MUST have an origin and access policy; without both, it is not persisted.
2. Search MUST apply ACL and classification before semantic calculation.
3. Correction by owner MUST prevail over automatic inference.
4. `RETRACTED` is not immediately deleted from the audit log, but is no longer recoverable for normal reasoning.
5. The system MUST NOT consolidate secrets, credentials or sensitive data from third parties without a specific rule.

## Limit and error cases

- **Contradictory memories:** keep both, mark conflict and ask for confirmation when material.
- **Deletion requested:** remove from active indexes, propagate to caches and mark applicable legal hold; inform real scope.
- **Research without evidence:** answer that you did not find confirmation, never fill in gaps with a similar memory.
- **Index failure:** resort to limited structured query; do not declare absence of memory with certainty.

## Justification

Separating candidates from active facts reduces persistent hallucinations. Provenance and revisions allow you to correct a memory without erasing the history of how the system reached a conclusion.

## Future expansions

Scheduled consolidation, document import with consent, retention by jurisdiction, memories shared by team and automatic obsolescence assessment.