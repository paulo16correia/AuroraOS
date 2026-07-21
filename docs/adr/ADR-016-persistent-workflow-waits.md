# ADR-016 — Esperas persistentes no Workflow Engine

**Estado:** aceite  
**Data:** 2026-07-18

## Decisão

Representar esperas como estado persistente de `Task`, e não como processos bloqueados na memória. Uma aprovação reabre o ciclo e transita a task correspondente para `COMPLETED`.

## Consequências

- O Kernel pode reiniciar sem perder onde o workflow parou.
- O Executor não tem responsabilidade sobre esperas nem dependências.
- O VS-018 poderá acordar `WAIT_FOR_TIME` sem alterar o contrato de task.
