# Aurora OS — Execution Trace Specification: VS-009 LLM Adapter

**Estado:** Autorizado por ADR-009  
**Caso de utilização:** mensagem local → contexto imutável → proposta linguística → validação pelo Kernel → resposta auditável

## Objetivo

Introduzir um motor de linguagem substituível sem transferir para ele a posse da Mind. O VS-009 prova que uma resposta pode ser proposta por um provider, incluindo OpenAI quando explicitamente configurado, mas que apenas o Kernel pode aceitar a proposta, criar `Thought`, decidir, persistir ou alterar o estado da entidade.

## Fluxo normativo

```text
Input → Signal → Context
                 │
                 ├─ Mind state (leitura): Identity, SelfModel, Memory, Goal, Plan,
                 │                         CapabilityRequest e CapabilityResult
                 ▼
         LanguageModelRequest (imutável, persistido pelo Kernel)
                 ▼
         LanguageModelProvider.propose(request)
                 ▼
         LanguageModelProposal (não confiada, sem acesso a persistência)
                 ▼
         KernelProposalValidator
                 ├─ aceite → Thought → Decision → Response
                 └─ rejeitada → Thought seguro → Decision → Response seguro
                 ▼
         Observation → Reflection → Goal Evaluation → Memory
```

O provider não recebe `Repository`, `EventBus`, `Executor`, `CapabilityRegistry` nem referência mutável para qualquer objeto da Mind.

## Contratos mínimos

```text
LanguageModelProvider
  provider_id: str
  model_id: str
  propose(LanguageModelRequest) -> LanguageModelProposal

LanguageModelRequest
  request_id, entity_id, trace_id, session_id, message_id, context_id
  provider_id, model_id, locale
  system_instruction, user_input
  identity_summary, memory_facts, goal_summaries, plan_summaries, capability_summaries
  created_at

LanguageModelProposal
  proposal_id, request_ref, entity_id, trace_id, session_id
  provider_id, model_id, text, intent, confidence, reason_summary
  status: PROPOSED|REJECTED
  created_at
```

Ambos os contratos são imutáveis. `LanguageModelProposal` é uma declaração não confiada: nunca é um `Thought`, uma `Decision`, uma `Action`, uma memória ou uma ordem de execução.

## Implementações VS-009

| Provider | Uso | Rede | Efeito na Mind |
| --- | --- | --- | --- |
| `deterministic-rules-v1` | testes, replay e operação por defeito | não | nenhum |
| `openai-responses-v1` | opt-in por configuração explícita | apenas API Responses | nenhum |

O provider OpenAI requer `OPENAI_API_KEY` no processo e um modelo explícito no comando. A chave não é persistida, registada ou incluída no contexto. Sem chave, erro de rede ou output vazio, o provider devolve `REJECTED`; o Kernel cria uma resposta segura e mantém o estado inalterado.

## Regras obrigatórias

1. A LLM nunca escreve no repositório e nunca publica eventos.
2. A LLM nunca cria `Thought`, `Decision`, `Response`, `Memory`, `Goal`, `Plan`, `Task`, `CapabilityRequest` ou `CapabilityResult`.
3. O `KernelProposalValidator` valida correlação (`entity_id`, `trace_id`, `session_id` e `request_ref`), estado, texto, intenção permitida e confiança antes de criar um `Thought`.
4. A proposta não pode executar uma capability nem transformar uma capability indisponível numa capability disponível.
5. Falha de provider é um resultado auditável, não uma exceção silenciosa ou uma alteração de estado.
6. O provider por defeito é determinístico e sem rede, preservando replay reproduzível.
7. Dados confidenciais ou segredos não pertencem ao `LanguageModelRequest`. A expansão de classificação e consentimento exige ADR própria.

## Eventos e trace

| Momento | Evento | Trace |
| --- | --- | --- |
| Kernel materializa o contexto externo | `LanguageModelRequestCreated` | `LLM_REQUEST(CREATED)` |
| Provider devolve proposta | `LanguageModelProposalReceived` | `LLM_PROPOSAL(PROPOSED|REJECTED)` |
| Kernel admite ou rejeita | `LanguageModelProposalValidated` | `LLM_VALIDATION(ACCEPTED|REJECTED)` |
| Kernel cria o pensamento | `ThoughtCreated` | `THOUGHT` |

## Casos limite e erro

| Condição | Comportamento obrigatório |
| --- | --- |
| `OPENAI_API_KEY` ausente | proposta `REJECTED`, resposta segura, sem alteração de estado |
| timeout, erro HTTP ou erro de rede | proposta `REJECTED`, razão estruturada sem segredo |
| output vazio | proposta `REJECTED` |
| correlação de Entity/trace/sessão inválida | validação falha; o ciclo não cria `Thought` |
| intenção fora da allow-list | proposta rejeitada pelo Kernel |
| replay | usa o provider determinístico e continua sem rede |

## Critérios de aceitação

1. Um ciclo normal emite `LLM_REQUEST`, `LLM_PROPOSAL` e `LLM_VALIDATION` antes de `THOUGHT`.
2. `LanguageModelRequest` e `LanguageModelProposal` sobrevivem para auditoria, mas não são parte editável da Mind.
3. Sem chave OpenAI, a resposta é segura, o trace é `REJECTED` e Goals/Memórias/Capabilities não mudam por causa do provider.
4. VS-000–VS-008 continuam verdes.
5. Não existe chamada direta a fornecedor espalhada fora de `OpenAIResponsesProvider`.

## Limites deliberados

VS-009 não adiciona ferramentas, autonomia externa, aprovação, plugins, scheduler, aprendizagem de modelo, streaming, saída estruturada para planos ou acesso a segredos. O modelo é um componente de proposta linguística; a governação da Mind continua no Kernel.
