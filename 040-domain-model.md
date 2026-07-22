# Aurora OS — RFC 040: Canonical Domain Model

**Status:** Normative · **Depends on:** RFC 020

## Objective

Define Aurora's internal objects before deployment. No class, table, event message, or public contract may introduce new semantics without changing this RFC or creating an extension RFC.

## Conventions and relationships

All objects include `id`, `tenant_id`, `created_at`, `updated_at`, `version`, `source_refs[]`, and `sensitivity` unless noted. Identifiers are UUIDv7. States only transit through the machines defined below.

```text
Identity ─owns→ Mind ─contains→ Memory / Knowledge / Experience / Goals
Conversation ─contains→ Session ─contains→ Event ─starts→ CognitiveCycle
CognitiveCycle ─owns→ Context / Thought / Decision / WorkingMemory
Decision ─may create→ Plan / Task / Action / ToolCall
ToolCall ─produces→ Observation ─feeds→ Reflection ─proposes→ LearningProposal
WorldModel ─contains→ Node ─linked by→ Edge / MemoryLink
Permission ─authorizes→ Action; VaultItem ─is leased to→ ToolCall
```

## Identity, conversation and context objects

| Object | Additional required fields | States and life cycle |
| --- | --- | --- |
| `Identity` | `identity_id`, `entity_id`, `name`, `purpose`, `profile_version`, `locale`, `values[]`, `disclosure_policy` | `DRAFT → ACTIVE → RETIRED`; only one active per Entity/Mind. In VS-002, `name`, `purpose`, `locale` and Genome provenance are required. |
| `Entity` | `entity_id`, `genome_id`, `lifecycle_state`, `mind_state_version`, `created_at` | `CREATED → RECOVERING → READY ↔ STOPPED → RETIRED`; owns Sessions, Mind State and Life History. |
| `EntityRuntimeContext` | `entity_id`, `genome_id`, `lifecycle_state`, `active_session?`, `snapshot_ref?`, `restored_at?` | created/loaded by the Kernel per process; does not belong to a Session. |
| `Genome` | `family`, `version`, `constitution_version`, `law_set_version`, `allowed_capabilities[]`, `signature` | `DRAFT → RELEASED → RETIRED`; it is inheritable and does not contain acquired data. |
| `DevelopmentState` | `stage`, `autonomy_ceiling`, `evidence_refs[]`, `assessment_at` | `PROBATION|ACTIVE|RESTRICTED|PAUSED`; promotion is approved and reversible. |
| `LifeEpisode` | `kind`, `occurred_at`, `narrative_summary`, `evidence_refs[]`, `significance` | `CANDIDATE → VERIFIED|RETRACTED`; does not replace auditing. |
| `InstanceLifecycle` | `state`, `entered_at`, `reason`, `active_cycles[]`, `last_snapshot` | `CREATED → BOOTSTRAPPING → RECOVERING → READY … → STOPPED|RETIRED`; Kernel is the owner. |
| `SelfModel` | ZZProtect56zz `BOOTING → READY|BUSY|WAITING|DEGRADED|PAUSED|RECOVERING`; it is always observed and dated. VS-002 creates the initial version without capabilities, permissions, beliefs, preferences, or relationships. |
| `SituationAssessment` | `local_time`, `user_availability`, `signals`, `needs`, `risk_posture`, `expires_at` | `CURRENT → INVALIDATED|EXPIRED`; it is temporal and never persisted as a fact. |
| `Personality` | `identity_id`, `voice_json`, `interaction_rules[]`, `prohibited_claims[]` | versioned; `DRAFT → ACTIVE → RETIRED`. |
| `Conversation` | `channel`, `participants[]`, `access_policy_id`, `last_activity_at` | `OPEN → ARCHIVED → DELETED`; archiving does not erase memory. |
| `Session` | `conversation_id`, `principal_id`, `started_at`, `expires_at`, `auth_context` | `ACTIVE → EXPIRED|REVOKED`; never survives revocation. |
| `Context` | `cycle_id`, `attention_set_id`, `memory_refs[]`, `goal_refs[]`, `budget` | `BUILDING → FROZEN → RELEASED`; it is immutable once frozen. || `Emotion` | `label`, `intensity`, `basis_refs[]`, `valid_until` | recommended technical name: `InteractionState`; It is a communicational state, not a feeling. `PROPOSED → ACTIVE → EXPIRED`. |

## Objects of knowledge and reality

