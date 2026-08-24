# Design 0014 — Vault abstraction (step 4)

**Status:** Implemented · **Date:** 2026-08-23
**Implements:** RFC 09, RFC 040 (`VaultItem`) · **Baseline:** `docs/adr/0012-specification-baseline.md`
**Implementation order:** step 4 of `docs/100-implementation-order.md`

## Scope

Step 4 is "Policy/Permission Engine and Vault abstraction". The policy engine exists from the first
phase and satisfies its gate — permissions deny by default. This increment adds the Vault.

## Cross-platform by construction

The obvious ways to protect secrets at rest are DPAPI on Windows and Keychain on macOS. Each is
stronger on its own platform and **neither is portable**, so neither is used.

Secrets are encrypted with **AES-256-GCM from the BCL**, which behaves identically on Windows, macOS
and Linux. The key is 32 bytes in an owner-only file, the same pattern the audit chain already uses,
now shared through `LocalKeyFile`. RFC 09 already lists HSM and centralised key management among its
future expansions; the protector takes raw key bytes, so changing where the key comes from touches
one class.

The whole repository was re-checked while doing this: no `ProtectedData`, no `WindowsIdentity`, no
`Registry`, no `DllImport`, and `net10.0` in every project.

`WindowsPrincipalAccessor` was renamed to `LocalPrincipalAccessor`. It always read
`Environment.UserName`, which is portable — the name was the problem, because a class called Windows
in a portable codebase invites Windows-only code into it. The `Principal.WindowsUser` member keeps
the older name for now: it reaches the audit schema, so changing it is a migration rather than a
rename, and it is tracked separately rather than smuggled into this slice.

## The value never enters the domain

RFC 040 states this about `VaultItem`, and the cheapest way to keep it true is to give callers no way
to hold the value:

- `SecretReference` carries provider, locator, purpose, allowed tools, rotation date and status —
  everything needed to decide a lease, nothing that reveals the secret.
- `EphemeralSecretHandle` has no value property and returns no value. It hands the secret to a
  callback for the length of one call, is **single-use**, and clears its buffer on dispose. A leaked
  handle cannot be replayed.
- `ToString()` is overridden to a redacted form, because a handle that can print a secret will
  eventually print one into a log (RFC 09 rule 2: redacted before any recording).

## Leasing

`Vault.lease(secret_reference, tool_call_id)`. The tool id travels alongside the call id, because
`allowed_tool_ids` cannot be enforced without knowing which tool is asking.

Refusals: unknown reference, `REVOKED`, `EXPIRED`, and any tool not on the allow list. An **empty**
allow list grants nothing — fail closed.

`ROTATING` still leases. A rotation in progress must not cut off a credential that is still valid;
the refusal states are the terminal ones.

Every attempt is audited, granted or refused, with the reference id and tool call hashed and the
value absent. A test asserts no audit field contains the secret.

## Status vocabulary

RFC 09 gives `SecretReference` the states `ACTIVE|REVOKED|EXPIRED`; RFC 040 gives the aggregate
`ACTIVE → ROTATING → REVOKED|EXPIRED`. The Domain Model is the authority on aggregates, so its
superset is used. A deployment that never rotates simply never sees `ROTATING`.

## Rotation is surfaced, not enforced

`rotation_due_at` past its date puts a reference in `RotationOverdueAsync` for the operator surface,
and leasing still works. Cutting off a credential because a date passed is its own outage; the
decision belongs to a person.

## Binding ciphertext to its reference

The secret's id is the AES-GCM associated data. Swapping two ciphertexts in the database makes
authentication fail rather than handing a tool the wrong credential — a test does exactly that swap.

## Tests

16 tests: reference carries no value; handle redacts itself; single use; disposal; encrypted at rest;
ciphertext bound to its own reference; tool not on the list refused; empty allow list refused;
unknown reference refused; revoked refused; rotation replaces the value; a revoked secret is not
rotated; overdue rotation surfaced but still leasable; lease audited without the secret; refused
lease audited too.

## Deferred

External providers — the model carries `provider` and `locator`, and only `local` is implemented.
OS keystore or HSM backing, per RFC 09's future expansions. Lease expiry is carried on the handle but
not yet enforced by a background reaper, since handles are single-use and process-local. Publishing
vault lifecycle events on the bus, which belongs with the producer wiring.

## Next

Step 5: Mind Manager, Genome and Mind State with backup and restore.
