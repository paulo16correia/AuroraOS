# Aurora OS — RFC 040: Modelo de domínio canónico

**Estado:** Normativo · **Depende de:** RFC 020

## Objetivo

Definir os objetos internos da Aurora antes da implementação. Nenhuma classe, tabela, mensagem de evento ou contrato público pode introduzir semântica nova sem alterar esta RFC ou criar uma RFC de extensão.

## Convenções e relações

Todos os objetos incluem `id`, `tenant_id`, `created_at`, `updated_at`, `version`, `source_refs[]` e `sensitivity`, salvo quando indicado. Identificadores são UUIDv7. Estados só transitam pelas máquinas definidas abaixo.

```text
Identity ─owns→ Mind ─contains→ Memory / Knowledge / Experience / Goals
Conversation ─contains→ Session ─contains→ Event ─starts→ CognitiveCycle
CognitiveCycle ─owns→ Context / Thought / Decision / WorkingMemory
Decision ─may create→ Plan / Task / Action / ToolCall
ToolCall ─produces→ Observation ─feeds→ Reflection ─proposes→ LearningProposal
WorldModel ─contains→ Node ─linked by→ Edge / MemoryLink
Permission ─authorizes→ Action; VaultItem ─is leased to→ ToolCall
```

## Objetos de identidade, conversa e contexto

| Objeto | Campos adicionais obrigatórios | Estados e ciclo de vida |
| --- | --- | --- |
| `Identity` | `name`, `profile_version`, `locale`, `values[]`, `disclosure_policy` | `DRAFT → ACTIVE → RETIRED`; só uma ativa por Mind. |
| `SelfModel` | `capability_snapshot`, `permission_snapshot`, `resource_snapshot`, `operational_state`, `current_focus` | `BOOTING → READY|BUSY|WAITING|DEGRADED|PAUSED|RECOVERING`; é sempre observado e datado. |
| `SituationAssessment` | `local_time`, `user_availability`, `signals`, `needs`, `risk_posture`, `expires_at` | `CURRENT → INVALIDATED|EXPIRED`; é temporal e nunca persistido como facto. |
| `Personality` | `identity_id`, `voice_json`, `interaction_rules[]`, `prohibited_claims[]` | versionada; `DRAFT → ACTIVE → RETIRED`. |
| `Conversation` | `channel`, `participants[]`, `access_policy_id`, `last_activity_at` | `OPEN → ARCHIVED → DELETED`; arquivar não apaga memória. |
| `Session` | `conversation_id`, `principal_id`, `started_at`, `expires_at`, `auth_context` | `ACTIVE → EXPIRED|REVOKED`; nunca sobrevive a revogação. |
| `Context` | `cycle_id`, `attention_set_id`, `memory_refs[]`, `goal_refs[]`, `budget` | `BUILDING → FROZEN → RELEASED`; é imutável depois de congelado. |
| `Emotion` | `label`, `intensity`, `basis_refs[]`, `valid_until` | nome técnico recomendado: `InteractionState`; é estado comunicacional, não sentimento. `PROPOSED → ACTIVE → EXPIRED`. |

## Objetos de conhecimento e realidade

| Objeto | Campos adicionais obrigatórios | Estados e ciclo de vida |
| --- | --- | --- |
| `Memory` | `kind`, `subject`, `predicate`, `object`, `confidence`, `evidence_refs[]`, `retention_until` | `CANDIDATE → ACTIVE ↔ DISPUTED → SUPERSEDED|RETRACTED|EXPIRED`. |
| `MemoryLink` | `from_memory_id`, `to_memory_id`, `relation_type`, `weight`, `reason_refs[]` | `PROPOSED → ACTIVE → RETRACTED`; não é inferência livre. |
| `Knowledge` | `claim`, `scope`, `evidence_refs[]`, `valid_time`, `certainty` | `PROPOSED → ASSERTED|DISPUTED → RETRACTED`. |
| `Belief` | `claim`, `scope`, `confidence`, `evidence_for[]`, `evidence_against[]`, `review_at` | `CANDIDATE → ACTIVE ↔ CHALLENGED → SUPERSEDED|RETRACTED|EXPIRED`. |
| `Preference` | `owner`, `dimension`, `value`, `strength`, `basis`, `scope`, `consent_required` | `CANDIDATE|ACTIVE → REJECTED|EXPIRED`; explícita prevalece sobre inferida. |
| `RelationshipAssertion` | `subject`, `relation_type`, `object`, `authority_scope`, `confidence`, `valid_time` | `PROPOSED → ACTIVE|DISPUTED → ENDED|RETRACTED`. |
| `Node` | `world_model_id`, `type`, `canonical_name`, `aliases[]`, `attributes_json` | `ACTIVE → MERGED|ARCHIVED`; fusão preserva redirecionamento. |
| `Edge` | `subject_node_id`, `predicate`, `object_node_id|literal`, `qualifiers`, `valid_time` | `PROPOSED → ASSERTED|DISPUTED → RETRACTED`. |
| `WorldModel` | `mind_id`, `schema_version`, `node_refs[]`, `edge_refs[]`, `last_reconciled_at` | `BUILDING → ACTIVE → DEGRADED|RETIRED`. |
| `Observation` | `observer`, `observed_at`, `modality`, `payload_ref`, `integrity`, `external_ref` | `RAW → VALIDATED|REJECTED → CONSOLIDATED|EXPIRED`. |
| `Signal` | `source_event`, `kind`, `severity`, `urgency`, `interruptibility`, `expires_at` | `NEW → QUEUED|FOCUSED|SUPPRESSED → RESOLVED|EXPIRED`. |
| `Need` | `kind`, `intensity`, `evidence_refs[]`, `recommended_goal`, `satisfaction_condition` | `DETECTED → ACKNOWLEDGED|PLANNED → SATISFIED|DEFERRED|EXPIRED`. |
| `CuriosityProposal` | `question`, `expected_value`, `allowed_sources`, `budget`, `approval_required` | `CANDIDATE → APPROVED|SCHEDULED → RESEARCHING → LEARNED|REJECTED|EXPIRED`. |
| `ResourceState` | `cpu`, `memory`, `disk`, `queue`, `cost`, `operational_energy` | `NORMAL|CONSTRAINED|CRITICAL|UNKNOWN`; substituído por observação mais recente. |
| `Experience` | `episode_ref`, `outcome`, `lessons[]`, `evidence_refs[]` | `CAPTURED → REVIEWED → CONSOLIDATED|DISCARDED`. |

