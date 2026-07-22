# Aurora Platform — 110: Coding Standard v1.0

**Type:** engineering standard · **Depends on:** RFC 040, 050, 090, document 100

## Principles

Code implements contracts; does not create semantics. Names, types, events, errors and logs should allow you to find the RFC/ADR that justifies behavior.

## Mandatory rules

- Initial implementation language: Python with strict static typing; Interfaces and models are typed.
- Structure by domain and layer: `kernel/`, `mind/`, `applications/`, `contracts/`, `adapters/`, `tests/`; never by generic “utils”.
- Events use past: `MindChangeApplied`, `DecisionCommitted`, `ActionObserved`; commands use imperative: `CreateSnapshot`, `ResolveCapability`.
- APIs and events use `snake_case` in payload; types and classes use `PascalCase`; identifiers have suffix `_id`/`_ref`.
- Errors are typed with `code`, `retryable`, `correlation_id` and safe message. Never use generic exception as a contract.
- Logs are structured, written and correlated; do not record full prompts, credentials, or secret data.
- Comments explain non-obvious decision/limit and link RFC/ADR; they do not literally describe code.
- Public interfaces, schemas and migrations are versioned. Incompatible changes require ADR/migration.

## Prohibitions

- Direct cross-layer access, `Any` as contract escape, mutable global state, network calls in domain code, secrets in serialized configuration, and blind retries of external actions.

## Code review

Each review checks: single responsibility, state owner, transition validation, published event, idempotency, applied policy, observability, testing, and compatibility.