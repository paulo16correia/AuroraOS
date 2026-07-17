# ADR-007 — Alinhamento do pipeline de CapabilityRequest

**Estado:** ACEITE  
**Data:** 2026-07-18  
**RFCs afetadas:** RFC 021, RFC 022, RFC 040, RFC 051, ADR-006

## Contexto

VS-007 introduziu `CapabilityRequest`, mas uma Entity que já tivesse uma Task `COMPLETED` podia carregar essa Task apenas para auditoria. Nessa situação, o pedido de email deixava de chegar ao Capability Registry e o trace registava incorretamente `CAPABILITY(NOT_REQUIRED)`.

## Decisão

VS-007.1 separa duas coleções de Tasks no Kernel:

- `completed_tasks_this_cycle`: só alimenta `GoalEvaluation`, impedindo progresso duplicado;
- `capability_source_tasks`: pode incluir uma Task concluída e persistida, para fundamentar uma `CapabilityRequest` do pedido atual.

Para qualquer intenção de email, o Kernel DEVE avaliar o catálogo e criar ou carregar a request correspondente antes de criar Thought e Decision. `NOT_REQUIRED` só é permitido quando a intenção não requer capability.

## Consequências

- `EMAIL_SEND` passa sempre por `CAPABILITY_REQUEST` e `CAPABILITY_ASSESSMENT(UNAVAILABLE)` quando o utilizador o pede.
- Uma Task histórica não volta a incrementar Goal progress.
- A resposta e Decision recebem sempre uma request validada pelo Kernel para pedidos de email.

## Migração e reversão

Sem migração de dados. Requests existentes são carregadas pela mesma chave idempotente; requests ausentes podem ser criadas a partir da Task persistida. A reversão preserva auditoria e apenas remove esta recuperação de fonte.
