# Aurora OS — RFC 08: Aprendizagem, reflexão e avaliação

**Estado:** Normativo · **Depende de:** RFC 02–07

## Objetivo

Permitir melhoria operacional a partir de resultados observados sem transformar cada conversa em alteração permanente de comportamento. Aprender significa propor, avaliar e consolidar conhecimento ou procedimento sob governação.

## Responsabilidades e arquitetura

```text
Conclusão de WorkItem/Task
             │
             ▼
     recolher evidência e métricas
             │
             ▼
  Reflection candidata → validação → ação de aprendizagem
             │                                │
             ▼                                ▼
          relatório                    memória/procedimento/política
```

O componente produz reflexões breves sobre sucesso, falha, incerteza, custo e segurança. Não reescreve o perfil, políticas ou ferramentas diretamente.

## Estruturas de dados

```text
Reflection
  id, work_item_id, task_id?, outcome: SUCCESS|PARTIAL|FAILURE|UNKNOWN
  evidence_refs[], observations[], root_causes[], lessons[]
  confidence, proposed_change_refs[], reviewer_required, status
  status: DRAFT|ACCEPTED|REJECTED|IMPLEMENTED|EXPIRED

LearningProposal
  id, type: MEMORY|PROCEDURE|PROMPT_CONFIG|POLICY_SUGGESTION
  change_json, expected_benefit, risk, evidence_refs[]
  evaluation_plan, rollback_plan, status: PROPOSED|APPROVED|TESTING|
    DEPLOYED|REJECTED|ROLLED_BACK

EvaluationRun
  id, proposal_id, dataset_ref, metrics_json, verdict, executed_at
```

## Interfaces

```text
Reflector.create(work_item_id) -> Reflection
Learning.propose(reflection_id) -> LearningProposal
Evaluator.run(proposal_id, test_scope) -> EvaluationRun
Learning.apply(proposal_id, approval) -> ChangeRecord
```

## Regras obrigatórias

1. Uma reflexão DEVE citar evidência concreta; uma impressão do modelo não é causa-raiz confirmada.
2. Apenas alterações de memória de baixo risco podem ser consolidadas automaticamente e ainda assim obedecem à RFC 03.
3. Mudanças de personalidade, política, ferramentas, modelos ou automação DEVEM ser aprovadas, testadas e reversíveis.
4. Avaliações DEVEM medir também regressão de segurança, custo e privacidade, não só qualidade textual.
5. O sistema NÃO DEVE treinar modelos com dados do utilizador sem consentimento específico e contrato de dados apropriado.

## Casos limite e erro

- **Resultado desconhecido:** criar reflexão `UNKNOWN`; não inferir sucesso a partir de ausência de erro.
- **Proposta conflitua com uma política:** rejeitar e guardar razão; nunca tentar um desvio alternativo.
- **Métrica melhora num caso e piora noutro:** manter em teste e requerer decisão humana segundo limiar definido.
- **Falha durante aplicação:** executar rollback ou marcar estado parcial, bloquear nova aplicação e abrir incidente.

## Justificação

Separar observação, proposta, teste e aplicação impede a deriva silenciosa de comportamento. A aprendizagem fica rastreável e pode ser revertida, condição essencial num assistente persistente.

## Expansões futuras

Conjuntos de avaliação privados, experimentação canário, aprendizagem federada com consentimento e recomendações de manutenção preditiva.

