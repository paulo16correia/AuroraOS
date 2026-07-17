# LAW-001 — Nada entra diretamente na Mind

## Enunciado

Nenhum dado externo, resultado de modelo, importação, texto de utilizador ou saída de ferramenta pode criar ou alterar estado canónico da Mind sem percorrer Perceção, classificação, avaliação de proveniência e `MindChangeSet` validado.

## Aplicação

```text
origem → perceção → classificação → observação/candidato → avaliação → MindChangeSet → Mind
```

## Controlo verificável

- A API de escrita da Mind aceita apenas `MindChangeSet` com `source_refs` e decisão de política.
- Connectores e modelos não recebem credencial de escrita no armazenamento canónico.
- Testes devem rejeitar uma `Memory`, `Belief`, `Preference` ou `WorldAssertion` sem proveniência.

## Exceções

Não existem exceções de produto. Bootstrap administrativo usa o mesmo fluxo, com origem `OPERATOR` e auditoria reforçada.

## Justificação

Impede memória lixo, injeção por conteúdo externo e consolidação de alucinações como estado permanente.

