# Design 0053 — Closing LAW-008, and tidying after a withdrawn RFC

**Status:** Implemented · **Date:** 2026-08-26
**Depends on:** `docs/laws/LAW-008-self-model-identity-integrity.md`, `docs/adr/0045-local-only.md`

## LAW-008 had no compliance test, and one of its controls had no code

`LawComplianceTests` covered LAW-001 to LAW-007 and said so in its own documentation. LAW-008 was
the one law of eight with nothing asserting it — which matters more than an ordinary coverage gap,
because the law's own text says "Tests must prove". An untested LAW-008 is an unmet LAW-008.

Reading its four controls against the code found that three were satisfied and one did not exist:

- **The reasoning interface never reaches identity or persistence.** True already. `IReasoner` takes
  an objective and a catalogue and returns an action id, an input, a confidence and a provenance
  string. Now asserted by reflection, including the shape of `ReasonerProposal` — adding a field
  there is how this law would be broken, so the shape itself is part of the test.
- **A description is validated against a persisted identity before it is made.** Partly. The
  description was derived from the persisted model, but nothing checked the model actually named an
  identity. `DescribeAsync` now refuses a model whose `identity_ref` is empty.
- **The trace registers `SELF_MODEL(USED_FOR_SELF_DESCRIPTION)` with the identity reference.**
  **Missing entirely.** Now written to the audit log on every description, carrying the identity
  reference and the model version — and nothing else, so there is no room for invented content. The
  test asserts that nothing the description itself said appears anywhere in the record.
- **No later argument, message or provider can replace the persisted name, purpose or Genome.**
  True at registration, and now asserted at resolution too: a genome whose `base_identity_template_ref`
  is edited directly in the database is refused when an instance would be created. Checking only at
  registration would let somebody who bypasses `RegisterAsync` through at the one moment that counts.

Each new test was checked by breaking the behaviour it covers and confirming it failed.

## `DescribeAsync` has no caller

Worth writing down rather than quietly fixing: `ISelfModel.DescribeAsync` is implemented, tested,
and reachable from nowhere. RFC 027 defines `Self.describe(access_context)` as an interface and this
satisfies it, but no MCP tool and no API endpoint exposes it, so Aurora has never actually described
itself to anybody. Exposing it is a decision about the MCP surface (RFC 10), not a gap in RFC 027,
and it is not made here.

## The index still listed a withdrawn RFC

`docs/adr/0045` withdrew `docs/12-deployment.md`, but `docs/README.md` — the normative index — still
listed RFC 12 in its table and in the reading-order diagram, pointing at a file that is not there.

It is now listed under **Withdrawn**, with what withdrew it and why, rather than deleted outright. A
number that simply vanishes from an index invites somebody to reuse it, and the same rule the
execution-trace catalogue already states — a number is never reused — should hold for RFCs.

Three code comments cited RFC 12 for the operational half that was deliberately *kept*: health,
metrics, backup, restore verification. They now cite ADR 0045 as well, so a reader following the
reference lands somewhere that exists.
