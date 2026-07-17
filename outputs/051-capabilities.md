# Aurora Platform — RFC 051: Sistema de capacidades

**Estado:** Normativo · **Depende de:** RFC 06, 045, 050

## Objetivo

Modelar necessidades funcionais em abstrações de alto nível. A Mind decide a capacidade desejada — por exemplo, comunicar ou pesquisar — e o Kernel seleciona uma Application/ferramenta permitida. Assim a intenção não fica acoplada a Gmail, Discord, browser ou qualquer fornecedor concreto.

## Arquitetura

```text
Decision: “preciso comunicar”
             │
             ▼
Capability Resolver
  ├─ comunicação → Email | Discord | Telegram | WhatsApp
  └─ pesquisa     → Browser | índice documental | filesystem
             │
             ▼
Policy + Self + Resource Model → ToolCall concreto
```

## Estruturas de dados

```text
Capability
  id, domain: COMMUNICATION|RESEARCH|SCHEDULING|DEVELOPMENT|FILES|AUTOMATION
  intent_schema, effect_classes[], risk_class, required_permissions[]

CapabilityProvider
  id, capability_id, application_id, tool_ref, priority
  availability, cost_model, data_classes[], constraints[], health_ref

CapabilityRequest
  id, decision_ref, capability_id, intent_payload, target_constraints[]
  status: REQUESTED|RESOLVED|BLOCKED|EXECUTED|FAILED
```

## Interfaces

```text
Capabilities.resolve(request, self, policy, resources) -> CapabilityProvider
Capabilities.execute(resolved_request) -> ToolCall
Capabilities.explainResolution(request_id) -> ResolutionReport
```

## Regras obrigatórias

1. A Mind pede capacidades, não fornecedores, salvo quando o utilizador exigir explicitamente um destino/serviço.
2. Resolução considera permissões, destinatário, classificação, custo, disponibilidade e preferência explícita.
3. Um provider não pode oferecer efeitos fora do manifesto da capacidade.
4. Falha de provider permite alternativa apenas se preserva intenção, âmbito e política; caso contrário bloqueia/pede decisão.

## Casos limite e erro

- **Email indisponível, Discord disponível:** não substituir automaticamente se a intenção era enviar email a destinatário específico.
- **Utilizador prefere VS Code:** preferência influencia provider de desenvolvimento, mas não ultrapassa segurança/custo.
- **Nenhum provider:** `BLOCKED` com capacidade em falta, não uma chamada genérica a shell.

## Justificação

Capacidades dão portabilidade e evitam que o modelo associe cada objetivo a um fornecedor fixo. O Kernel decide a melhor concretização autorizada.

## Expansões futuras

Composição de capacidades, negociação de qualidade, marketplace certificado e fallback declarativo.

