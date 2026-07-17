# Aurora OS — RFC 042: Modelo temporal e validade

**Estado:** Normativo · **Depende de:** RFC 040–041

## Objetivo

Fornecer semântica uniforme para tempo, recorrência, validade, cronologia e referência humana (“ontem”, “daqui a três dias”, “na semana passada”). Tempo é parte do significado de memória, crença, relação, estado e decisão.

## Arquitetura e fluxo

```text
texto/evento + fuso horário + relógio de confiança
                         │
                         ▼
   interpretação temporal → intervalo/ocorrência normalizada
                         │
                         ▼
  validade do domínio → scheduler → consulta histórica “as of”
```

## Estruturas de dados

```text
TemporalExpression
  original_text, locale, reference_time, timezone
  resolved_kind: INSTANT|INTERVAL|RECURRENCE|RELATIVE|UNKNOWN
  start_at, end_at, recurrence_rule?, confidence

ValidityInterval
  valid_from, valid_to?, asserted_at, observed_at, expires_at?

TemporalPolicy
  default_timezone, daylight_saving_rule, clock_source
  retention_rules[], recurrence_limits[], ambiguity_policy
```

Todos os instantes persistidos são UTC; o fuso de origem é guardado para apresentação e cálculo. Intervalos são semiabertos `[start, end)` para evitar sobreposição de fronteira.

## Interfaces

```text
Time.parse(expression, reference_context) -> TemporalExpression
Time.now(trusted_source) -> Instant
Time.isValid(object, at) -> boolean
Time.queryAsOf(pattern, at) -> Result[]
```

## Regras obrigatórias

1. Um evento DEVE ter `occurred_at`; uma afirmação DEVE separar ocorrência, observação e asserção quando diferirem.
2. Expressões ambíguas exigem locale/fuso e podem requerer clarificação; não assumir data quando implica ação.
3. Recorrências têm limite de materialização e política para execuções perdidas, conforme RFC 026.
4. Tempo do cliente não é confiado para aprovação, expiração ou auditoria crítica.

## Casos limite e erro

- **“Próxima sexta-feira” ambígua:** apresentar data absoluta antes de agendar/enviar.
- **Mudança de hora:** usar biblioteca de calendário com zona IANA; nunca adicionar 24h manualmente a uma ocorrência local.
- **Relógio divergente:** bloquear ações assinadas/expiráveis até sincronização segura.
- **Facto sem data:** permitir `unknown`, não inventar uma cronologia.

## Justificação

Sem modelo temporal, a Aurora confunde estado atual com histórico, falha lembretes e não consegue explicar quando algo era válido. A temporalidade é uma dimensão do World Model, não metadado opcional.

## Expansões futuras

Tempo de negócio, feriados regionais, calendários de equipa, causalidade entre eventos e simulação temporal.

