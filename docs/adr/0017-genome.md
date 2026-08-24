# Design 0017 — Genome (step 5b)

**Status:** Implemented · **Date:** 2026-08-23
**Implements:** RFC 036 · **Baseline:** `docs/adr/0012-specification-baseline.md`

## Signing: ECDSA, not an HMAC

RFC 036 rule 1 requires a genome to be signed, versioned, reproducible and independent of secrets.

A genome is authored in one place and verified in many, so the signature is **ECDSA P-256** rather
than a keyed hash: an installation must be able to check a manifest without holding anything that
would let it forge one. An HMAC would give every verifier the power to mint genomes.

P-256 comes from the BCL, so verification behaves identically on Windows, macOS and Linux. A real
deployment ships only the public key; the private key file exists so a genome can be authored and
tested locally.

Verification checks **both** halves: the integrity hash proves the fields are unaltered, the
signature proves they came from the author. Checking one alone leaves the other forgeable. Tests
cover a tampered field, a foreign signature, and a row edited directly in the database after
registration.

## Overrides may restrict, never relax

`ValidateOverride` returns `ALLOW`, `DENY` or `REVIEW`:

- `constitution_version`, `law_set_version` and `mind_schema_version` are **always denied**. RFC 036
  is explicit that a variant may restrict capabilities or policies and may never relax the
  Constitution, the Laws or a security guarantee.
- `allowed_capability_ids` is allowed when it is a subset, denied when it would grant something the
  genome does not carry — and the refusal names which.
- `policy_bundle_refs` is allowed when it only adds, denied when it removes.
- Anything else is `REVIEW`. The resolver does not guess at whether an unfamiliar field is a
  restriction; a person decides. Denying outright would block legitimate variants, and allowing
  would be exactly the silent relaxation the rule forbids.

A denied override does not fail the resolution. It is recorded in `denied_overrides`, so the
installation still starts and the refusal is visible rather than silent.

## Only a RELEASED genome births an instance

`DRAFT` and `RETIRED` are refused at resolution. The RFC gives the status field and requires
versioned, reproducible manifests; allowing a draft to create an instance would make "which genome
is this installation running" unanswerable.

## Bootstrap degrades, never substitutes

Capability availability is checked against the live registry. All present is `READY`, some missing
is `DEGRADED` with the missing ones named, none present is `BLOCKED`.

RFC 036 says an unavailable capability blocks or degrades the bootstrap and **does not invent a
replacement**. A substitute nobody asked for is worse than a smaller instance, because the
installation would then be running something other than what its genome says.

## Reproducibility

The same genome and the same installation context produce the same `effective_hash`, while each
resolution keeps its own id. A test asserts both: the hash is what makes a resolution comparable
across machines, and the id is what makes each one auditable.

## Tests

18 conformance tests covering sealing and verification, tampering, foreign signatures, unsigned
registration, post-registration edits, the three fixed fields, capability restriction and widening,
policy removal, unknown fields going to review, resolution recording refusals, draft genomes
refused, hash reproducibility, and the three bootstrap outcomes.

## Deferred

RFC 036's own future expansions: a genome catalog, declarative composition of variants,
compatibility tests and promotion between environments. Also rule 3 — recording the effective
genome in Mind State and Life History — which lands with Mind State in step 5c, and rule 4's
migration plan for incompatible genome changes.

## Next

Step 5c: Mind State — capture, verify, restore.
