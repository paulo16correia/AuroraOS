# Aurora OS — Execution Trace Specification: VS-003 Memory Retrieval + Personal Continuity

**Status:** Authorized by ADR-002
**Use case:** learn a declared preference → shutdown → restore → recover it in an auditable way

## Objective

Demonstrate personal continuity between Sessions. Aurora must persist a structured episodic memory, created from an explicit user declaration, and retrieve it only for the same Entity when the next message is relevant.

## Normative flow

```text
Session 009
User: "My name is Paulo and I like programming Python."
  → Perception → Attention
  → MEMORY_RETRIEVAL([])
  → Context → SelfModel → Thought → Response
  → MemoryCreated(EPISODIC, Paulo, LIKES_PROGRAMMING_LANGUAGE, Python)
  → shutdown

Session 010
User: "What language do you think I should use?"
  → Perception → Attention
  → MEMORY_RETRIEVAL([memory, reason=user_preference, confidence=0.91])
  → Context(memory_refs=[memory]) → SelfModel → Thought → Decision → Response
→ "As I know you like programming Python, it could be a good choice depending on the objective."
```

## Minimum contracts

```text
Memory
  memory_id, entity_id, kind: EPISODIC, status: ACTIVE
  subject: Paulo
  predicate: LIKES_PROGRAMMING_LANGUAGE
  object_value: Python
  confidence: 0.91
  provenance: USER_ASSERTION
  signal_id, observation_id, anchors[], created_at

MemoryRetrieval
  memory_id, reason: user_preference, confidence: 0.91
```

## Mandatory rules

1. Only explicit statements recognized by the deterministic extractor can create a VS-003 memory.
2. Recovery MUST first filter by `entity_id` and `status=ACTIVE`; there is no global memory in this slice.
3. A recovered memory MUST go into `Context.memory_refs` and be registered in `MEMORY_RETRIEVAL` before creating the Context.
4. The provider can only mention a preference if the corresponding memory has been provided by the Kernel.
5. Lack of result produces `MEMORY_RETRIEVAL([])`; never justifies a claim of remembrance.
6. New memory maintains origin, Signal/Observation anchors and `MemoryCreated` event; is not an unrelated sentence.

## Events and trace

| Situation | Event | Trace |
| --- | --- | --- |
| Empty query | `MemoryRetrieved` | `MEMORY_RETRIEVAL(retrieved=[])` |
| Explicit statement | `MemoryCreated`, `MemoryStored` | `MEMORY(CREATED)` |
| Relevant query | `MemoryRetrieved` | `MEMORY_RETRIEVAL(retrieved=[id, reason, confidence])` |
| Preference used | `ThoughtCreated` | `MEMORY_RETRIEVAL(USED_FOR_RESPONSE)` |

## Acceptance criteria

1. The first Session creates a single structured memory for Paulo/Python, with provenance `USER_ASSERTION`.
2. After shutdown and new Session, memory is recovered to the same Entity with confidence `0.91`.
3. The answer recommends Python based on this memory and does not claim knowledge outside of the retrieved data.
4. A different Entity does not recover Roberto's memory.
5. The trace shows the order `ATTENTION → MEMORY_RETRIEVAL → CONTEXT → SELF_MODEL`.
6. LAW-008 continues to prevent the identity response from being provider or message dependent.

## Deliberate limits

There is no semantic search, embeddings, inferred preferences, consolidation, memory patching, UI, or tool access. These mechanisms will only be introduced in own slices, with explicit evaluation criteria and permissions.