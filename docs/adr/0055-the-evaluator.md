# Design 0055 — The evaluator RFC 08 asked for

**Status:** Implemented · **Date:** 2026-08-26
**Depends on:** `docs/08-learning.md`
**Found by:** the spot check in `docs/adr/0053` that questioned how RFC coverage had been decided

## What was missing

RFC 08 defines four interfaces. Three existed. `Evaluator.run(proposal_id, test_scope) ->
EvaluationRun` did not, and neither did the `EvaluationRun` type.

The fingerprint was in the code: `LearningProposalState.Testing` was declared and **referenced
nowhere**. A proposal went `PROPOSED → APPROVED → DEPLOYED`, straight past the step whose absence
the RFC's own justification warns about — "separating observation, proposal, testing and
application prevents the silent drift of behavior".

`LearningProposal` was also missing three fields the RFC names: `expected_benefit`, `risk` and
`evidence_refs`. Without `risk` there was no way to tell a low-risk memory change from anything
else, which is the distinction rule 2 turns on.

## The design decision that mattered: PASS is not the default

Aurora runs on one machine with no evaluation corpus and no way to A/B a behaviour change. The
tempting implementation returns `PASS` with plausible-looking metrics, and it would have been worse
than leaving the gap: a fabricated pass gets cited as evidence, by Aurora and by whoever reads the
record.

So each of rule 4's three mandated dimensions reports **whether it could be measured at all**, and
the verdict follows from that:

- **FAIL** — a mandated dimension regressed.
- **PASS** — all three were measured and none regressed.
- **INCONCLUSIVE** — something could not be measured, or moved both ways.

`INCONCLUSIVE` is the RFC's own limit case ("keep in test and require human decision") and it is
the answer whenever Aurora did not look. A dimension it cannot measure is written as *unmeasured*
with the reason, never as a zero — for the same reason `ResourceReading` keeps null distinct from
zero elsewhere in this codebase: "I did not look" and "I looked and found nothing" call for
opposite responses.

Textual quality is deliberately absent from the mandated set. Rule 4 says "not **just** textual
quality", and it is the one thing Aurora genuinely cannot judge locally. Reporting a number for it
would have been the dishonest half of the method.

## What is actually measured

**Security regression** — does a change set widen what Aurora may *do* rather than what it *knows*?
A proposal declared `MEMORY` whose content mentions policy, capabilities, permissions, grants,
connectors or the vault is not a memory change. The check is crude and errs towards finding a
regression that is not there: a false positive costs a human decision, a false negative deploys an
untested policy change.

**Cost** — bounded by the proposal's declared evaluation plan, or unmeasured when it declares none.
Aurora will not infer the running cost of an arbitrary change from its JSON.

**Privacy** — the change set must carry nothing shaped like a credential. This reuses the detector
the plugin registry already had; it was moved to `SecretShape` in Core rather than copied, because
a security check that exists twice is a security check that gets improved once.

## The gate, without which none of it changes anything

`ApplyLearningAsync` now refuses:

- a change that has never been evaluated, unless it is a **low-risk memory change** — rule 2's
  permission, kept deliberately, because a memory is provenanced, revisable and forgettable, so
  getting one wrong is a correction rather than a change in behaviour;
- a change whose last evaluation failed;
- a change whose last evaluation was inconclusive, unless a person says otherwise — an inconclusive
  evaluation that applied itself would be a system deciding it had passed its own test;
- a change with no rollback plan, because reversible is one of rule 3's three conditions and not a
  nice-to-have beside them.

Every run is kept rather than overwritten. Which verdict was current when a change was applied is
the question somebody asks after it goes wrong, and a row that was overwritten cannot answer it.

## Schema

Target version 15. `evaluation_run` is a new table, so it arrives with the DDL. The three columns
on `learning_proposal` come through the idempotent `RequiredColumns` pass, and a database written
before this reads its existing proposals as `HIGH` risk — a change recorded before anybody wrote
down its risk is not thereby a safe one.

## Still open in RFC 08

The limit case "**failure during application:** execute rollback or mark partial status, block new
application and open incident" is not implemented. `RollbackPlan` is now required before a change is
applied, but nothing executes it, and nothing opens an incident. Recorded here rather than left for
the next person to rediscover.
