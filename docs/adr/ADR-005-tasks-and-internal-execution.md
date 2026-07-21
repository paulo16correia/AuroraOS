# ADR-005 — Tasks e ciclo de execução interna controlada

**Estado:** ACEITE  
**Data:** 2026-07-17  
**RFCs afetadas:** RFC 05, RFC 021, RFC 022, RFC 040, LAW-003, LAW-006

## Contexto

VS-005 permite à Entity propor um Plan, mas não demonstra que o plano produz progresso verificável. Ligar diretamente um Plan a uma Action externa eliminaria a fronteira de autorização definida pela Constituição e pela LAW-006.

## Decisão

VS-006 introduz `Task` persistente entre `Plan` e `Observation`. A única Task inicial é `INTERNAL_ANALYSIS`: é criada pelo Kernel a partir de um Plan explícito, executa localmente e produz um resultado textual controlado. Não tem cliente de rede, sistema de ficheiros, ferramenta, scheduler ou provider de capacidade.

```text
Goal → Plan(PROPOSED) → Task(READY → RUNNING → COMPLETED) → Observation → Reflection → GoalEvaluation
```

Ao completar a Task, o Kernel cria `GoalEvaluation(PROGRESS_DETECTED, progress_delta=1)` com referências à Task, Observation e Reflection. A Task é incluída no `MindState` de shutdown, mesmo quando concluída, para preservar continuidade e auditoria.

## Consequências

- A execução existe apenas no domínio interno e é determinística.
- `TaskCreated`, `TaskStarted` e `TaskCompleted` tornam o ciclo auditável.
- Pedidos externos, incluindo enviar email, podem gerar análise interna; nunca geram `Action`, `ToolCall` ou `CapabilityRequest` neste slice.
- Uma Task já persistida é carregada, não recriada nem reexecutada durante restauro.

## Migração e reversão

Não há criação retroativa de Tasks. Desativar VS-006 impede novas execuções internas, mas preserva Tasks, snapshots e eventos existentes para replay e auditoria.
