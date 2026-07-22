# Aurora OS — RFC 12: Implementation, operation and continuity

**State:** Normative · **Depends on:** RFC 09–11

## Objective

Define how to operate Aurora on a VPS in a retrievable and observable way. The first implementation favors simplicity, isolation and tested restoration, not premature complexity.

## Reference architecture

```text
Internet → proxy TLS/WAF → API/UI
                            │
            ┌───────────────┼────────────────────┐
            ▼               ▼                    ▼
PostgreSQL core/worker+secret vault index
            │               │                    │
            ▼               ▼                    ▼
connectors encrypted backups metrics/logs/audit
```

Services are containerized and run with unprivileged users, segmented networks, and minimal persistent volumes. The tool process must have tighter limits than the core.

## Operational data structures

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

## Mandatory rules

1. Production MUST use TLS, controlled DNS, security updates, and administrative access with strong authentication.
2. Each release MUST fix image versions, have a reversible migration or compatibility strategy, and pass checks before receiving traffic.
3. Encrypted backups MUST include data, necessary configurations and schema reference; Restorations MUST be tested periodically in an isolated environment.
4. Metrics, logs and alerts MUST be written; logs do not replace the audit journal.
5. Scaling resources or adding services should only happen with a measured need: load, availability, isolation or recovery.

## Release flow

```text
signed artifact → validation → pre-release backup → compatible migration
       │                                                   │
       ▼                                                   ▼
test environment → health checks → gradual activation → observation
                                                              │
                                              failure ────────┴→ rollback/incidente
```

## Limit and error cases

- **Migration interrupted:** block new version, use migration marker and follow recovery procedure; never rerun blindly.
- **Disk almost full:** stop non-essential intake, alert and preserve base/audit; Do not erase data arbitrarily.
- **Complete VPS failure:** restore verified snapshot, rotate secrets if necessary and reconcile outstanding external calls.
- **Incorrect clock:** block operations that depend on expiration/subscription until secure synchronization.

## Justification

A VPS is suitable for the initial phase because it reduces cost and operational surface. The discipline of immutable images, verified copies, and incident procedures makes it possible to grow without losing control.

## Future expansions

High regional availability, data segregation, infrastructure as code, cross-region recovery, centralized configuration management, and chaos testing.