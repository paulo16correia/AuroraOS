# ADR-006 — Framework de avaliação de capacidades sem execução

**Estado:** ACEITE  
**Data:** 2026-07-18  
**RFCs afetadas:** RFC 021, RFC 022, RFC 040, RFC 051, LAW-002, LAW-006

## Contexto

VS-006 consegue concluir uma Task interna, mas um pedido de email ainda só produz uma ausência genérica de efeito. A Entity deve poder representar explicitamente o que lhe falta sem obter autorização, ferramenta ou acesso ao mundo externo.

## Decisão

VS-007 introduz um `CapabilityRegistry` persistente e `CapabilityRequest` auditável. O registo inicial contém `MEMORY`, `PLANNING` e `INTERNAL_REASONING` ativos; `EMAIL_SEND`, `CALENDAR`, `FILESYSTEM` e `WEB_SEARCH` estão definidos mas desativados.

Uma Task `INTERNAL_ANALYSIS` que identifica um pedido de email cria `CapabilityRequest(EMAIL_SEND, UNAVAILABLE)`. O Kernel pode explicar essa indisponibilidade ao utilizador. A request não é uma Action, não é autorização e não chama provider algum.

## Consequências

- A Aurora distingue “não tenho capacidade” de “não executei ainda”.
- O trace inclui `CAPABILITY_ASSESSMENT`; os eventos incluem registo, pedido e avaliação de capacidade.
- O Decision mantém-se `RESPOND`; nunca passa a `TOOL_CALL` neste slice.
- O estado de capacidades e requests é preservado no snapshot para continuidade e auditoria.

## Migração e reversão

Entities existentes recebem o catálogo de compatibilidade no primeiro arranque VS-007. Desativar este slice impede novas avaliações, mas preserva catálogo e requests persistidos; não existe ferramenta ou autorização a revogar porque nenhuma foi criada.
