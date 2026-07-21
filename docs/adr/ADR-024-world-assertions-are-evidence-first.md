# ADR-024 - World Assertions sao orientadas a evidencia

**Estado:** Aceite  
**Data:** 2026-07-19

## Contexto

Memorias textuais sao insuficientes para representar a realidade operacional:
nao permitem perguntar de forma segura por relacoes, explicar a origem de um
facto ou distinguir uma observacao de uma inferencia.

## Decisao

Introduzir `WorldAssertion` como objeto imutavel e persistido. Cada assertion
guarda sujeito, predicado, objeto, validade, proveniencia e referencias de
evidencia. O primeiro predicado e `WORKS_ON_PROJECT`, criado unicamente por
uma mensagem assertiva explicita do utilizador.

## Consequencias

- Queries podem ser respondidas a partir de relacoes estruturadas e auditaveis.
- O LLM recebe apenas factos recuperados e nao tem autoridade de escrita.
- A evolucao para grafo, temporalidade rica, conflitos e resolucao de entidade
  conserva o mesmo contrato de evidencia.

## Alternativas rejeitadas

- **Usar apenas pesquisa vetorial de mensagens:** nao fornece predicado,
  validade ou proveniencia estruturada.
- **Permitir que o LLM extraia e persista qualquer facto:** mistura proposta
  nao confiavel com estado canonicamente auditavel.
