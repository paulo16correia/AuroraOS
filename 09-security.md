# Aurora OS — RFC 09: Security, Privacy and Auditing

**State:** Normative · **Depends on:** RFC 01–08

## Objective

Set controls to protect identity, data, secrets, integrations and decisions. Security is a property of the entire cycle, not a final module.

## Architecture

```text
Channel → authentication → authorization → validation/isolation
                                      │
Data → classification → encryption → minimal access → immutable auditing
                                      │
Tool → policy → vault of secrets → isolated executor → reconciliation
```

## Minimal threat model

| Threat | Main control |
| --- | --- |
| Injection into web/email/documents | Untrusted Content, Isolation, and Off-Model Policy |
| Account or token compromised | OAuth with minimum scope, rotation, revocation and anomaly detection |
| Prompt/log exfiltration | sorting, redaction, vaulting and output filtering |
| Duplicate action | idempotence, state `UNKNOWN` and reconciliation |
| Climbing via SSH/browser | limited capabilities, sandbox and approval by action |
| Memory corruption | provenance, revisions, ACL and tested backups |

## Data structures

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

The audit journal is hash-chained and has write control separate from the application. Contains no secrets or unnecessary full content.

## Interfaces

```text
Auth.authenticate(credential, channel) -> Principal
Policy.authorize(principal, action, resource) -> PolicyDecision
Vault.lease(secret_reference, tool_call_id) -> EphemeralSecretHandle
Audit.append(record) -> AuditRecord
Incident.open(security_event) -> Incident
```

## Mandatory rules

1. Strong authentication MUST protect administration, policy change, and data export.
2. Secrets MUST be encrypted at rest, transmitted only over protected channels and redacted before any recording.
3. All services MUST use their own identities and authenticated communication; There is no shared superuser account.
4. Audit records MUST be retained and protected according to policy; the user can see actions that concern him.
5. High risk incidents MUST revoke affected capacity, preserve evidence, and notify owner.

## Limit and error cases

- **Suspected secret exposed:** revoke/rotate, block dependent calls and open incident; never resend the amount to chat.
- **Integrity check fails:** put auditing in incident mode, prevent write actions and preserve forensic copy.
- **Model provider without data guarantees:** do not send data classified above what is allowed.
- **Unreadable backup copy:** reliability incident; Do not assume recovery until test restore passes.

## Justification

The system has unusually broad attack surfaces — language, data, and external actions. Defense in depth reduces dependence on the model “behaving well”.

## Future expansions

HSM, centralized key management, SIEM, behavioral detection, regular intrusion testing and isolations per client.