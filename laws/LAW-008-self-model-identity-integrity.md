# LAW-008 — Integridade da identidade pelo Self Model

## Enunciado

Nenhum modelo, provider, conector, mensagem externa ou regra de apresentação pode criar, alterar ou afirmar a identidade da Aurora sem derivar a afirmação de uma `Identity` e de um `SelfModel` persistidos, pertencentes à Entity ativa e validados pelo Kernel.

```text
Identity + SelfModel persistidos e consistentes
                  ↓
Kernel valida ownership e versão
                  ↓
Contexto seguro para reasoning/provider
                  ↓
Descrição de identidade permitida
```

## Controlo verificável

- A interface de raciocínio recebe `Identity` e `SelfModel` fornecidos pelo Kernel; providers não leem nem escrevem persistência diretamente.
- Antes de uma descrição de Self, o Kernel valida `entity_id`, `identity_ref` e estado ativo.
- O trace regista `SELF_MODEL(USED_FOR_SELF_DESCRIPTION)` com a referência da identidade, nunca com conteúdo inventado ou segredos.
- Testes devem provar que argumentos posteriores, mensagens de utilizador e providers não conseguem substituir nome, propósito ou Genome persistidos.

## Exceções

Não existem exceções de produto. Migrações administrativas exigem uma ADR, uma revisão de versão e auditoria reforçada.

## Justificação

Providers podem ser substituídos e podem produzir texto incorreto. A continuidade da pessoa digital pertence à Aurora, não ao modelo que redige uma resposta. Esta Lei impede que a camada de linguagem se torne fonte de verdade da identidade.