| Object | Additional required fields | States and life cycle |
| --- | --- | --- |
| `Memory` | `memory_id`, `entity_id`, `kind`, `subject`, `predicate`, `object_value`, `confidence`, `provenance`, `signal_id`, `observation_id`, `anchors[]`, `status` | `CANDIDATE → ACTIVE ↔ DISPUTED → SUPERSEDED|RETRACTED|EXPIRED`. VS-003 only creates `EPISODIC/ACTIVE` with `USER_ASSERTION`. |
| `MemoryRetrieval` | `memory_id`, `reason`, `confidence`, `retrieved_at` | ephemeral per cycle; it is the only way for a Memory to enter Context. |
| `MemoryLink` | `from_memory_id`, `to_memory_id`, `relation_type`, `weight`, `reason_refs[]` | `PROPOSED → ACTIVE → RETRACTED`; it is not free inference. |
| `Knowledge` | `claim`, `scope`, `evidence_refs[]`, `valid_time`, `certainty` | `PROPOSED → ASSERTED|DISPUTED → RETRACTED`. |
| `Belief` | `claim`, `scope`, `confidence`, `evidence_for[]`, `evidence_against[]`, `review_at` | `CANDIDATE → ACTIVE ↔ CHALLENGED → SUPERSEDED|RETRACTED|EXPIRED`. |
| `Preference` | `owner`, `dimension`, `value`, `strength`, `basis`, `scope`, `consent_required` | `CANDIDATE|ACTIVE → REJECTED|EXPIRED`; explicit prevails over inferred. |
| `RelationshipAssertion` | `subject`, `relation_type`, `object`, `authority_scope`, `confidence`, `valid_time` | `PROPOSED → ACTIVE|DISPUTED → ENDED|RETRACTED`. |
| `Node` | `world_model_id`, `type`, `canonical_name`, `aliases[]`, `attributes_json` | `ACTIVE → MERGED|ARCHIVED`; fusion preserves redirection. |
| `Edge` | `subject_node_id`, `predicate`, `object_node_id|literal`, `qualifiers`, `valid_time` | `PROPOSED → ASSERTED|DISPUTED → RETRACTED`. |
| `WorldModel` | `mind_id`, `schema_version`, `node_refs[]`, `edge_refs[]`, `last_reconciled_at` | `BUILDING → ACTIVE → DEGRADED|RETIRED`. |
| `Observation` | `observer`, `observed_at`, `modality`, `payload_ref`, `integrity`, `external_ref` | `RAW → VALIDATED|REJECTED → CONSOLIDATED|EXPIRED`. |
| `Signal` | `source_event`, `kind`, `severity`, `urgency`, `interruptibility`, `expires_at` | `NEW → QUEUED|FOCUSED|SUPPRESSED → RESOLVED|EXPIRED`. |
| `DomainEvent` | `type`, `producer`, `aggregate_ref`, `payload_ref`, `correlation_id`, `schema_version` | immutable; published by outbox and delivered idempotently. |
| `Need` | `kind`, `intensity`, `evidence_refs[]`, `recommended_goal`, `satisfaction_condition` | `DETECTED → ACKNOWLEDGED|PLANNED → SATISFIED|DEFERRED|EXPIRED`. || `CuriosityProposal` | `question`, `expected_value`, `allowed_sources`, `budget`, `approval_required` | `CANDIDATE → APPROVED|SCHEDULED → RESEARCHING → LEARNED|REJECTED|EXPIRED`. |
| `ResourceState` | `cpu`, `memory`, `disk`, `queue`, `cost`, `operational_energy` | `NORMAL|CONSTRAINED|CRITICAL|UNKNOWN`; replaced with most recent observation. |
| `Experience` | `episode_ref`, `outcome`, `lessons[]`, `evidence_refs[]` | `CAPTURED → REVIEWED → CONSOLIDATED|DISCARDED`. |

## Objects of intention and deliberation

| Object | Additional required fields | States and life cycle |
| --- | --- | --- |
| `Goal` | `outcome`, `owner`, `priority`, `success_criteria[]`, `constraints`, `deadline` | `DRAFT → ACTIVE ↔ PAUSED|BLOCKED → COMPLETED|CANCELLED|FAILED`. |
| `EntityState` | `state_id`, `entity_id`, `availability`, `focus`, `energy`, `pending_goals`, `version`, `updated_at` | observed and versioned; VS-004 only uses `READY|STOPPED` and does not represent emotion. |
| `Need` | `need_id`, `entity_id`, `kind`, `reason`, `intensity`, `recommended_goal_ref`, `evidence_refs[]`, `status` | `DETECTED → SATISFIED`; VS-004 does not allow Need to create Action, Task or Plan. |
| `GoalEvaluation` | `evaluation_id`, `entity_id`, `goal_id`, `observation_id`, `progress_delta`, `evidence_refs[]`, `outcome`, `evaluated_at` | immutable; is only created after Observation/Reflection. |
| `Plan` | `plan_id`, `entity_id`, `type`, `goal_ref`, `created_from_need`, `steps[]`, `basis_memory_refs[]`, `execution_mode`, `version` | `PROPOSED → REVIEWED → APPROVED → REJECTED|COMPLETED`. VS-006 can create an internal Task from an explicit Plan; never creates Action. |
| `PlanStep` | `sequence`, `action`, `execution_mode`, `status` | `PENDING → READY → EXECUTED|SKIPPED|BLOCKED`; VS-005 only allows `PENDING` and does not run them. |
| `Task` | `task_id`, `entity_id`, `goal_ref`, `plan_ref`, `type`, `status`, `result_summary`, `version` | `READY → RUNNING → COMPLETED|FAILED`. VS-007 can use a completed Task as the source of a CapabilityRequest, without creating an Action. |
| `Thought` | `intent`, `objective_ref`, `evidence_refs[]`, `options[]`, `uncertainty[]`, `recommendation` | `DRAFT → VALIDATED → SUPERSEDED|REJECTED`; does not execute. |
| `Decision` | `mode`, `alternatives[]`, `policy_refs[]`, `expiry_at`, `approval_required` | `PROPOSED → COMMITTED → EXECUTED|SUPERSEDED|EXPIRED`. |
| `Action` | `decision_id`, `effect_type`, `target_ref`, `parameters_hash`, `reversibility` | `PROPOSED → AUTHORIZED → DISPATCHED → OBSERVED|CANCELLED|UNKNOWN`. |
| `WorkingMemory` | `cycle_id`, `capacity`, `expires_at`, `items[]` | `OPEN → SEALED → DISCARDED|EXPIRED`; it is not persisted as fact. |
| `Reflection` | `outcome`, `evidence_refs[]`, `lessons[]`, `proposal_refs[]` | `DRAFT → ACCEPTED|REJECTED → IMPLEMENTED|EXPIRED`. |## Execution and security objects

