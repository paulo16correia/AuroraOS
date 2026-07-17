# Aurora OS — RFC 060: SDK de plugins e extensões

**Estado:** Normativo · **Depende de:** RFC 06, 09, 040

## Objetivo

Permitir que terceiros adicionem capacidades à Aurora sem receberem autoridade implícita sobre a Mind, segredos ou ferramentas existentes. Um plugin é uma extensão declarativa e isolada, não código confiado por defeito.

## Arquitetura

```text
Autor do plugin → manifesto assinado → validação de compatibilidade
                                             │
                                             ▼
                       catálogo de capacidades e políticas exigidas
                                             │
                                             ▼
                   instalação isolada → autorização por capacidade
                                             │
                                             ▼
                               Tool Orchestrator → plugin sandbox
```

## Estruturas de dados

```text
PluginManifest
  plugin_id, version, publisher, signature, min_platform_version
  capabilities[], tool_manifests[], event_subscriptions[]
  required_permissions[], data_classes[], network_endpoints[]
  migration_manifest?, documentation_ref, integrity_hash

PluginInstallation
  id, plugin_id, version, status: INSTALLED|DISABLED|QUARANTINED|REMOVED
  granted_permissions[], config_ref, installed_at, updated_at

PluginCapability
  key, input_schema, output_schema, effects[], approval_requirements
  rate_limit, timeout, idempotency_support, audit_level
```

## Interfaces

```text
Plugin.verify(manifest, signature) -> VerificationReport
Plugin.install(manifest, approval) -> PluginInstallation
Plugin.describe(capability) -> ToolManifest
Plugin.invoke(authorised_tool_call) -> ToolResult
Plugin.disable(installation_id, actor) -> PluginInstallation
```

## Regras obrigatórias

1. Cada plugin DEVE declarar capacidades, efeitos, dados tratados, domínios de rede e permissões; pedidos dinâmicos não declarados são negados.
2. Plugins DEVEM executar isolados, sem acesso ao processo principal, base de dados, cofre ou rede geral.
3. A instalação e qualquer aumento de permissão exigem revisão/consentimento explícito.
4. Um plugin nunca recebe dados acima da classificação declarada nem pode subscrever eventos sem filtro de autorização.
5. Atualizações com alterações incompatíveis, novas permissões ou nova origem exigem nova verificação e podem entrar em `QUARANTINED`.

## Casos limite e erro

- **Assinatura inválida:** rejeitar antes de instalar; não executar sequer em modo de pré-visualização.
- **Plugin falha repetidamente:** circuito de proteção desativa a capacidade e preserva chamadas pendentes para reconciliação.
- **Atualização remove capacidade usada em plano ativo:** bloquear tarefas dependentes e pedir replaneamento.
- **Plugin tenta enviar segredo em output:** redator bloqueia saída, abre evento de segurança e coloca instalação em quarentena.

## Justificação

Um SDK maduro converte extensibilidade num contrato de segurança, em vez de transformar cada integração numa exceção privilegiada. Assim um plugin Spotify pode ser útil sem receber poderes sobre email, SSH ou a Mind.

## Expansões futuras

Marketplace com reputação, certificação, permissões delegadas, testes de conformidade e plugins declarativos sem código arbitrário.

