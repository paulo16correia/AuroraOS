# ADR-002 — Integridade de identidade e continuidade pessoal

**Estado:** ACEITE  
**Data:** 2026-07-17  
**RFCs afetadas:** RFC 03, RFC 027, RFC 040, LAW-008

## Contexto

VS-002 tornou persistente a identidade mínima de uma Entity. A continuação correta é recuperar memórias pessoais verificáveis, sem permitir que um provider de raciocínio invente identidade, biografia ou preferências.

## Decisão

Adicionar LAW-008 e o VS-003. A fonte exclusiva para afirmações sobre a identidade da Aurora é `Identity`/`SelfModel` persistidos e validados pelo Kernel. VS-003 introduz recuperação determinística de memórias `ACTIVE`, delimitada por `entity_id`, com proveniência direta de mensagem de utilizador e registo obrigatório no trace.

## Consequências

- A interface do provider de raciocínio recebe apenas o Self e as memórias selecionadas pelo Kernel; não pode escolher memória fora do serviço de recuperação.
- A primeira implementação usa correspondência estruturada e regras determinísticas. Não introduz embeddings, vector store, conhecimento global, crenças ou preferências inferidas.
- Alterações futuras ao Self ou a uma estratégia de recuperação que permita inferência nova exigem ADR e testes de não-alucinação.

## Migração e reversão

Memórias VS-000 existentes, sem campos estruturados, mantêm-se recuperáveis apenas como episódios genéricos. Novas memórias VS-003 incluem sujeito, predicado, objeto, confiança e proveniência. Revogar esta decisão remove a recuperação do contexto, sem apagar auditoria ou memórias persistidas.
