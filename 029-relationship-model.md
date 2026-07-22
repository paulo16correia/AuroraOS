# Aurora OS — RFC 029: Model of relationships and preferences

**State:** Normative · **Depends on:** RFC 04, 028, 040–042

## Objective

Model relationships between people, organizations, accounts, projects and resources; and distinguish these relationships from the user's operational preferences. The system must know “who has a relationship with whom” without inferring undemonstrated intimacy, authority or access.

## Architecture

```text
World Model entities + temporal evidence
                    │
        ┌───────────┴────────────┐
        ▼                        ▼
 RelationshipAssertion      Preference
        │                        │
        ▼                        ▼
graph/World Model decision, style and recommendation
```

## Data structures

```text
RelationshipAssertion
  id, subject_ref, relation_type, object_ref
  qualifiers_json, authority_scope, confidence, evidence_refs[]
  valid_from, valid_to, status: PROPOSED|ACTIVE|DISPUTED|ENDED|RETRACTED

Preference
  id, owner_ref, subject_ref, dimension, value_json
  strength: 0..1, basis: EXPLICIT|OBSERVED|INFERRED
  evidence_refs[], scope_json, status: CANDIDATE|ACTIVE|REJECTED|EXPIRED
  review_at, consent_required
```

Relationships can be family, professional, contractual, belonging, dependency, contact or property. `authority_scope` is always explicit: “you are a client” does not give authorization to act on the person’s behalf. Preferences include tool, format, time, tone, or technical choice; They do not define Aurora's personality.

## Interfaces

```text
Relationships.assert(candidate, evidence) -> RelationshipAssertion
Relationships.end(id, evidence) -> RelationshipAssertion
Preferences.setExplicit(owner, dimension, value) -> Preference
Preferences.infer(candidate) -> Preference
Preferences.resolve(owner, context) -> Preference[]
```

## Mandatory rules

1. Relationship, permission and identity are separate objects; none is implicitly derived from another.
2. Inferred preferences cannot trigger purchases, external communications, sensitive data, or persistent changes without confirmation.
3. Third party relationships are only stored if relevant, authorized and subject to proportional retention.
4. Temporal history must preserve beginning/end; reassigning a relationship does not rewrite the past.

## Limit and error cases

- **Two people with the same name:** do not create a relationship until entity resolution is completed.
- **Explicit preference contradicts inferred:** explicit prevails and the inferred is `REJECTED`.
- **Relation expires:** removes current decisions, but preserves historical evidence according to policy.

## Justification

Relationships make the World Model socially useful; preferences make decisions personal without converting habits into personality traits or rights of action.

## Future expansions

Team relationships, formal delegation, contextual preferences, consented import of contacts and privacy policies per relationship.