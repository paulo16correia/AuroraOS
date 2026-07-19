# VS-030 - World Model: relacoes em disputa

## Objetivo

Permitir que uma afirmacao explicita do utilizador conteste uma relacao atual
sem apagar o seu historico. A relacao passa a `DISPUTED` e deixa de ser usada
como facto em queries atuais ou temporais.

## Cenario vertical

```text
WorldAssertion(CURRENT, Paulo -> Aurora)
       |
       +-- "A informacao de que o Paulo trabalha no projeto Aurora esta errada."
                         |
                         v
WorldAssertion(DISPUTED, evidencia original + correcao)
                         |
                         v
World.query -> nao devolve a assertion como facto confirmado
```

## Regras obrigatorias

1. Uma disputa so nasce de uma declaracao explicita do utilizador.
2. A `WorldAssertion` e preservada, com a mensagem de contestacao adicionada a
   `evidence_refs`; nao e apagada nem transformada numa negacao global.
3. Assertions `DISPUTED` nao entram em queries atuais, historicas ou `as_of`.
4. Repetir a mesma contestacao e idempotente: estado, evidencia e versao nao
   mudam novamente.
5. O sistema responde por desconhecimento enquanto nao existir uma nova
   assertion confirmada; nao escolhe uma versao dos factos por popularidade.

## Casos limite

- Contestacao sem relation atual correspondente: nao criar estado negativo.
- Duas relations independentes: disputar uma nao afeta a outra.
- Resolucao de disputa e criacao de assertion substituta ficam fora deste
  slice; exigem evidencia e ciclo proprio.

## Criterios de aceitacao

1. A contestacao cria a transicao `CURRENT -> DISPUTED` auditavel.
2. O Event Bus emite `WorldAssertionDisputed`.
3. Queries atuais e `as_of` deixam de devolver o facto contestado.
4. Repetir a contestacao nao cria novas entidades nem muda a versao.
