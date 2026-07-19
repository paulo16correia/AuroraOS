# Catálogo canónico de Vertical Slices

Este ficheiro é a fonte de verdade para a numeração dos Vertical Slices (VS).
Uma feature só pode usar um número após ser registada aqui. Um VS é composto por
um execution trace, código no `aurora-kernel`, testes automatizados e, quando
altera uma decisão arquitetural, um ADR.

## Regra de numeração

- Um número identifica um único comportamento de ponta a ponta e nunca é reutilizado.
- Um ajuste corretivo usa o sufixo `.1` (por exemplo, `VS-007.1`).
- Se uma proposta for abandonada, o número fica `RESERVADO` ou `SUBSTITUÍDO`; não é renumerado.
- Este catálogo prevalece sobre mensagens de planeamento, notas e nomes de branches.

## Linha de implementação

| VS | Comportamento canónico | Estado | Evidência documental |
| --- | --- | --- | --- |
| 000–017 | Núcleo, Entity, Mind, Tasks, capabilities, approval, Event Engine | Implementado | `VS-000` a `VS-017` |
| 018 | Scheduler idempotente de `WAIT_FOR_TIME` | Implementado | `VS-018-persistent-scheduler.md` |
| 019 | Provider Google Calendar e OAuth local | Implementado | `VS-019-google-calendar.md` |
| 019.1 | Calendar no pipeline de capability | Implementado | `VS-019.1-calendar-capability-integration.md` |
| 020 | Consulta Google Calendar Free/Busy | Implementado | `VS-020-calendar-freebusy.md` |
| 021 | Orquestração Calendar + Email com aprovações independentes | Implementado | `VS-021-multi-capability-orchestration.md` |
| 022 | Resolução explícita de entidades para destinatários | Implementado | `VS-022-entity-resolution.md` |
| 023 | Compensação conservadora de ramos falhados | Implementado | `VS-023-workflow-compensation.md` |
| 024 | Workflows recorrentes | Implementado | `VS-024-recurring-workflows.md` |
| 025 | Ciclo de vida de eventos Google Calendar | Implementado | `VS-025-calendar-event-lifecycle.md` |
| 026 | World Model: relações explícitas com proveniência | Implementado | `VS-026-world-model-foundation.md` |
| 027 | World Model temporal: transições para histórico | Implementado | `VS-027-temporal-world-assertions.md` |

## Histórico de colisões resolvidas

Durante o planeamento, `VS-021` foi temporariamente usado para Calendar
Update/Delete e depois para orquestração multi-capability. O significado
canónico é **orquestração multi-capability**. A alteração e remoção de eventos
de Calendar são uma expansão futura e receberão outro número.

## Próximo slice

O catálogo aguarda a priorização do próximo comportamento. Qualquer novo VS
tem de ser registado aqui antes de receber implementação no Kernel.
