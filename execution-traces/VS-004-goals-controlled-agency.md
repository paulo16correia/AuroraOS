# Aurora OS — Execution Trace Specification: VS-004 Goals + Controlled Agency Loop

**Estado:** Autorizado por ADR-003  
**Caso de utilização:** criar propósito operacional → ajudar → avaliar progresso → restaurar → descrever objetivo

## Objetivo

Demonstrar que uma Entity possui direção operacional persistente sem executar autonomia silenciosa. VS-004 cria Goal, EntityState e Need inicial; o Kernel avalia progresso após Reflection e antes de persistir a memória do ciclo.

## Fluxo normativo

```text
CreateEntity(Roberto, purpose="ajudar-te a evoluir na programação")
  → GoalCreated(ASSIST_USER, ACTIVE)
  → EntityStateInitialized(READY, USER_ASSISTANCE, energy=1.0)
  → NeedDetected(UNDERSTAND_USER, intensity=0.4)

User: "Estou a aprender Python."
  → ciclo normal → Observation → Reflection
  → GoalEvaluation(progress_events +1, evidence=interaction)
  → MemoryCreated(LEARNING_TOPIC, Python)
  → NeedEvaluation(SATISFIED quando existe contexto suficiente)

shutdown → restore

User: "Qual é o teu objetivo?"
  → Goal carregado pelo Kernel
  → "O meu objetivo ativo é ajudar-te a evoluir na programação."
```

## Contratos mínimos

```text
Goal
  goal_id: "goal:{entity_id}:assist-user"
  entity_id, type: ASSIST_USER, title, outcome
  status: ACTIVE, priority: 0.8
  progress_events: 0..n, evidence_refs[], version
  created_from_ref: Identity, created_at, updated_at

EntityState
  state_id: "entity-state:{entity_id}"
  entity_id, availability: READY|STOPPED
  focus: USER_ASSISTANCE, energy: 0..1
  pending_goals, updated_at, version

Need
  need_id: "need:{entity_id}:understand-user"
  entity_id, kind: UNDERSTAND_USER, reason
  intensity: 0..1, status: DETECTED|SATISFIED
  recommended_goal_ref, evidence_refs[], updated_at

GoalEvaluation
  evaluation_id, entity_id, goal_id, observation_id
  progress_delta, evidence_refs[], outcome, evaluated_at
```

## Regras obrigatórias

1. O Goal inicial deriva apenas do propósito persistido na Identity; parâmetros posteriores não o substituem.
2. `progress_events` só aumenta com Observation/Reflection do ciclo atual e evidência registada.
3. EntityState é operacional, não emocional; `energy=1.0` é uma baseline de capacidade, não sentimento.
4. Need não cria efeito externo, Goal adicional, Plan ou Task. Só é registada e pode ser lida pelo Kernel/UI futura.
5. A resposta a “Qual é o teu objetivo?” vem de Goal `ACTIVE` carregado pelo Kernel, não de instrução textual no provider.
6. Snapshot de shutdown inclui referências a Goal, EntityState e Need.

## Eventos e trace

| Situação | Evento | Trace |
| --- | --- | --- |
| Nascimento/compatibilidade | `GoalCreated`, `EntityStateInitialized`, `NeedDetected` | `GOAL(CREATED)`, `ENTITY_STATE(INITIALIZED)`, `NEED(DETECTED)` |
| Ciclo | `GoalEvaluated`, `EntityStateUpdated` | `GOAL_EVALUATION(progress_delta, evidence)` |
| Need | `NeedUpdated` | `NEED(EVALUATED)` |
| Pergunta sobre objetivo | `ThoughtCreated` | `GOAL(USED_FOR_GOAL_DESCRIPTION)` |

## Critérios de aceitação

1. O nascimento cria exatamente um Goal `ASSIST_USER`, um EntityState e uma Need por Entity.
2. Uma interação de assistência incrementa `progress_events` e cria GoalEvaluation com referência à Observation.
3. Após shutdown/restauro, Goal e progresso mantêm os mesmos IDs e versão coerente.
4. “Qual é o teu objetivo?” responde apenas com o Goal ativo persistido.
5. Uma Need é auditada, mas não resulta em Action, ToolCall, Plan ou Task.
6. VS-000–VS-003 permanecem verdes.

## Limites deliberados

Não existem Planner, Scheduler, criação conversacional de Goals, Tasks, autonomia externa, notificações, ferramentas ou evolução de Genome. VS-004 cria estado orientador, não comportamento autónomo.
