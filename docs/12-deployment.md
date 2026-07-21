# Aurora OS — RFC 12: Implementação, operação e continuidade

**Estado:** Normativo · **Depende de:** RFC 09–11

## Objetivo

Definir como operar a Aurora numa VPS de forma recuperável e observável. A primeira implantação privilegia simplicidade, isolamento e restauro testado, não complexidade prematura.

## Arquitetura de referência

```text
Internet → proxy TLS/WAF → API/UI
                            │
            ┌───────────────┼────────────────────┐
            ▼               ▼                    ▼
       núcleo/worker   PostgreSQL+índice      cofre de segredos
            │               │                    │
            ▼               ▼                    ▼
        conectores       backups cifrados     métricas/logs/auditoria
```

Os serviços são contentorizados e executados com utilizadores não privilegiados, redes segmentadas e volumes persistentes mínimos. O processo de ferramenta deve ter limites mais restritos que o núcleo.

## Estruturas de dados operacionais

```text
DeploymentManifest
  release_id, image_digests[], schema_version, config_version
  migration_ids[], approved_by, deployed_at, rollback_release_id

BackupSnapshot
  id, scope, encrypted_location_ref, started_at, completed_at
  checksum, restore_test_at, retention_until, status

HealthCheck
  component, status: PASS|WARN|FAIL, checked_at
  latency_ms, dependency_refs[], detail_safe
```

## Interfaces

```text
Operations.deploy(manifest) -> DeploymentResult
Operations.rollback(release_id) -> DeploymentResult
Backup.create(scope) -> BackupSnapshot
Backup.restoreTest(snapshot_id, isolated_target) -> RestoreReport
Health.read() -> HealthCheck[]
```

## Regras obrigatórias

1. Produção DEVE usar TLS, DNS controlado, atualizações de segurança e acesso administrativo com autenticação forte.
2. Cada release DEVE fixar versões de imagem, ter migração reversível ou estratégia de compatibilidade e passar verificações antes de receber tráfego.
3. Backups cifrados DEVEM incluir dados, configurações necessárias e referência ao esquema; restauros DEVEM ser testados periodicamente num ambiente isolado.
4. Métricas, logs e alertas DEVEM ser redigidos; logs não substituem o diário de auditoria.
5. Escalar recursos ou adicionar serviços só deve acontecer com uma necessidade medida: carga, disponibilidade, isolamento ou recuperação.

## Fluxo de release

```text
artefacto assinado → validação → backup pré-release → migração compatível
       │                                                   │
       ▼                                                   ▼
ambiente de teste → verificações de saúde → ativação gradual → observação
                                                              │
                                              falha ────────┴→ rollback/incidente
```

## Casos limite e erro

- **Migração interrompida:** bloquear versão nova, usar marcador de migração e seguir procedimento de recuperação; nunca reexecutar às cegas.
- **Disco quase cheio:** parar ingestão não essencial, alertar e preservar base/auditoria; não apagar dados arbitrariamente.
- **Falha total da VPS:** restaurar snapshot verificado, rodar segredos se necessário e reconciliar chamadas externas pendentes.
- **Relógio incorreto:** bloquear operações que dependem de expiração/assinatura até sincronização segura.

## Justificação

Uma VPS é adequada para a fase inicial porque reduz custo e superfície operacional. A disciplina de imagens imutáveis, cópias verificadas e procedimentos de incidente torna possível crescer sem perder controlo.

## Expansões futuras

Alta disponibilidade regional, segregação de dados, infraestrutura como código, recuperação entre regiões, gestão de configuração centralizada e testes de caos.

