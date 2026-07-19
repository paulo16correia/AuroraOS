# ADR-029 - Correcoes criam assertions novas e preservam disputas

**Estado:** Aceite  
**Data:** 2026-07-19

## Contexto

O VS-030 permite contestar uma `WorldAssertion`, mas uma disputa apenas remove
confianca operacional; nao determina o facto correto. O sistema deve aceitar
uma correcao posterior sem tornar a evidencia antiga invisivel ou sobrescrita.

## Decisao

Uma correcao explicita do utilizador cria uma nova `WorldAssertion(CURRENT)`.
Esta conserva a evidencia da correcao e referencia a assertion `DISPUTED`
anterior em `supersedes_assertion_ref`. A assertion original permanece imutavel
e disputada.

A correcao exige uma unica assertion `DISPUTED` elegivel para o sujeito e
predicado. Ambiguidade, ausencia de disputa ou conflito com uma relacao atual
para outro objeto bloqueiam a escrita.

## Consequencias

- A auditoria mantem facto original, contestacao e facto substituto.
- Queries operacionais recuperam apenas a relacao `CURRENT` nova.
- O Kernel nao deduz negacoes, exclusividade de projetos ou hierarquia entre
  fontes.

## Alternativas rejeitadas

- **Mudar a assertion disputada para um novo facto atual:** perde proveniencia.
- **Resolver qualquer disputa com uma frase positiva:** aceita ambiguidade e
  permite que texto incidental reescreva historia.
