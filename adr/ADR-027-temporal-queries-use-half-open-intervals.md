# ADR-027 - Consultas temporais usam intervalos semiabertos

**Estado:** Aceite  
**Data:** 2026-07-19

## Decisao

O World Model representa validade como `[valid_from, valid_to)`. O inicio e
inclusivo; o fim, quando existe, e exclusivo.

## Justificacao

Esta convencao evita sobreposicao no instante de transicao entre duas
relacoes consecutivas. E permite que o Kernel determine a validade sem pedir
ao modelo linguistico para raciocinar sobre tempo.

## Alternativa rejeitada

- **Fim inclusivo:** cria ambiguidade quando uma relacao termina no mesmo
  instante em que outra comeca.
