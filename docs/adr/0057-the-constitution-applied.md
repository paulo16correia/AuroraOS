# Design 0057 — The Constitution, applied rather than quoted

**Status:** Implemented · **Date:** 2026-08-26
**Closes:** RFC 035 rules 1 and 2

## The gap

RFC 035 states eight Articles and two enforceable rules. Until this, it was eight paragraphs of prose
and a version string on the genome: nothing checked a policy against the Articles at publication, and
a high-risk `Decision` carried `PolicyDecisionIds` and `RiskLevel` and no reference to the rules it
had been judged against.

That made the Constitution the one part of the specification that could be contradicted without
anything noticing — which is the opposite of what sitting above the policies is supposed to mean.

## Two things had to change, not one

Writing the checks first produced a Constitution that failed almost every real decision, on two
Articles:

- **Article 2** (declare material uncertainty) failed any decision at middling confidence, because
  the engine recorded uncertainty only on specific paths and a 0.50 confidence with an empty list
  reads as surer than it is.
- **Article 8** (time-limited) failed every effectful decision, because the expiry came from the
  caller's deadline and most callers set none.

Both were real. The wrong fix was to soften the checks until they passed; the right one was to make
the engine satisfy them. It now declares its own confidence as uncertainty when it is below the
threshold, and bounds an effectful decision to an hour when nobody set a deadline — RFC 022 rule 4
says decisions expire, and an effectful one that never does is a standing permission nobody granted.

The check and the thing it checks are in different components, so the assertion is not circular: the
engine declares, the Constitution verifies.

## PASS is not the default, and REVIEW is not a failure

Article 1 is about what a piece of information *is*, and a decision holds references rather than
contents. So a decision that reaches outside Aurora is `REVIEW` on Article 1 — it does not block, and
it says a person should look. A decision that reaches nothing outside is `PASS`, and that is a real
verdict rather than a courtesy: it cannot disclose anything.

This is the same discipline as `docs/adr/0055`. An assessment that returned PASS for what it did not
look at would become a rubber stamp, and the stamp would then be cited as the reason the decision was
safe.

## Where it bites

A high-risk decision — anything effectful, or rated HIGH or CRITICAL — is committed against an
assessment, and a `FAIL` refuses the commit. Not a warning: an Article is not a preference to be
weighed against getting the job done.

Rule 1 has no middle answer at all. A policy that relaxes a Law is refused at publication, because
there is no reviewer to escalate to and no context in which it becomes acceptable. So is one that
cites nothing — a rule nobody can attribute is a rule nobody can revoke — and one that both permits
and denies the same thing, which is less unconstitutional than unenforceable.

The assessment is pure, so one stored beside a decision can be re-derived from that decision and
compared rather than only trusted.