| Object | Additional required fields | States and life cycle |
| --- | --- | --- |
| `ToolCall` | `tool_id`, `capability`, `input_hash`, `idempotency_key`, `external_ref` | `PROPOSED → AUTHORIZED → QUEUED → RUNNING → SUCCEEDED|FAILED|CANCELLED|UNKNOWN`. |
| `Permission` | `principal`, `action`, `resource_scope`, `effect`, `conditions`, `expires_at` | `ACTIVE → REVOKED|EXPIRED`; `DENY` prevails. |
| `Capability` | `capability_id`, `entity_id`, `name`, `kind`, `enabled`, `trusted`, `version` | persistent and versioned catalog; describes availability, not authorization. VS-007 activates internal capabilities only. |
| `CapabilityRequest` | `request_id`, `entity_id`, `task_ref`, `capability_ref`, `reason`, `status` | `REJECTED|UNAVAILABLE|APPROVED`. VS-007.1 requires a request for all recognized external intent, even when the originating Task is historical; It has no provider or execution. |
| `CapabilityResult` | `result_id`, `entity_id`, `request_ref`, `executor_id`, `status`, `summary`, `created_at` | immutable; VS-008 only produces `DRY_RUN|NOT_IMPLEMENTED` through `NOOP_EXECUTOR`. Does not prove external effect. |
| `CapabilityProvider` | `capability`, `application`, `tool_ref`, `availability`, `cost_model`, `constraints[]` | `AVAILABLE|DEGRADED|DISABLED`; receives no authority outside the manifesto. |
| `KernelCommand` | `issuer`, `command_type`, `payload_ref`, `idempotency_key`, `policy_refs[]` | `ACCEPTED|REJECTED → RUNNING → COMPLETED|FAILED`; only Kernel executes. |
| `Mission` | `purpose`, `success_definition`, `boundaries[]`, `goal_refs[]`, `review_at` | `DRAFT → ACTIVE|PAUSED → RETIRED`; guides Goals, does not execute. |
| `ConstitutionalAssessment` | `subject`, `articles_checked[]`, `result`, `conflicts[]`, `evidence_refs[]` | `PASS|FAIL|REVIEW`; is attached to high risk decision/policy. |
| `VaultItem` | `provider_ref`, `purpose`, `allowed_tool_ids[]`, `rotation_due_at` | `ACTIVE → ROTATING → REVOKED|EXPIRED`; value never enters the domain. |
| `LearningProposal` | `type`, `change_set`, `evaluation_plan`, `rollback_plan` | `PROPOSED → APPROVED → TESTING → DEPLOYED|REJECTED|ROLLED_BACK`. |

## Mandatory interfaces and rules

```text
Domain.validate(object) -> ValidationResult
Domain.transition(object_ref, target_state, evidence) -> DomainEvent
Domain.link(source_ref, target_ref, relation, provenance) -> MemoryLink|Edge
```1. Each transition MUST validate preconditions, authorization, and optimistic versioning.
2. References replace duplication of sensitive objects; relationships have provenance.
3. `Observation` is not `Knowledge`; `Thought` is not `Decision`; `Decision` is not `Action`.
4. There are no states implicit in text or “free” fields for permissions, effects or lifecycle.

## Limit cases, error and justification

Broken references make the object invalid for execution, but are not cleared from the audit. Version conflicts explicitly fail and require reloading. This separation of objects prevents a single message from being at the same time fact, plan, authorization and execution.

## Future expansions

Multimodal types, legal entities, multiparty approval flows, per-domain ontologies, and declarative schema migrations.