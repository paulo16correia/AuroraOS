# Aurora Platform — RFC 037: Modelo de desenvolvimento

**Estado:** Normativo · **Depende de:** RFC 07, 027–028, 036, 040

## Objetivo

Definir como uma instância ganha confiança operacional ao longo do tempo sem que “aprender” se converta em aumento automático de poder. Desenvolvimento altera padrões de assistência, exigência de confirmação e autonomia delegada dentro dos limites do Genome, Constituição e políticas.

## Arquitetura

```text
evidência de uso + avaliações + preferências explícitas + incidentes
                                  │
                                  ▼
                         DevelopmentAssessment
                                  │
                                  ▼
                    fase proposta → aprovação/política → perfil efetivo
```

## Estruturas de dados

```text
DevelopmentProfile
  id, genome_ref, stages[]
Stage
  id, name, autonomy_ceiling, confirmation_rules[], capability_constraints[]
  promotion_criteria[], regression_criteria[]

DevelopmentState
  mind_id, current_stage_id, evidence_refs[], assessment_at
  status: PROBATION|ACTIVE|RESTRICTED|PAUSED
```

Uma fase inicial pode privilegiar explicação, rascunhos e confirmação; fases posteriores podem permitir regras pré-autorizadas estreitas. Nenhuma fase concede capacidades fora do que o utilizador e a política autorizaram.

## Interfaces

```text
Development.assess(mind_id) -> DevelopmentAssessment
Development.proposeTransition(mind_id, target_stage) -> Proposal
Development.applyTransition(proposal_id, approval) -> DevelopmentState
```

## Regras obrigatórias

1. Promoção requer evidência de fiabilidade, não apenas tempo decorrido.
2. Incidente, revogação ou degradação relevante pode restringir/reverter a fase.
3. Personalidade pode adaptar estilo dentro de limites; valores constitucionais e identidade base não evoluem por inferência.
4. Mudanças de autonomia são visíveis e reversíveis pelo utilizador.

## Casos limite e erro

- **Muitos sucessos de baixo risco:** não justificam autonomia financeira, SSH ou comunicação pública.
- **Falha durante fase avançada:** reduzir apenas o âmbito afetado quando possível, preservando evidência.
- **Dados insuficientes:** manter fase atual; não promover por padrão.

## Justificação

Desenvolvimento distingue maturidade operacional de uma personalidade que “deriva”. A confiança cresce por provas e pode diminuir por risco.

## Expansões futuras

Perfis por domínio, ensaios controlados, canários de autonomia e métricas de confiança explicáveis.
