# Aurora OS — RFC 05: Objetivos, planeamento e execução de tarefas

**Estado:** Normativo · **Depende de:** RFC 02–03

## Objetivo

Converter resultados desejados em trabalho observável. O planeador propõe uma sequência; o gestor de ferramentas e a política autorizam cada efeito real.

## Arquitetura e fluxo

```text
Pedido/objetivo → clarificar resultado e restrições → plano candidato
                                                │
                                                ▼
                  dependências ← tarefas ← critérios de aceitação
                                                │
                           aprovação do plano? ─┤
                                                ▼
                       agendar tarefa pronta → executar/observar
                                                │
                                                ▼
                                   atualizar estado + reflexão
```

## Estruturas de dados

```text
Goal
  id, title, outcome, owner_id, priority: 1..5
  status: DRAFT|ACTIVE|BLOCKED|PAUSED|COMPLETED|CANCELLED|FAILED
  constraints_json, success_criteria[], deadline_at, budget_json
  created_from_ref, approval_policy_id

Task
  id, goal_id, title, description, kind: RESEARCH|DECISION|TOOL|HUMAN|CHECK
  status: DRAFT|READY|RUNNING|WAITING_INPUT|WAITING_APPROVAL|
          SUCCEEDED|FAILED|SKIPPED|CANCELLED
  dependencies[], inputs_json, expected_output_schema
  risk: LOW|MEDIUM|HIGH|CRITICAL, assignee: AURORA|HUMAN
  retry_policy, idempotency_key, acceptance_tests[]

Plan
  id, goal_id, revision, rationale, assumptions[], task_ids[]
  status: PROPOSED|APPROVED|ACTIVE|SUPERSEDED|CLOSED
```

## Interfaces

```text
Planner.create(goal_request, context) -> Plan
Planner.replan(goal_id, trigger) -> PlanRevision
Scheduler.next_runnable() -> Task[]
TaskService.transition(task_id, target_state, evidence) -> Task
```

## Regras obrigatórias

1. Um objetivo DEVE declarar resultado e critério de sucesso; “trata disto” exige clarificação ou plano de descoberta.
2. Toda a transição de tarefa DEVE respeitar uma máquina de estados e anexar evidência.
3. `RUNNING` nunca pode coexistir com dependência não `SUCCEEDED` salvo regra explícita.
4. Repetições automáticas são permitidas apenas para tarefas idempotentes e dentro de orçamento.
5. O planeador NÃO DEVE esconder pressupostos; deve apresentá-los quando influenciam custo, risco ou direção.

## Casos limite e erro

- **Dependência falhada:** bloquear descendentes e oferecer replaneamento; não “marcar concluído” por inferência.
- **Objetivo incompatível com política:** manter `BLOCKED` com razão, sem decompor em contornos da regra.
- **Prazo ultrapassado:** notificar, pausar ou continuar conforme política, nunca alterar secretamente o prazo.
- **Saída inválida de tarefa:** falhar validação, guardar diagnóstico e não desbloquear dependências.

## Justificação

Objetivos e tarefas separados evitam confundir intenção duradoura com uma tentativa concreta de execução. Critérios de aceitação tornam progresso mensurável e auditável.

## Expansões futuras

Estimativa probabilística, calendários, negociação de prioridades, equipas multiagente aprovadas e otimização de recursos.

