# ADR-025 - O World Model preserva historia ao encerrar relacoes

**Estado:** Aceite  
**Data:** 2026-07-19

## Decisao

Quando uma relacao observada deixa de vigorar, a Aurora altera o seu estado
para `HISTORICAL` e preenche `valid_to`. Nao elimina a assertion, a evidencia
nem a proveniencia original.

## Justificacao

Apagar uma relacao mistura "ja nao e atual" com "nunca existiu". Preservar a
linha temporal permite explicar respostas e preparar futuras consultas `as_of`
sem inventar uma narrativa a partir de mensagens soltas.

## Alternativas rejeitadas

- **Apagar a relacao:** perde prova e torna impossivel explicar o passado.
- **Criar uma relacao negativa:** introduz uma semantica de negacao global que
  o primeiro World Model ainda nao consegue resolver com seguranca.
