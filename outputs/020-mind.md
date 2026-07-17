# Aurora OS — RFC 020: Mind — modelo de estado cognitivo

**Estado:** Normativo · **Depende de:** RFC 000, 01, 010

## Objetivo

Definir a **Mind** como agregado canónico de estado persistente da Aurora. A Mind não é um processo, um prompt nem uma base de dados única; é o contrato que liga identidade, realidade conhecida, experiência e intenção. As RFCs de memória, conhecimento, objetivos, personalidade e aprendizagem concretizam os seus componentes sem alterar este contrato.

## Composição

```text
Mind
├─ Identity                 quem é e como comunica
├─ Memory / Experience      o que aconteceu e o que foi aprendido
├─ Knowledge / World Model  o que é conhecido sobre entidades e relações
├─ Goals / Plans / Tasks    o que se pretende alcançar
├─ Attention                o que merece recursos agora
├─ Working Memory            contexto ativo, temporário e isolado
├─ Reflection / Learning    avaliação e mudança governada
└─ Policies / Permissions   o que é permitido fazer
```

## Estruturas de dados

```text
Mind
  id, tenant_id, status: INITIALIZING|ACTIVE|DEGRADED|PAUSED|RECOVERING|RETIRED
  identity_id, policy_set_version, world_model_version
  active_context_ids[], active_goal_ids[], last_consolidation_at
  created_at, updated_at

MindChangeSet
  id, mind_id, source: USER|CYCLE|TOOL|SCHEDULER|OPERATOR
  changes[], evidence_refs[], policy_decision_ids[]
  status: PROPOSED|VALIDATED|APPLIED|REJECTED|ROLLED_BACK
```

## Interfaces

```text
MindService.open(tenant_id) -> Mind
MindService.snapshot(mind_id, scope) -> MindSnapshot
MindService.propose(change_set) -> MindChangeSet
MindService.apply(change_set_id) -> Mind
MindService.pause(mind_id, actor) -> Mind
```

## Regras obrigatórias

1. O estado persistente da Aurora DEVE pertencer a uma `Mind` e estar sujeito a controlo de acesso.
2. `MindChangeSet` DEVE ser atómico por agregado ou compensado por mudanças explícitas; nunca parcialmente silencioso.
3. `PAUSED` impede novas ações e tarefas automáticas, mas permite inspeção e exportação autorizada.
4. Working Memory não é consolidada sem a RFC 024 e RFC 03; observação temporária não é memória.

## Casos limite e erro

- **Duas operações alteram a mesma intenção:** usar versão otimista; uma falha com conflito e é reavaliada.
- **Recuperação após interrupção:** `RECOVERING` reconcilia tarefas e chamadas de ferramentas antes de regressar a `ACTIVE`.
- **Retirada da Mind:** exportar/apagar segundo retenção, revogar conectores e impedir reativação sem bootstrap explícito.

## Justificação

Nomear a Mind elimina a falsa simplificação “LLM + memória”. A arquitetura passa a tratar contexto, realidade, objetivos e experiência como partes de um mesmo sistema governado.

## Expansões futuras

Minds pessoais e de equipa, snapshots assinados, ramificações para simulação e migração entre instalações.
