# VS-026 - World Model: relacoes explicitas com proveniencia

## Objetivo

Materializar o primeiro comportamento do RFC 041: a Aurora deixa de guardar
apenas texto sobre uma conversa e passa a guardar uma afirmacao estruturada
sobre uma relacao do mundo. O slice cobre uma afirmacao explicita de trabalho
em projeto e uma consulta posterior, mesmo depois de reiniciar a Entity.

## Cenario vertical

```text
Utilizador: "O Paulo trabalha no projeto Aurora."
        |
        v
Perception -> WorldAssertion(CURRENT, USER_ASSERTION)
        |
        v
Persistencia + Event + Trace
        |
        v
Shutdown / restore
        |
        v
Utilizador: "Em que projeto trabalha o Paulo?"
        |
        v
World.query -> contexto linguistico read-only -> resposta
```

## Contrato

`WorldAssertion` representa uma relacao evidenciada, nao uma frase:

```text
assertion_id, entity_id
subject_ref, subject_label
predicate
object_ref, object_label
evidence_refs[], confidence, provenance
observed_at, asserted_at, valid_from, valid_to
status: CURRENT|HISTORICAL|DISPUTED|RETRACTED
version
```

No VS-026, o unico predicado materializado e `WORKS_ON_PROJECT`.
`subject_ref` e `object_ref` sao identificadores locais canonicos; os labels
ficam separados para tornar a resposta legivel sem confundir texto com
identidade interna.

## Regras obrigatorias

1. So uma afirmacao explicita do utilizador pode criar uma `WorldAssertion`.
   O LLM nao cria nem altera World Assertions.
2. Toda a assertion conserva a mensagem de origem em `evidence_refs` e a
   proveniencia `USER_ASSERTION`.
3. Uma query sem assertion correspondente responde como desconhecida; ausencia
   de dados nao prova ausencia da relacao.
4. O modelo so recebe factos recuperados pelo Kernel como contexto read-only.
5. A assertion pertence a uma Entity e sobrevive a shutdown/restauro.
6. Correcao, fusao de entidades, contradicoes e inferencia ficam fora deste
   slice; devem receber ciclos e ADRs proprios.

## Casos de erro

- Frase incompleta ou ambigua: nao criar assertion.
- Repeticao da mesma declaracao: atualizar a assertion existente de forma
  idempotente, sem criar uma segunda relacao equivalente.
- Query de pessoa sem correspondencia: responder que nao existe evidencia
  registada, sem inventar um projeto.

## Criterios de aceitacao

1. A afirmacao cria `WorldAssertion(CURRENT)` e emite `WorldAssertionCreated`.
2. O trace tem estagios `WORLD_MODEL` para escrita e recuperacao.
3. A resposta posterior usa apenas a assertion recuperada.
4. Shutdown e restore preservam a referencia no `MindState`.
5. A suite de regressoes permanece verde.
