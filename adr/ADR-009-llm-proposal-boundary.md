# ADR-009 — Fronteira de proposta linguística

**Estado:** Aceite  
**Data:** 2026-07-18  
**Decisão:** VS-009 — LLM Adapter

## Contexto

A Aurora possui continuidade, memória, objetivos, planos, tarefas, avaliações de capability e executor inerte. Falta um motor de linguagem que possa ser trocado entre fornecedores sem transformar o fornecedor na fonte de verdade ou permitir que este altere o estado cognitivo.

## Decisão

Introduzir os contratos imutáveis `LanguageModelRequest` e `LanguageModelProposal`, a porta `LanguageModelProvider` e a fronteira `KernelProposalValidator`.

```text
Kernel constrói Request → Provider devolve Proposal → Kernel valida → Kernel cria Thought
```

O Kernel é o único escritor do repositório e o único publicador de eventos. Providers são adaptadores puros: não recebem dependências de persistência, executores ou capacidades. A implementação padrão é determinística. `OpenAIResponsesProvider` existe apenas como adaptador opt-in e obtém a chave exclusivamente de `OPENAI_API_KEY` no processo.

## Consequências

### Positivas

- O fornecedor é substituível sem mudar a ontologia ou a persistência da Aurora.
- Cada proposta tem identidade e correlação auditáveis.
- Falhas de fornecedor degradam para resposta segura, não para estado parcialmente alterado.
- Replay e testes permanecem determinísticos por defeito.

### Custos

- Há uma etapa adicional e objetos auditáveis antes do `Thought`.
- A aceitação de contexto por fornecedores externos requer governação explícita.
- Providers que devolvam texto livre podem ser rejeitados ou limitados pelo Kernel.

## Alternativas rejeitadas

1. **Chamar SDKs de LLM diretamente no Kernel** — acopla a lógica cognitiva ao fornecedor e torna auditoria e testes frágeis.
2. **Permitir que a LLM escreva Memory/Goal/Plan** — viola ownership de estado, idempotência e a Constituição.
3. **Usar apenas um prompt persistido como personalidade** — mistura identidade, estado e comportamento num artefacto não governado.

## Migração e compatibilidade

`DeterministicReasoningProvider` é removido. Os fluxos existentes passam pelo provider determinístico sob a nova interface. O comportamento de VS-000–VS-008 permanece coberto pelos testes de regressão.
