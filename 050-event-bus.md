# Aurora Platform — RFC 050: Event Bus

**Estado:** Normativo · **Depende de:** LAW-007, RFC 040, 045

## Objetivo

Definir o Event Bus como a espinha dorsal de comunicação entre Kernel, Mind e Applications. O Bus transporta factos de domínio e sinais de controlo; não transporta segredos nem substitui armazenamento canónico.

## Arquitetura

```text
Produtor → Outbox transacional → Event Bus → subscrição idempotente → consumidor
                 │                   │                 │
                 ▼                   ▼                 ▼
              auditoria          retenção         Dead-letter queue

MemoryCreated → Knowledge / Planner / Reflection / UI / Logs
```

## Estruturas de dados

```text
DomainEvent
  event_id, type, schema_version, producer, occurred_at
  correlation_id, causation_id, aggregate_ref, payload_ref|payload_json
  sensitivity, idempotency_key?, integrity_hash

Subscription
  id, consumer, event_types[], filter_ref, delivery_mode
  checkpoint, status: ACTIVE|PAUSED|FAILED, retry_policy

Delivery
  event_id, subscription_id, attempt, delivered_at
  status: PENDING|ACKED|RETRYING|DEAD_LETTER|SKIPPED
```

## Interfaces

```text
Bus.publish(event) -> DomainEvent
Bus.subscribe(subscription) -> Subscription
Bus.ack(delivery_id) -> Delivery
Bus.replay(subscription_id, cursor) -> Delivery[]
```

## Regras obrigatórias

1. Produtores escrevem estado e outbox na mesma transação; publicação posterior é reexecutável.
2. Consumidores são idempotentes, verificam esquema e não assumem ordem global entre agregados.
3. Dados grandes/sensíveis usam referências autorizadas, não payloads abertos.
4. Falhas repetidas vão para fila morta auditável, nunca são descartadas silenciosamente.
5. O Bus não permite que subscritores obtenham permissões do produtor.

## Casos limite e erro

- **Evento duplicado:** consumidor ignora pelo `event_id`/chave de idempotência.
- **Esquema novo:** consumidor incompatível pausa com diagnóstico; não interpreta campos desconhecidos permissivamente.
- **Bus indisponível:** serviços aceitam apenas operações cuja outbox possa ser persistida; efeitos externos dependentes são adiados.

## Justificação

Eventos permitem que `MemoryCreated` atualize índice, UI e auditoria sem acoplamento direto. O Bus cria uma história de causalidade que suporta recuperação e depuração.

## Expansões futuras

Partições, retenção por classe de dados, federação entre instalações e fluxos de eventos analíticos isolados.

