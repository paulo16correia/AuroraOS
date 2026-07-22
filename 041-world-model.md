# Aurora OS — RFC 041: World Model — representation of operational reality

**State:** Normative · **Depends on:** RFC 04, 040

## Objective

Maintain a temporal, evidenced and consultable representation of the reality relevant to Aurora. The World Model does not store isolated sentences: it models entities, attributes, state, history, relationships, events and observed capabilities.

## Architecture```text
Observation / memory confirmed / import authorized
                          │
                          ▼
normalization and entity resolution
                          │
                          ▼
node + edge + validity + provenance
                          │
                          ▼
                World Model versionado
                          │
                          ▼
attention, planning, decision and evidence-based explanation
```## Reality Model```text
Person ─has→ Email account ─receives→ Message
Person ─participates_in→ Discord Server ─contains→ Channel
Person ─works_in→ Project ─uses→ VPS ─runs→ Service
Service ─is_deploy_de→ Repository ─has→ Credential (vault reference)
Project ─has→ Objective ─decomposed_into→ Task
```A relationship asserts only what its evidence allows. “The person has Discord” does not imply that Aurora can read that Discord; Access is a distinct relationship from permission.

## Data structures```text
WorldAssertion
  id, subject_ref, predicate, object_ref|literal
  evidence_refs[], confidence, valid_from, valid_to
  observed_at, asserted_at, status: PROPOSED|CURRENT|HISTORICAL|DISPUTED|RETRACTED

EntityResolution
  candidate_ref, observed_name, match_score, evidence_refs[]
  decision: MATCH|CREATE|DEFER, decided_by, decided_at

WorldModelVersion
  id, mind_id, parent_version, change_set_refs[], status: DRAFT|ACTIVE|ROLLED_BACK
```## Interfaces```text
World.observe(observation_id) -> WorldAssertion[]
World.resolve(entity_candidate) -> EntityResolution
World.query(pattern, as_of, access_context) -> Subgraph
World.reconcile(version_id, evidence) -> WorldModelVersion
```## Mandatory rules

1. Every statement MUST separate `observed_at` from `asserted_at` and be queryable “as of”.
2. Identity of person, account and resource is only merged after resolution rule and sufficient evidence.
3. Permissions, secrets and effective access are distinct objects of property and social relationship.
4. The World Model MUST support ignorance: absence of an edge does not prove absence in the world.
5. A tool can create `Observation`, but never a `CURRENT` assertion without validation.

## Limit and error cases

- **Email or Discord reassociated:** ends the validity of the previous relationship; Don't rewrite history.
- **Contradictory resource:** keep parallel statements `DISPUTED`, do not choose based on text popularity.
- **External entity deleted:** mark observation/state as inaccessible, without deleting permitted historical evidence.
- **Partial import:** create version `DRAFT`; do not make it a source for decisions until validation is complete.

## Justification

The world model makes it possible to plan on operational reality (“which VPS runs this service?”) instead of searching for similar phrases. Temporality and provenance prevent the representation from appearing more certain than it is.

## Future expansions

Inventory status, infrastructure digital twins, impact simulation, verified sources and declarative consistency rules.