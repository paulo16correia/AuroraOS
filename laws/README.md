# Laws of Aurora OS

Laws are architectural invariants. Applies to all RFCs, services, connectors, and model versions. An implementation that violates a Law is non-compliant, even if it has passed functional tests.

```text
Constitution → Laws → Policies → Decisions → Actions
```- The Constitution defines lasting values ​​and limits.
- Laws define verifiable structural invariants.
- Policies implement Laws by context and user.
- No policy can allow a violation of Law.

Each Law must be enforced by at least one architectural control, compliance test and audit log.

- [LAW-008 — Identity Integrity by Self Model](LAW-008-self-model-identity-integrity.md)