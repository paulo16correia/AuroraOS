# Aurora Platform — Repository topology

## Decision

Separate specification, Kernel and Applications to prevent implementation or connectors from silently changing the architecture.

```text
aurora-spec
RFCs, Laws, Constitution, ADRs, compliance tests and contract baseline

aurora-kernel
  Aurora Kernel, Mind Manager, State Manager, Event Bus, Policy, auditoria,
published contracts, migrations and reference implementation

aurora-apps
Applications/providers: email, Discord, browser, GitHub, calendar, etc.
Each provider depends only on the SDK/contracts published by the Kernel
```

## Versioning rules

- `aurora-spec` publishes baseline versions and contracts; is a normative source.
- `aurora-kernel` declares the minimum/maximum supported spec version.
- `aurora-apps` declares the SDK/Capability version and does not import Mind's internal modules.
- Incompatible change runs through `ADR → spec → kernel SDK → apps`, with compatibility tests.

## Current status

This repository is `aurora-spec`: the RFCs and normative documentation live in the root, while the grouped documents live in their own directories (`adr/`, `laws/`, `governance/`, `reviews/` and `execution-traces/`). `aurora-kernel` is maintained as an independent repository. This organization is structural and does not change the frozen baseline.