# VS-016 — Workflow Engine Persistente

## Objetivo

Introduzir estados de workflow que sobrevivem ao reinício e representar um plano como um DAG simples: tarefas dependentes, uma ramificação paralela e uma espera explícita por aprovação.

## Fluxo implementado

```text
INTERNAL_ANALYSIS ──> PROVIDE_RECOMMENDATION
         |
         +────────────> PREPARE_AUDIT_CONTEXT
         |
         +────────────> WAIT_FOR_APPROVAL
                                  |
                                  v
                            APPROVAL recebida
                                  |
                                  v
                              COMPLETED
```

## Modelo `Task`

Além de `sequence` e `dependency_refs`, uma task pode agora declarar `activation_condition`, `wait_kind`, `wait_ref` e `wait_until`. Os estados reservados são `WAITING_FOR_EVENT`, `WAITING_FOR_APPROVAL` e `WAITING_FOR_TIME`.

## Regras

1. Uma task em espera nunca é executada pelo executor externo.
2. A aprovação é um evento que retoma apenas tasks `WAITING_FOR_APPROVAL` da entidade ativa.
3. O ramo paralelo possui a mesma dependência que o ramo principal, mas nenhuma dependência entre ambos.
4. Não existe polling nem despertar temporal neste VS; `WAIT_FOR_TIME` será acordado pelo Scheduler no VS-018.
5. Todas as transições produzem eventos e trace: `TaskWaiting` e `TaskResumed`.

## Casos limite

- Reinício durante a espera: a task mantém `WAITING_FOR_APPROVAL`.
- Confirmação repetida: a task concluída não volta a transitar.
- Política que bloqueia a execução: a espera é retomada pela decisão de aprovação, mas a capability continua bloqueada pela policy.

## Expansões

Condições declarativas, eventos externos tipados, timeouts, junções de ramos, paralelismo real e scheduler autónomo.