## Objetos de intenção e deliberação

| Objeto | Campos adicionais obrigatórios | Estados e ciclo de vida |
| --- | --- | --- |
| `Goal` | `outcome`, `owner`, `priority`, `success_criteria[]`, `constraints`, `deadline` | `DRAFT → ACTIVE ↔ PAUSED|BLOCKED → COMPLETED|CANCELLED|FAILED`. |
| `Plan` | `goal_id`, `revision`, `assumptions[]`, `task_refs[]`, `rationale` | `PROPOSED → APPROVED → ACTIVE → SUPERSEDED|CLOSED`. |
| `Task` | `goal_id`, `kind`, `dependencies[]`, `acceptance_tests[]`, `risk`, `assignee` | `DRAFT → READY → RUNNING → SUCCEEDED|FAILED|WAITING_*|CANCELLED`. |
| `Thought` | `intent`, `objective_ref`, `evidence_refs[]`, `options[]`, `uncertainty[]`, `recommendation` | `DRAFT → VALIDATED → SUPERSEDED|REJECTED`; não executa. |
| `Decision` | `mode`, `alternatives[]`, `policy_refs[]`, `expiry_at`, `approval_required` | `PROPOSED → COMMITTED → EXECUTED|SUPERSEDED|EXPIRED`. |
| `Action` | `decision_id`, `effect_type`, `target_ref`, `parameters_hash`, `reversibility` | `PROPOSED → AUTHORIZED → DISPATCHED → OBSERVED|CANCELLED|UNKNOWN`. |
| `WorkingMemory` | `cycle_id`, `capacity`, `expires_at`, `items[]` | `OPEN → SEALED → DISCARDED|EXPIRED`; não é persistida como facto. |
| `Reflection` | `outcome`, `evidence_refs[]`, `lessons[]`, `proposal_refs[]` | `DRAFT → ACCEPTED|REJECTED → IMPLEMENTED|EXPIRED`. |

## Objetos de execução e segurança

| Objeto | Campos adicionais obrigatórios | Estados e ciclo de vida |
| --- | --- | --- |
| `ToolCall` | `tool_id`, `capability`, `input_hash`, `idempotency_key`, `external_ref` | `PROPOSED → AUTHORIZED → QUEUED → RUNNING → SUCCEEDED|FAILED|CANCELLED|UNKNOWN`. |
| `Permission` | `principal`, `action`, `resource_scope`, `effect`, `conditions`, `expires_at` | `ACTIVE → REVOKED|EXPIRED`; `DENY` prevalece. |
| `ConstitutionalAssessment` | `subject`, `articles_checked[]`, `result`, `conflicts[]`, `evidence_refs[]` | `PASS|FAIL|REVIEW`; é anexado a decisão/política de risco elevado. |
| `VaultItem` | `provider_ref`, `purpose`, `allowed_tool_ids[]`, `rotation_due_at` | `ACTIVE → ROTATING → REVOKED|EXPIRED`; valor nunca entra no domínio. |
| `LearningProposal` | `type`, `change_set`, `evaluation_plan`, `rollback_plan` | `PROPOSED → APPROVED → TESTING → DEPLOYED|REJECTED|ROLLED_BACK`. |

## Interfaces e regras obrigatórias

```text
Domain.validate(object) -> ValidationResult
Domain.transition(object_ref, target_state, evidence) -> DomainEvent
Domain.link(source_ref, target_ref, relation, provenance) -> MemoryLink|Edge
```

1. Cada transição DEVE validar pré-condições, autorização e versão otimista.
2. Referências substituem duplicação de objetos sensíveis; relações têm proveniência.
3. `Observation` não é `Knowledge`; `Thought` não é `Decision`; `Decision` não é `Action`.
4. Não existem estados implícitos em texto nem campos “livres” para permissões, efeitos ou ciclo de vida.

## Casos limite, erro e justificação

Referências quebradas tornam o objeto inválido para execução, mas não são apagadas da auditoria. Conflitos de versão falham explicitamente e exigem recarregamento. Esta separação de objetos impede que uma única mensagem seja ao mesmo tempo facto, plano, autorização e execução.

## Expansões futuras

Tipos multimodais, entidades jurídicas, fluxos de aprovação multiparte, ontologias por domínio e migrações de esquema declarativas.
