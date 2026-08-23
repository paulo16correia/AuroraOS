# Design 0006 — Audit pre-image enrichment (It.3, second increment)

**Status:** Implemented · **Date:** 2026-08-23
**Depends on:** `docs/adr/0005-it3a-audit-hardening.md`, `docs/adr/0004-it1-reasoner.md`

## Why now

The It.0 audit record stored *what happened*: principal, action, input hash,
outcome. That was enough while every action was named explicitly by the caller.

It.1 changed that. Once an untrusted reasoner can choose the action, "what
happened" no longer lets you reconstruct the decision: the record could not
distinguish an action a human named from one a model proposed.

## What is now recorded

`AuditEntry` adds five fields, all optional (a request rejected before resolution
has no risk or `via` to report):

| Field | What it is for |
|---|---|
| `risk` | The capability's declared level at execution time |
| `via` | `explicit`, `reasoner` or `keyword` — who chose the action |
| `decision` | The consent outcome (`auto_low`, `granted`, …) |
| `policy_ids` | Which policy rules decided |
| `reason` | The textual reason for a refusal |

`via` is the most important of the five. It is what lets you answer, months
later, the question that matters when something goes wrong: **was this a human or
the model?**

## The fields are signed, not merely stored

All of them enter the HMAC pre-image. A test exists for precisely this: rewriting
only `decision` and `via` in the database must break verification. Stored outside
the signature, the "why" would be silently forgeable — which would be worse than
not recording it, because it would invite misplaced confidence.

The pre-image now carries a version tag (`v2`) as its first field. A future
addition of fields becomes a recognisable format change rather than an
unexplained verification failure.

## Operational consequence: existing installations

**Changing the pre-image invalidates audit chains written by earlier versions.**
The server verifies the chain at startup and **refuses to start** if verification
fails (fail-closed, by design).

An existing installation upgrading to this version will not boot. That is
acceptable now because the project is pre-production and there are no
installations to protect; it stops being acceptable the moment there are. Before
the first real deployment, one of two things is required:

- verification by pre-image version, keeping `v1` records verifiable under the
  `v1` rule (the version field already exists for this); or
- an explicit migration step that archives the old chain, seals it, and starts a
  new one — never one that deletes it.

The same applies to It.3a, which had already swapped SHA-256 for HMAC.

## Tests

4 new tests: enriched fields covered by the signature (tampering only with
`decision`/`via` breaks the chain); a normal execution recording risk, `via` and
decision; a policy refusal recording its reason; and reasoner resolution being
distinguishable from explicit in the record.

## Deferred

Verification by pre-image version (see above); exporting the log in a signed
format for off-machine storage; retention and rotation.
