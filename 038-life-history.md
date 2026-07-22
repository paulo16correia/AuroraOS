# Aurora Platform — RFC 038: Life Story and Operational Narrative

**State:** Normative · **Depends on:** RFC 020, 036–037, 040, 043

## Objective

Maintain a verifiable narrative of the instance's history: birth, milestones, early capabilities, incidents, developmental transitions, and relevant learnings. It does not replace the audit diary or transform inferences into autobiography.

## Architecture

```text
eventos auditados + marcos aprovados + DevelopmentState
                              │
                              ▼
                       LifeEpisode candidate
                              │
                              ▼
review/provenance → Life History
                              │
                              ▼
narrative cited for “who am I / what has changed”
```

## Data structures

```text
LifeEpisode
  id, mind_id, kind: BIRTH|MILESTONE|FIRST_USE|INCIDENT|LEARNING|TRANSITION
  occurred_at, title, narrative_summary, evidence_refs[]
  significance: LOW|MEDIUM|HIGH, status: CANDIDATE|VERIFIED|RETRACTED

LifeHistory
  mind_id, episode_refs[], narrative_version, last_compiled_at
```

## Interfaces

```text
LifeHistory.propose(event_refs) -> LifeEpisode
LifeHistory.verify(episode_id) -> LifeEpisode
LifeHistory.narrate(mind_id, audience) -> CitedNarrative
```

## Mandatory rules

1. Each episode MUST reference auditable evidence and date/range.
2. Narrative distinguishes confirmed events from interpretative summaries.
3. The user can correct narrative text without changing the underlying audit journal.
4. Sensitive data does not enter a narrative without an applicable disclosure policy.

## Limit and error cases

- **Unidentifiable “First error”:** responding that there is not enough evidence, not choosing an arbitrary episode.
- **Retracted episode:** remove it from the current narrative, preserving protected revision trail.

## Justification

Life story provides understandable continuity without treating a collection of memories as automatic narrative identity.

## Future expansions

Visual timeline, project narratives, shared milestones, and annual maintenance summaries.