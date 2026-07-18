# VS-014 — Políticas de Capacidades

## Objetivo

Introduzir uma fronteira de autorização persistente entre `CapabilityRequest` e o executor. Uma capability disponível não constitui autorização para a usar. O Kernel deve avaliar uma política antes de preparar uma operação e novamente antes de executar uma aprovação persistida.

## Escopo

Incluído: políticas por entidade e capability, aprovação obrigatória, autorização por principal e sessão, limite diário, decisão auditável e negação segura.

Excluído: editor de políticas na interface, delegação entre entidades, quotas por dispositivo, políticas temporais complexas e planos com vários passos.

## Fluxo normativo

```text
CapabilityRequest
       |
       v
CapabilityPolicyService.evaluate
       |
       +-- DENIED --> CapabilityResult(POLICY_DENIED) --> Response
       |
       +-- ALLOWED --> Executor.prepare --> ApprovalRequest (se exigida)
                                             |
                                             v
                                           Approval
                                             |
                                             v
                              CapabilityPolicyService.evaluate
                                             |
                                             +-- DENIED --> CapabilityResult(POLICY_DENIED)
                                             +-- ALLOWED --> Executor.execute
```

## Objetos de domínio

### `CapabilityPolicy`

| Campo | Significado |
|---|---|
| `policy_id` | Identificador determinístico por entidade e capability. |
| `capability_ref` | Capability protegida. |
| `status` | `ACTIVE` ou estado inativo futuro. |
| `approval_requirement` | `REQUIRED` ou `NOT_REQUIRED`. |
| `allowed_principal_ids` | Principais autorizados; `*` representa todos. |
| `allowed_session_ids` | Sessões autorizadas; `*` representa todas. |
| `daily_limit` | Máximo de execuções concluídas no dia UTC; `null` significa sem limite. |
| `version` | Incrementado por alterações futuras via API administrativa. |

### `PolicyEvaluation`

É imutável e preserva a evidência de uma decisão: policy usada, pedido analisado, resultado `ALLOWED` ou `DENIED`, motivo, exigência de aprovação e contagem diária no instante da avaliação.

## Regras obrigatórias

1. Nenhum executor interpreta permissões, sessões ou quotas.
2. Política inexistente equivale a `DENIED`.
3. A política é verificada antes de `prepare()` e antes de `execute()`.
4. Uma negação produz `CapabilityResult(POLICY_DENIED)` e nunca uma preparação, aprovação ou ação externa.
5. A aprovação humana não sobrepõe uma política entretanto revogada.
6. Os limites contam apenas execuções `COMPLETED`; falhas e operações incertas não consomem quota.
7. Cada avaliação publica `CapabilityPolicyEvaluated` e uma entrada de trace `CAPABILITY_POLICY`.

## Políticas iniciais

No nascimento da entidade, o runtime cria uma política por capability registada. As capabilities externas exigem aprovação; `EMAIL_SEND` recebe limite diário inicial de 10. Estes valores são defaults explícitos, não regras escondidas no executor.

## Casos limite

- **Reinício entre aprovação e envio:** a política é reavaliada; se mudou para `DENIED`, não ocorre envio.
- **Principal diferente:** uma confirmação vinda de principal não autorizado cria uma negação auditável.
- **Limite atingido:** o pedido não prepara o executor e não cria aprovação pendente.
- **Policy inexistente por migração incompleta:** falha fechada com `POLICY_NOT_FOUND`.

## Critérios de aceitação

1. Um email autorizado segue o fluxo existente sem alteração do executor.
2. Um principal não listado recebe `POLICY_DENIED` antes de `ExecutionPreparation`.
3. Uma política e cada avaliação sobrevivem a shutdown/restore.
4. O teste de regressão de envio idempotente permanece verde.

## Expansões futuras

- políticas por dispositivo, canal, janela temporal e classificação de dados;
- quotas por custo, fornecedor e destino;
- API administrativa versionada e assinada;
- motor declarativo para combinar várias políticas sem alterar o Kernel.
