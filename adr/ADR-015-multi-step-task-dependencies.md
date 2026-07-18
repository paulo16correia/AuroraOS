# ADR-015 — Dependências explícitas entre tarefas de um plano

**Estado:** aceite  
**Data:** 2026-07-18

## Decisão

Cada `Task` passa a declarar a sua posição (`sequence`) e as tarefas que devem terminar primeiro (`dependency_refs`). O coordenador de tasks é o único componente que interpreta estas dependências; o Planner propõe passos e os executores continuam isolados.

## Justificação

Um `Plan` com passos sem relações é apenas uma lista. Dependências persistentes permitem retomar trabalho, auditar o porquê de uma task estar bloqueada e, mais tarde, introduzir paralelismo sem alterar o modelo fundamental.

## Consequências

- O primeiro plano operacional passa a ter análise e recomendação como tarefas separadas.
- A compatibilidade com tarefas já persistidas é mantida por valores default.
- O VS-016 poderá adicionar scheduling ou estados de espera sem reestruturar o contrato `Task`.
