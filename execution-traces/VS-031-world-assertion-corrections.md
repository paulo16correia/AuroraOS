# VS-031 - World Model: correcao explicita de relacoes disputadas

## Objetivo

Permitir que o utilizador substitua uma relacao `DISPUTED` por uma nova relacao
evidenciada, sem apagar, reabrir ou alterar a assertion contestada. Este slice
trata apenas `WORKS_ON_PROJECT`, quando a mensagem declara explicitamente o
facto corrigido.

## Cenario vertical

```text
WorldAssertion(DISPUTED, Paulo -> Aurora)
       |
       +-- "A informacao correta e que o Paulo trabalha no projeto Nexus."
                         |
                         v
WorldAssertion(DISPUTED, Paulo -> Aurora)  <- evidencia preservada
WorldAssertion(CURRENT,  Paulo -> Nexus)   <- nova evidencia + supersedes ref
                         |
                         v
World.query -> "Paulo trabalha no projeto Nexus."
```

## Contrato de entrada

```text
A informacao correta e que o <pessoa> trabalha no projeto <projeto>.
```

A frase tem de ser uma afirmacao explicita do utilizador. Nem uma LLM, nem uma
inferencia, nem uma mensagem ambigua podem criar a assertion substituta.

## Regras obrigatorias

1. A correcao e elegivel apenas se existir exatamente uma assertion `DISPUTED`
   para a mesma pessoa e predicado.
2. A assertion disputada nunca muda de estado, versao, validade ou evidencia.
3. A nova assertion tem `status = CURRENT`, evidencia da mensagem de correcao
   e `supersedes_assertion_ref` a apontar para a assertion disputada.
4. Se ja existir uma assertion `CURRENT` para a mesma pessoa e projeto, a
   operacao e idempotente: nao cria uma terceira assertion.
5. Uma assertion atual para outro projeto nao e encerrada por implicacao;
   exige uma transicao explicita futura.
6. Zero ou multiplas assertions disputadas elegiveis resultam em nenhuma
   escrita e num resultado auditavel no trace.
7. O Event Bus emite `WorldAssertionCorrected` apenas quando cria a nova
   assertion.

## Fluxo e auditoria

```text
Input -> Perception -> WorldModel.correction
      -> validar uma assertion disputada
      -> criar WorldAssertion(CURRENT)
      -> persistir -> evento -> trace -> query futura
```

`WORLD_MODEL` regista `CORRECTED`, `CURRENT_ALREADY`,
`CORRECTION_NOT_ELIGIBLE` ou `CORRECTION_CONFLICT_WITH_CURRENT`. O resultado
nao depende do raciocinio privado do modelo de linguagem.

## Casos limite

- **Sem disputa previa:** a forma de correcao nao cria uma relacao.
- **Duas disputas para a mesma pessoa:** o resultado e ambiguo; nao ha escrita.
- **Repeticao da correcao:** a relacao `CURRENT` existente e reutilizada.
- **Restauro:** ambas as assertions pertencem ao MindState e sobrevivem ao
  reinicio.

## Criterios de aceitacao

1. Uma correcao valida deixa a assertion original `DISPUTED`.
2. Cria uma assertion `CURRENT` distinta, com `supersedes_assertion_ref`.
3. A query atual devolve somente o novo projeto.
4. A repeticao nao cria duplicados.
5. Evento, trace e persistencia permitem reconstruir a decisao.
