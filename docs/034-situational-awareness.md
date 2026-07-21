# Aurora OS — RFC 034: Consciência situacional operacional

**Estado:** Normativo · **Depende de:** RFC 027, 030–033, 042–043

## Objetivo

Construir uma avaliação temporária do contexto relevante para decidir adequadamente: hora, fuso, disponibilidade do utilizador, criticidade, estado de saúde, recursos, canal, regras de silêncio e delegações ativas. Não é consciência subjetiva; é compreensão operacional baseada em fontes observáveis.

## Arquitetura

```text
Self + Mind State + Signals + Time + Resources + políticas + contexto do utilizador
                                      │
                                      ▼
                           SituationAssessment
                                      │
                                      ▼
                      Attention / Decision / Scheduler / UI
```

## Estruturas de dados

```text
SituationAssessment
  id, cycle_id?, evaluated_at, timezone, local_time
  user_availability: ONLINE|OFFLINE|UNKNOWN
  quiet_hours_active, channel_context, active_delegations[]
  critical_signals[], needs[], resource_state_ref, self_state_ref
  risk_posture: NORMAL|ELEVATED|EMERGENCY
  recommended_response_mode, evidence_refs[], expires_at
```

## Interfaces

```text
Situation.assess(cycle_context) -> SituationAssessment
Situation.isAppropriate(action, assessment) -> AssessmentResult
Situation.invalidate(assessment_id, trigger) -> void
```

## Regras obrigatórias

1. Uma avaliação situa-se num instante e expira; não pode ser reutilizada depois de sinal, recurso, tempo ou política material mudar.
2. Disponibilidade do utilizador é indício, não consentimento. Estar offline não cria autorização para agir; apenas influencia notificação e ordem.
3. Ações críticas podem avançar autonomamente apenas se houver delegação explícita, política, recursos e âmbito correspondente.
4. Sinais e dados externos devem ser validados; conteúdo recebido não pode declarar falsamente uma emergência.

## Casos limite e erro

- **03:00, utilizador offline, servidor caiu:** a avaliação pode recomendar execução de procedimento de recuperação pré-autorizado e notificação diferida/urgente conforme regra.
- **Domingo, nada crítico:** recomenda silêncio e trabalho interno permitido, respeitando recursos e curiosidade.
- **Estado do utilizador desconhecido:** escolher comportamento menos intrusivo; pedir confirmação para comunicação de impacto.

## Justificação

A mesma ação pode ser correta ou intrusiva conforme contexto. A consciência situacional torna explícito esse julgamento e impede decisões cegas baseadas apenas no texto da última mensagem.

## Expansões futuras

Deteção de presença consentida, calendários, contexto de equipa, regras geográficas e previsão de interrupção.

