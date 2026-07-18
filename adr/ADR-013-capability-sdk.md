# ADR-013 — SDK estável de capabilities

**Estado:** Aceite  
**Data:** 2026-07-18  
**Decisão:** VS-013 — Capability SDK

## Contexto

VS-012 provou uma capability real, mas o Dispatcher ainda tinha conhecimento especial do executor de email. Repetir esse padrão para cada integração faria crescer o Kernel indefinidamente.

## Decisão

Definir `CapabilityExecutor` com `prepare`, `execute` e `recover`, e introduzir `CapabilityExecutorRegistry`. Migrar `EmailSendExecutor` para esse contrato. O registry é construído na composição do runtime, não pelo ciclo cognitivo.

## Consequências

- Adicionar uma capability passa a ser registar um executor compatível.
- A recuperação torna-se uma responsabilidade explícita de cada integração.
- O Kernel mantém ownership de estado, eventos, aprovação e idempotência.

## Alternativas rejeitadas

1. **Um executor genérico com condicionais por capability** — concentra complexidade e acopla o Kernel a integrações.
2. **Plugins com acesso direto ao repositório** — viola ownership e auditabilidade.
3. **Executores sem recuperação definida** — torna falhas e reinícios inseguros.
