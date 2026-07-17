# Aurora OS — RFC 09: Segurança, privacidade e auditoria

**Estado:** Normativo · **Depende de:** RFC 01–08

## Objetivo

Definir controlos para proteger identidade, dados, segredos, integrações e decisões. Segurança é uma propriedade de todo o ciclo, não um módulo final.

## Arquitetura

```text
Canal → autenticação → autorização → validação/isolamento
                                      │
Dados → classificação → cifragem → acesso mínimo → auditoria imutável
                                      │
Ferramenta → política → cofre de segredos → executor isolado → reconciliação
```

## Modelo de ameaças mínimo

| Ameaça | Controlo principal |
| --- | --- |
| Injeção em web/email/documentos | conteúdo não confiável, isolamento e política fora do modelo |
| Conta ou token comprometido | OAuth com âmbito mínimo, rotação, revogação e deteção de anomalia |
| Exfiltração por prompt/log | classificação, redação, cofre e filtragem de saída |
| Ação duplicada | idempotência, estado `UNKNOWN` e reconciliação |
| Escalada via SSH/browser | capacidades limitadas, sandbox e aprovação por ação |
| Corrupção de memória | proveniência, revisões, ACL e backups testados |

## Estruturas de dados

```text
SecurityEvent
  id, severity: INFO|LOW|MEDIUM|HIGH|CRITICAL, type
  correlation_id, actor_ref, resource_ref, decision_ref
  evidence_ref, detected_at, status: OPEN|CONTAINED|RESOLVED

SecretReference
  id, provider, locator, purpose, allowed_tool_ids[]
  rotation_due_at, status: ACTIVE|REVOKED|EXPIRED

AuditRecord
  id, occurred_at, actor, action, target, outcome
  correlation_id, input_hash, policy_ids[], previous_hash, record_hash
```

O diário de auditoria é encadeado por hash e tem controlo de escrita separado da aplicação. Não contém segredos nem conteúdo integral desnecessário.

## Interfaces

```text
Auth.authenticate(credential, channel) -> Principal
Policy.authorize(principal, action, resource) -> PolicyDecision
Vault.lease(secret_reference, tool_call_id) -> EphemeralSecretHandle
Audit.append(record) -> AuditRecord
Incident.open(security_event) -> Incident
```

## Regras obrigatórias

1. Autenticação forte DEVE proteger administração, alteração de políticas e exportação de dados.
2. Segredos DEVEM ser cifrados em repouso, transmitidos apenas por canais protegidos e redigidos antes de qualquer registo.
3. Todos os serviços DEVEM usar identidades próprias e comunicação autenticada; não há conta partilhada de superutilizador.
4. Registos de auditoria DEVEM ser retidos e protegidos segundo política; o utilizador pode ver ações que lhe dizem respeito.
5. Incidentes de alto risco DEVEM revogar capacidade afetada, preservar evidência e notificar o proprietário.

## Casos limite e erro

- **Suspeita de segredo exposto:** revogar/rodar, bloquear chamadas dependentes e abrir incidente; nunca reenviar o valor ao chat.
- **Verificação de integridade falha:** colocar auditoria em modo de incidente, impedir ações de escrita e preservar cópia forense.
- **Fornecedor de modelo sem garantias de dados:** não enviar dados classificados acima do permitido.
- **Cópia de segurança ilegível:** incidente de fiabilidade; não assumir recuperação até restauro de teste passar.

## Justificação

O sistema tem superfícies de ataque invulgarmente amplas — linguagem, dados e ações externas. Defesa em profundidade reduz dependência de o modelo “portar-se bem”.

## Expansões futuras

HSM, gestão centralizada de chaves, SIEM, deteção comportamental, testes de intrusão regulares e isolamentos por cliente.

