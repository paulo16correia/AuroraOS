# VS-027 - World Model temporal: transicoes para historico

## Objetivo

Implementar a primeira transicao temporal do World Model definido no RFC 042.
Uma relacao `WORKS_ON_PROJECT` atual pode ser encerrada por uma afirmacao
explicita do utilizador. A evidencia permanece; apenas a sua validade muda.

## Cenario vertical

```text
"O Paulo trabalha no projeto Aurora."
       |
       v
WorldAssertion(CURRENT, valid_to=null)
       |
       v
"O Paulo deixou o projeto Aurora."
       |
       v
WorldAssertion(HISTORICAL, valid_to=<instante da mensagem>)
       |
       v
"Em que projeto trabalhou o Paulo?"
       |
       v
World.query(as_of=historico) -> resposta com evidencia
```

## Regras obrigatorias

1. A transicao so pode ser iniciada por uma mensagem assertiva explicita do
   utilizador; LLMs e ferramentas nao encerram relacoes canonicas.
2. `valid_to` e definido uma unica vez e a assertion passa de `CURRENT` para
   `HISTORICAL`. O objeto nao e apagado.
3. A mensagem que encerra a relacao e adicionada a `evidence_refs`.
4. Uma segunda declaracao de encerramento e idempotente: nao altera novamente
   `valid_to`, nem cria duplicados.
5. Consultas atuais ignoram assertions historicas; consultas historicas usam
   exclusivamente assertions `HISTORICAL` recuperadas pelo Kernel.

## Casos limite

- Nenhuma relacao atual correspondente: nao criar estado negativo nem inferir
  que a pessoa nunca trabalhou no projeto.
- Duas relacoes atuais conflituantes: encerrar apenas a relacao explicitamente
  identificada; a conciliacao entre projetos fica para um VS futuro.
- Reativacao: fora do escopo. Uma nova declaracao de trabalho cria uma nova
  assertion com nova validade, sem reescrever a historica.

## Criterios de aceitacao

1. A relacao atual transita para `HISTORICAL` com `valid_to` persistido.
2. O trace e o Event Bus registam a transicao temporal.
3. A query historica responde a partir da assertion persistida.
4. Repetir a declaracao de encerramento mantem a mesma data de fim.
5. Restauro preserva a assertion historica e a sua validade.
