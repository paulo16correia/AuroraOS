# ADR-014 — Política persistente antes de capacidades externas

**Estado:** aceite  
**Data:** 2026-07-18

## Contexto

O SDK de capabilities estabilizou o contrato dos executores, mas um executor não deve decidir se uma entidade pode agir. Sem uma camada própria, autorização, aprovação e limites acabariam distribuídos pelos adaptadores de email, ficheiros e calendários.

## Decisão

Adotar `CapabilityPolicyService` como fronteira Kernel-owned. A política é persistida por entidade e capability e produz `PolicyEvaluation` imutável. A decisão é aplicada antes da preparação e reavaliada imediatamente antes da execução aprovada.

`CapabilityPolicy` é a fonte de verdade para: principal, sessão, estado da regra, exigência de aprovação e limite diário. Um executor recebe apenas uma operação já autorizada; não recebe a política nem acede ao estado da Mind.

## Consequências

- A revogação de uma policy tem efeito mesmo sobre pedidos já aprovados.
- Um denial é observável como `CapabilityResult(POLICY_DENIED)`.
- A introdução de um novo executor não requer adicionar regras de autorização ao Kernel.
- A configuração administrativa das policies permanece fora deste slice; os defaults são criados no nascimento da entidade.

## Alternativas rejeitadas

1. **Verificar permissões dentro de cada executor.** Duplica lógica e cria bypasses.
2. **Usar apenas aprovação humana.** Não cobre quotas, limites de sessão ou revogação.
3. **Permitir quando não existir policy.** Viola o princípio fail-closed.

## Migração

Entidades existentes recebem policies default durante `ensure_ready`. Nenhuma capability externa é executada se a policy correspondente não existir.
