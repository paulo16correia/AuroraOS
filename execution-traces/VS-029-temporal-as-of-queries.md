# VS-029 - World Model temporal: consultas as-of

## Objetivo

Consultar uma relacao do World Model num instante explicito. A pergunta nao
usa a data de criacao do objeto: avalia o intervalo `[valid_from, valid_to)`
que a propria assertion declara.

## Cenario vertical

```text
Assertion: Paulo --WORKS_ON_PROJECT--> Aurora
valid_from = 2026-07-17T10:00:00Z
valid_to   = 2026-07-18T10:00:00Z

"Em que projeto trabalhava o Paulo em 2026-07-17T12:00:00+00:00?"
        |
        v
World.query(as_of=2026-07-17T12:00:00Z)
        |
        v
Aurora
```

## Regras obrigatorias

1. A consulta exige data/hora ISO 8601 com offset ou `Z`.
2. Uma assertion e valida se `valid_from <= as_of` e `as_of < valid_to`; se
   `valid_to` for nulo, a relacao continua valida.
3. O Kernel recupera as assertions; o LLM recebe apenas os factos devolvidos
   e nao calcula intervalos nem completa lacunas.
4. Nenhuma assertion valida no instante pedido devolve desconhecimento, nunca
   uma suposicao sobre presente ou passado.
5. O trace regista `AS_OF_RETRIEVED`, o instante consultado e as referencias
   usadas na resposta.

## Casos limite

- Instante igual a `valid_to`: nao pertence ao intervalo encerrado.
- Data sem timezone ou invalida: nao executar query temporal.
- Mais de uma relation valida: devolver todas como resultados, sem escolher
  arbitrariamente uma.

## Criterios de aceitacao

1. Uma query no meio de um intervalo historico devolve a assertion certa.
2. Uma query depois de `valid_to` nao usa essa assertion.
3. Uma query no intervalo da reativacao devolve a assertion atual.
4. Repetir a query nao altera estado persistido.
