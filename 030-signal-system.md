# Aurora OS — RFC 030: Sistema de sinais

**Estado:** Normativo · **Depende de:** RFC 011, 021, 040, LAW-001

## Objetivo

Representar estímulos do mundo que podem merecer atenção. Um **Signal** não é um evento: um evento é um facto normalizado e imutável; um sinal é uma avaliação temporária de relevância, urgência e necessidade de interrupção derivada desse evento.

## Arquitetura

```text
External World → Event/Observation → Perception → Signal
                                               │
                         urgência/prioridade ──┼→ Attention → Cognitive Cycle
                                               └→ fila, supressão ou escalada
```

## Estruturas de dados

```text
Signal
  id, source_event_ref, kind: MESSAGE|ALERT|CHANGE|SCHEDULE|HEALTH|OPPORTUNITY
  severity: INFO|LOW|MEDIUM|HIGH|CRITICAL
  urgency, relevance, confidence, target_refs[], expires_at
  interruptibility: QUEUE|FOCUS_WHEN_IDLE|INTERRUPT|EMERGENCY
  status: NEW|QUEUED|FOCUSED|SUPPRESSED|EXPIRED|RESOLVED
  reason_codes[], policy_refs[]
```

## Interfaces

```text
Signals.emit(event_ref, classifier) -> Signal
Signals.route(signal_id) -> RouteDecision
Signals.acknowledge(signal_id, resolution_ref) -> Signal
```

## Regras obrigatórias

1. Todo Signal deve referenciar uma fonte observável; o modelo não cria sinais sem evento, schedule ou estado de saúde verificável.
2. Urgência não concede permissão nem chama ferramentas; apenas altera atenção e ordem de avaliação.
3. Interrupção exige limiar de política e deve preservar o ciclo interrompido para retoma.
4. Signals expiram ou são resolvidos; não se tornam memória permanente sem LAW-001.

## Casos limite e erro

- **Servidor em baixo:** signal `CRITICAL/EMERGENCY` interrompe trabalho de baixo risco, mas toda a ação corretiva continua a exigir decisão e permissão.
- **Mensagem não urgente:** vai para `QUEUE` ou `FOCUS_WHEN_IDLE`; não provoca ação automática.
- **Tempestade de sinais:** deduplicar por origem/alvo, agrupar e aplicar limites de taxa.

## Justificação

Signals transformam acontecimentos num sistema nervoso com prioridade explícita, sem confundir ruído externo com obrigação de agir.

## Expansões futuras

Correlação de incidentes, sinais multimodais, políticas de interrupção por canal e análises de fadiga de alertas.

