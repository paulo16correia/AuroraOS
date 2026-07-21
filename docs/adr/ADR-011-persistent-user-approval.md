# ADR-011 — Aprovação explícita e persistente do utilizador

**Estado:** Aceite  
**Data:** 2026-07-18  
**Decisão:** VS-011 — Persistent User Approval

## Contexto

VS-010 consegue preparar operações por capability, mas uma preparação não é autorização. Sem um objeto persistente de aprovação, um reinício poderia perder o estado de espera, repetir o pedido ou permitir que texto de conversa fosse interpretado fora de contexto.

## Decisão

Adicionar `ApprovalRequest` e `Approval`. O Kernel cria a request apenas depois de uma preparação pendente, e resolve a confirmação do utilizador apenas quando há uma única request PENDING para a Entity. A aprovação é persistida com utilizador, correlação e instante.

EMAIL_SEND passa a estar disponível apenas para **preparação e aprovação**. Não possui transporte externo; `Approval(APPROVED)` não aciona execução.

## Consequências

- A autorização sobrevive a shutdown/restauro e é auditável.
- VS-012 pode exigir uma Approval válida sem redesenhar o ciclo cognitivo.
- As capabilities externas continuam incapazes de produzir efeitos nesta versão.

## Alternativas rejeitadas

1. **Guardar "sim" como mensagem de conversa** — não contém ligação segura à operação nem controlo de ciclo de vida.
2. **Executar logo após aprovação** — mistura autorização com efeito externo antes do executor real estar implementado.
3. **Deixar a LLM decidir o que foi aprovado** — viola ownership de estado e permite ambiguidade.
