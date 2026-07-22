# LAW-005 — Every state has an owner, life cycle and border

## Statement

Each persistent or temporary object must declare who owns it, where it lives, how it is born, how it evolves, how it ends and who can read/write it.

## Verifiable control

- Domain model requires `tenant_id`, origin, state, version, retention/TTL, and access policy.
- Tables, caches or queues without an owner and retention agreement are not allowed.
- Temporary objects expire; Persistent objects have review, archive, or purge strategy.

## Justification

This Act prevents “orphan status,” the primary mechanism by which agent systems become impossible to properly debug or erase.