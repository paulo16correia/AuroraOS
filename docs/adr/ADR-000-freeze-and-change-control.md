# ADR-000 — Freeze v1.0 e controlo de alterações

**Estado:** ACEITE  
**Data:** 2026-07-17  
**RFCs afetadas:** todas as RFCs v1.0

## Contexto

A arquitetura já contém Kernel, Mind, Applications, Constituição, Leis, ciclo cognitivo, estado persistente, Event Bus e governança. Continuar a adicionar conceitos aumentaria risco de sobreposição e adiaria validação empírica.

## Decisão

Adotar o Architecture Freeze v1.0. A baseline é implementada e testada por ordem definida; alterações estruturais passam por ADR. As RFCs 021–025 são a fonte normativa do ciclo cognitivo; a RFC 02 permanece como referência de orquestração de implementação, sem introduzir semântica concorrente.

## Consequências

- A implementação começa por contratos e infraestrutura do Kernel, não por conectores ou UX.
- Descobertas passam por review/ADR; não há alterações informais de arquitectura.
- A documentação de implementação, testes e standards torna-se a prioridade.

## Migração e reversão

Não há migração de dados. Revogar este ADR requer ADR posterior aceite e nova revisão completa da baseline.

