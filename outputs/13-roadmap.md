# Aurora OS — RFC 13: Roadmap, marcos e aceitação

**Estado:** Normativo · **Depende de:** RFC 00–12

## Objetivo

Transformar a visão em entregas verificáveis. Este roadmap estabelece portas de qualidade; não autoriza saltar controlos para demonstrar funcionalidades.

## Arquitetura de entrega

```text
Especificar → implementar → testar → avaliar segurança → piloto limitado
      ▲                                                    │
      └────────── falha/regressão ← medir ← operar ────────┘
```

## Estruturas de dados

```text
Milestone
  id, name, scope_refs[], entry_criteria[], exit_criteria[]
  risks[], owner, status: PLANNED|ACTIVE|AT_RISK|ACCEPTED|REJECTED

AcceptanceEvidence
  id, milestone_id, criterion_id, test_ref, result
  reviewed_by, reviewed_at, exceptions[]

RiskRegisterEntry
  id, description, likelihood, impact, mitigation, owner, status
```

## Interfaces

```text
Programme.start(milestone_id) -> Milestone
Quality.submitEvidence(milestone_id, evidence) -> AcceptanceEvidence
Architecture.accept(milestone_id) -> Decision
Risk.review() -> RiskRegisterEntry[]
```

## Marcos e critérios de saída

| Marco | Resultado | Critérios de aceitação mínimos |
| --- | --- | --- |
| M0 — Fundação | RFCs, ADRs e ambiente de teste | todas as RFCs revistos; modelo de ameaças e dados aprovados |
| M1 — Núcleo | conversa, WorkItem, Thought, auditoria | eventos idempotentes; falhas recuperáveis; modelo substituível |
| M2 — Memória | memória corrigível e grafo inicial | proveniência/ACL; eliminação propagada; conflitos visíveis |
| M3 — Planeamento | objetivos e tarefas persistentes | dependências, aprovação, cancelamento e critérios de aceitação |
| M4 — Ferramenta piloto | conector de baixo risco | política, cofre, reconciliação e registo end-to-end |
| M5 — Operação | VPS e UI de controlo | backup restaurado; alertas; permissões e auditoria navegáveis |
| M6 — Expansão | canais externos graduais | testes de segurança por conector; automatização revogável |

## Regras obrigatórias

1. Nenhum marco é aceite sem evidência reproduzível, revisão de segurança e documentação atualizada.
2. Funcionalidades de escrita externa só avançam depois de uma ferramenta piloto demonstrar política, aprovação, idempotência e reconciliação.
3. Exceções DEVEM ter dono, data de expiração e plano de mitigação; não se tornam comportamento permanente por omissão.
4. Métricas de sucesso DEVEM incluir taxa de ações corretas, incidentes, custo, latência, recuperação e satisfação do utilizador.
5. A arquitetura DEVE ser revista antes de cada marco para confirmar que decisões anteriores ainda são válidas.

## Casos limite e erro

- **Critério de marco falha:** estado `REJECTED` ou `AT_RISK`; replaneamento é obrigatório antes de nova tentativa.
- **Evidência incompleta:** não equivale a aprovação; manter marco aberto.
- **Incidente durante piloto:** pausar expansão, investigar, corrigir e repetir a evidência afetada.
- **Mudança de fornecedor/modelo:** executar avaliação de compatibilidade, privacidade e regressão antes de promover.

## Justificação

Marcos definidos por evidência combatem o “feature rush” e permitem entregar valor sem normalizar risco técnico. O projeto cresce por confiança demonstrada, não por uma promessa de autonomia.

## Expansões futuras

Certificação de conectores, testes independentes, métricas públicas de fiabilidade, versões empresariais e um processo formal de RFC/ADR para contribuidores.

