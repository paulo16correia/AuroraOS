# Aurora OS — Execution Trace Specification: VS-005 Persistent Planning Layer

**Estado:** Autorizado por ADR-004  
**Caso de utilização:** recuperar contexto do utilizador → avaliar Goal/Need → propor plano persistente → restaurar → descrever trabalho pendente

## Objetivo

Demonstrar deliberação controlada sem ação. A Entity deve conseguir imaginar uma sequência de ajuda, persistir essa proposta e recuperá-la depois de reiniciar, mantendo a fronteira `Desire → Goal → Need → Plan → Action`: VS-005 termina em `Plan`.

## Fluxo normativo

```text
User assertion: "O meu nome é Paulo e gosto de programar Python."
  → Memory(LIKES_PROGRAMMING_LANGUAGE, USER_ASSERTION)
  → Need(UNDERSTAND_USER) avaliada

User: "Como poderias ajudar-me melhor?"
  → Signal → Attention → MemoryRetrieval(planning_context) → Context
  → Goal(ASSIST_USER, ACTIVE) + Need(UNDERSTAND_USER)
  → PLANNING
  → PlanCreated(ASSISTANCE_PLAN, PROPOSED, SIMULATION_ONLY)
  → Thought/Decision(RESPOND) → Response
  → Observation → Reflection → Memory
  → nenhuma Action / Task / ToolCall

shutdown → MindState(plan_refs[]) → restore

User: "Que objetivo tinhas pendente?"
  → PlanLoaded → Goal + Plan usados para responder
```

## Contratos mínimos

```text
Plan
  plan_id: "plan:{entity_id}:assist-user"
  entity_id
  type: ASSISTANCE_PLAN
  goal_ref: Goal(ASSIST_USER)
  created_from_need: Need(UNDERSTAND_USER)
  status: PROPOSED|REVIEWED|APPROVED|REJECTED|COMPLETED
  steps: PlanStep[]
  basis_memory_refs[]
  execution_mode: SIMULATION_ONLY
  version, created_at, updated_at

PlanStep
  sequence: 1..n
  action: ASK_CLARIFICATION|PROVIDE_RECOMMENDATION
  execution_mode: SIMULATION_ONLY
  status: PENDING
```

## Regras obrigatórias

1. Um Plan só pode nascer de um pedido explícito neste slice; startup, scheduler e Need isolada nunca o criam.
2. `goal_ref`, `created_from_need` e `basis_memory_refs` devem pertencer à Entity ativa.
3. A primeira proposta usa sempre `PROPOSED` e `SIMULATION_ONLY`; qualquer transição posterior é proibida no VS-005.
4. A resposta sobre a proposta ou sobre trabalho pendente é derivada do Plan persistido pelo Kernel.
5. `PlanCreated` não pode coexistir, no mesmo trace, com `ActionCreated`, `TaskCreated`, `CapabilityRequested` ou `CapabilityExecuted`.
6. O snapshot de shutdown inclui `plan_refs`; restauro não gera uma nova proposta nem duplica o objeto.

## Eventos, trace e erros

| Situação | Evento | Trace | Erro/comportamento |
| --- | --- | --- | --- |
| Pedido explícito elegível | `PlanCreated` | `PLANNING(PROPOSED)`, `PLAN_CREATED` | Persistir uma proposta única por Entity/Goal. |
| Plan já existente | `PlanLoaded` | `PLANNING(LOADED)` | Reutilizar, não duplicar. |
| Consulta posterior | `PlanLoaded` | `PLAN(LOADED)` | Expor somente estado persistido. |
| Goal/Need ausente | nenhum Plan | erro controlado do Kernel | Nunca fabricar referências. |
| Pedido sem capacidade | nenhum efeito | `CAPABILITY(NOT_REQUIRED)` | Nenhuma ferramenta é consultada. |

## Critérios de aceitação

1. Criar Roberto, guardar preferência de Paulo e pedir “Como poderias ajudar-me melhor?” cria um único Plan `PROPOSED`.
2. O Plan tem os dois PlanSteps definidos, em `PENDING` e `SIMULATION_ONLY`.
3. O trace contém Goal, Need, `PLANNING` e `PLAN_CREATED`; não contém execução externa.
4. Depois de shutdown/restauro, “Que objetivo tinhas pendente?” descreve o Goal ativo e o Plan proposto persistido.
5. VS-000 a VS-004 continuam verdes.

## Limites deliberados

Não existem Desire persistente, aprovação humana, revisão de plano, Tasks, Scheduler, Capability Registry, ferramentas, ações simuladas ou aprendizagem a partir do plano. VS-005 representa futuro possível; não altera o mundo.
