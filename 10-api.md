# Aurora OS — RFC 10: API, eventos e contratos de integração

**Estado:** Normativo · **Depende de:** RFC 02–09

## Objetivo

Definir uma superfície de integração estável para UI, canais e operadores. A API expõe recursos e comandos; não expõe diretamente o motor de raciocínio nem segredos.

## Arquitetura

```text
Cliente/UI/Conector → API Gateway → autenticação + versão + limites
                              │
              ┌───────────────┼───────────────┐
              ▼               ▼               ▼
           comandos        consultas       stream de eventos
              │               │               │
              └────→ núcleo e serviços ←─────┘
```

## Recursos e interfaces

```text
POST /v1/events                 aceita evento normalizado
POST /v1/goals                  cria objetivo em DRAFT
GET  /v1/goals/{id}             lê estado e plano autorizado
POST /v1/approvals/{id}/decide  aprova/rejeita âmbito exato
GET  /v1/memories               pesquisa com ACL
PATCH /v1/memories/{id}         corrige memória
DELETE /v1/memories/{id}        solicita esquecimento
GET  /v1/audit                  consulta auditável paginada
GET  /v1/stream                 WebSocket/SSE de eventos autorizados
```

```text
ApiEnvelope
  request_id, correlation_id, api_version, data, errors[], links[]
ApiError
  code, message, retryable, field?, support_reference
DomainEvent
  id, type, subject_ref, occurred_at, producer, schema_version
  correlation_id, causation_id, payload_ref|payload_json, sensitivity
```

## Regras obrigatórias

1. Comandos de escrita DEVEM aceitar `Idempotency-Key`; pedidos repetidos devolvem o mesmo resultado lógico.
2. A API DEVE versionar alterações incompatíveis e publicar esquemas de eventos.
3. Consultas DEVEM filtrar autorização no servidor; clientes nunca recebem “tudo” para filtrar localmente.
4. Erros não DEVEM revelar segredos, detalhes internos ou se um recurso sensível existe para um utilizador não autorizado.
5. Eventos são factos imutáveis; correções são novos eventos que referenciam os anteriores.

## Casos limite e erro

- **Cliente desatualizado:** devolver versão suportada e instrução de migração, não interpretar campos desconhecidos de forma permissiva.
- **Entrega de webhook falha:** reentregar com backoff e assinatura; após limite, encaminhar para fila morta auditável.
- **Stream perde ligação:** cliente retoma por cursor; servidor limita retenção e informa lacunas.
- **Paginação muda durante consulta:** usar cursor estável, não índices numéricos.

## Justificação

Eventos imutáveis permitem reconstruir estado e investigar incidentes. Separar comandos de consultas reduz efeitos acidentais e melhora controlo de acesso.

## Expansões futuras

SDKs oficiais, GraphQL apenas para leitura, federação de eventos, API de administração segregada e contratos de parceiros.

