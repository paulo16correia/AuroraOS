# ADR-019 — Política própria para consultas Calendar Free/Busy

## Decisão

`CALENDAR_FREEBUSY` é uma capability de leitura separada de `CALENDAR_CREATE_EVENT`. A policy inicial é `NOT_REQUIRED` para aprovação porque o pedido direto do utilizador autoriza uma única consulta ao seu próprio calendário, sem produzir um efeito externo.

## Consequências

Esta decisão mantém a experiência conversacional natural para perguntas de disponibilidade, mas preserva a policy como fronteira de segurança: uma instalação pode alterar a policy persistida para `REQUIRED`, restringir o principal ou desativar a capability. Operações de escrita continuam sempre sujeitas a aprovação explícita.
