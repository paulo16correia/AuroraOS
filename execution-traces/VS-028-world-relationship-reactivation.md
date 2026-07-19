# VS-028 - World Model temporal: reativacao de relacoes

## Objetivo

Permitir reativar uma relacao historica sem alterar ou apagar o seu intervalo
anterior. O caso vertical e: Paulo trabalhou no projeto Aurora, deixou-o e mais
tarde voltou ao mesmo projeto.

## Fluxo

```text
WorldAssertion(HISTORICAL, valid_from=A, valid_to=B)
       |
       +-- "O Paulo voltou ao projeto Aurora."
                         |
                         v
WorldAssertion(CURRENT, valid_from=C, valid_to=null)
                         |
                         v
Query atual -> assertion C
Query historica -> assertion A-B
```

## Regras obrigatorias

1. Reativar cria uma nova `WorldAssertion`; nunca muda a assertion historica
   de volta para `CURRENT`.
2. A nova assertion tem a propria evidencia, `valid_from` e identificador
   estavel derivado da mensagem de reativacao.
3. So pode haver uma relacao `CURRENT` por combinacao de sujeito, predicado e
   objeto. Repetir a mesma reativacao devolve a relacao atual, sem duplicar.
4. Uma reativacao sem relacao historica anterior nao cria estado por inferencia.
5. O encerramento posterior localiza a relacao atual por identidade logica,
   nao por um identificador de primeira geracao.

## Criterios de aceitacao

1. Depois de encerrar e reativar, existem duas assertions: uma `HISTORICAL` e
   uma `CURRENT`.
2. A query presente devolve a assertion nova; a query passada conserva a
   assertion anterior.
3. Repetir a frase de reativacao nao cria uma terceira assertion.
4. O trace e o Event Bus registam `WorldAssertionReactivated`.
