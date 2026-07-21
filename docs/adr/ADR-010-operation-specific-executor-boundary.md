# ADR-010 — Fronteira de executores específicos por operação

**Estado:** Aceite  
**Data:** 2026-07-18  
**Decisão:** VS-010 — Real Executor Boundary

## Contexto

VS-008 demonstrou a passagem de `CapabilityRequest` para `CapabilityResult` através de um `NOOP_EXECUTOR`. Essa prova era segura, mas não exprimia que cada capacidade externa exigirá lógica própria, validação específica e uma fronteira auditável antes de ser executada.

## Decisão

Substituir `NOOP_EXECUTOR` por `ExecutorRegistry` e `CapabilityExecutor.prepare`. O primeiro executor registado é `EmailSendExecutor`; nesta versão só prepara `EMAIL_SEND` e devolve `ExecutionPreparation`. Não possui nem pode aceder a transporte SMTP, HTTP, credenciais ou ferramentas do sistema.

O `ExecutorDispatcher` persiste a preparação e produz um `CapabilityResult` que declara `BLOCKED` ou `AWAITING_APPROVAL`. A execução externa não faz parte do contrato VS-010.

## Consequências

- Cada capability poderá ter uma implementação isolada e testável.
- O Kernel continua a ser dono de persistência, eventos, correlação e idempotência.
- A prova de bloqueio torna-se mais precisa do que `NOT_IMPLEMENTED`.
- VS-011 tem uma preparação concreta à qual pode anexar aprovação.

## Alternativas rejeitadas

1. **Ativar SMTP já em VS-010** — introduziria efeito externo antes do limite de aprovação.
2. **Manter um executor genérico com condicionais** — espalharia detalhes de operações e dificultaria isolamento.
3. **Permitir que a LLM escolha o executor** — violaria o controlo do Kernel e do registry.

## Migração

`NOOP_EXECUTOR` e `NOT_IMPLEMENTED` deixam de ser o resultado normal de EMAIL_SEND. Pedidos indisponíveis passam a gerar `ExecutionPreparation(BLOCKED)` e `CapabilityResult(BLOCKED)`. A migração não ativa capabilities nem exige dados novos.
