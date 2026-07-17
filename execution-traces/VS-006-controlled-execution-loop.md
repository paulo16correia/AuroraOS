# Aurora OS — Execution Trace Specification: VS-006 Controlled Internal Execution Loop

**Estado:** Autorizado por ADR-005  
**Caso de utilização:** pedido explícito → Plan → Task interna → Observation → progresso do Goal, sem efeitos externos

## Objetivo

Demonstrar progresso cognitivo verificável sem conceder poder externo. VS-006 acrescenta a camada `Task` e termina depois de uma execução interna controlada.

## Fluxo normativo

```text
User: "Como podes ajudar-me?"
  → Signal → Context → Goal(ASSIST_USER) + Need(UNDERSTAND_USER)
  → PLANNING → PlanCreated(ASSISTANCE_PLAN, PROPOSED)
  → TASK(CREATED, INTERNAL_ANALYSIS, READY)
  → TASK(RUNNING) → TASK_COMPLETED(result="Need more user context")
  → Thought → Decision(RESPOND) → Response
  → Observation → Reflection
  → GoalEvaluation(PROGRESS_DETECTED, +1, evidence=[Task, Observation, Reflection])
  → Memory

shutdown → MindState(task_refs[]) → restore → TaskLoaded
```

## Contrato mínimo

```text
Task
  task_id: "task:{entity_id}:assist-user:internal-analysis"
  entity_id
  goal_ref: Goal(ASSIST_USER)
  plan_ref: Plan(ASSISTANCE_PLAN)
  type: INTERNAL_ANALYSIS
  status: READY|RUNNING|COMPLETED|FAILED
  result_summary: string?
  version, created_at, updated_at
```

## Regras obrigatórias

1. A Task DEVE referenciar um Plan e Goal pertencentes à mesma Entity.
2. VS-006 só pode criar `INTERNAL_ANALYSIS`; qualquer outro tipo falha de forma explícita.
3. A transição é estrita: `READY → RUNNING → COMPLETED|FAILED`. Uma Task concluída não pode ser reexecutada.
4. A Task não pode criar `Action`, `ToolCall`, `CapabilityRequest`, chamada de rede, escrita de ficheiro ou agendamento.
5. Cada Task concluída DEVE gerar evidência para `GoalEvaluation` depois de Observation e Reflection.
6. Pedido externo sem capability, como “Envia um email por mim”, é analisado internamente e termina sem efeito externo.
7. Snapshot e restauro preservam as referências das Tasks, incluindo Tasks `COMPLETED`.

## Eventos, trace e erros

| Situação | Evento | Trace | Resultado |
| --- | --- | --- | --- |
| Plan novo | `TaskCreated` | `TASK(CREATED)` | Task `READY` persistida. |
| Execução interna | `TaskStarted` | `TASK(RUNNING)` | Sem saída para o mundo exterior. |
| Conclusão | `TaskCompleted` | `TASK_COMPLETED` | Resultado e versão persistidos. |
| Restauro | `TaskLoaded` | `TASK(LOADED)` | Não recria nem reexecuta. |
| Tipo inválido/transição inválida | nenhum efeito | erro controlado | Task preservada para auditoria. |

## Critérios de aceitação

1. Nascimento de Entity continua a criar Goal `ASSIST_USER`.
2. “Como podes ajudar-me?” cria Plan e Task, e completa a Task interna.
3. Shutdown/restauro preserva a Task com o mesmo identificador, estado e resultado.
4. “Envia um email por mim” pode criar Task interna, mas não cria Action, ToolCall nem CapabilityRequest; o trace mantém `CAPABILITY(NOT_REQUIRED)`.
5. A suite VS-000–VS-005 permanece verde.

## Limites deliberados

Não existem executor externo, ferramentas, capability registry, scheduler, actions simuladas, retry, paralelismo ou aprovação de Task. VS-006 mede progresso interno, não autorização para mudar o mundo.
