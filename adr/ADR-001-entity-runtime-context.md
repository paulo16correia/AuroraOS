# ADR-001 — Entity Runtime Context

**Estado:** ACEITE  
**Data:** 2026-07-17  
**RFCs afetadas:** 020, 039, 040, 043, 050; Execution Traces VS-000 e VS-001

## Contexto

VS-000 demonstrou um ciclo completo e persistência, mas o trace estava ancorado em `session_id`. Uma sessão é efémera; não pode ser o proprietário da Mind, memória, história ou estado de uma entidade persistente. Esta lacuna bloqueia a prova de continuidade após shutdown/restart.

## Decisão

Introduzir `Entity` e `EntityRuntimeContext` como contratos canónicos:

```text
Entity
 ├─ possui Genome, Mind State, Lifecycle e Life History
 └─ contém Sessions
      └─ contém Traces/Cycles
```

`entity_id` é obrigatório em todos os contratos de domínio do Kernel, eventos, registos de trace, auditoria, snapshots e comandos. O Kernel cria/restaura o contexto de runtime antes de processar um Signal. A Mind não escolhe nem altera o proprietário da entidade.

## Consequências

- A execução passa a exigir/assumir uma entidade explícita (`entity_001` apenas como valor de demonstração da CLI).
- A persistência deve manter Entity, estado do ciclo de vida e Mind State independentemente de sessões/processos.
- VS-001 passa a ser o **Resurrection Test**, antes de qualquer capability externa.

## Migração

Bases SQLite existentes recebem colunas de `entity_id` em eventos e traces, mantendo dados antigos com valor `legacy_unscoped` apenas para leitura. Novas escritas exigem `entity_id` válido. A API/CLI adiciona `--entity-id` e `--genome-id`.

## Alternativas rejeitadas

- Usar `session_id` como identidade: perde continuidade e confunde presença com existência.
- Guardar entidade apenas em Memory: não fornece ciclo de vida, dono de estado ou restauro determinístico.

