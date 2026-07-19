# ADR-026 - Reativacao cria um novo intervalo temporal

**Estado:** Aceite  
**Data:** 2026-07-19

## Decisao

Uma reativacao cria uma nova `WorldAssertion(CURRENT)` com novo inicio de
validade. A assertion `HISTORICAL` anterior permanece imutavel.

## Justificacao

Uma relacao que termina e volta a existir representa dois intervalos factuais.
Reutilizar o mesmo objeto apagaria a descontinuidade e falsificaria consultas
sobre o estado do mundo em cada periodo.

## Alternativa rejeitada

- **Reabrir a assertion historica:** perde o intervalo em que a relacao nao
  esteve ativa e impede uma cronologia correta.
