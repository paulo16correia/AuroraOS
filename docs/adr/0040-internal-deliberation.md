# Design 0040 — Internal deliberation and explainable synthesis

**Status:** Implemented · **Date:** 2026-08-25
**Implements:** `docs/025-internal-deliberation.md`

## The separation is the whole design

Two artefacts, deliberately not one:

- **`DeliberationState`** — how Aurora worked. Bound to a cycle and a deadline, and carrying a
  `trace_ref` to protected technical material.
- **`Thought`** — what Aurora can say about it. Reason, sources, next effect.

Keeping them apart is what lets the second be shared without the first. RFC 025 makes that argument
on privacy grounds, and it is also the honest one: **a transcript of intermediate reasoning is not
an explanation**. Treating one as the other designs the product around a non-deterministic process
instead of around something that can be checked.

## The trace can be asked about, never read

There is no method on `IDeliberationService` that returns a trace. Not by convention — by shape:
`TraceAvailableAsync` returns `bool`, and a reflection test asserts that *anything* whose name
mentions the trace returns only a boolean, and that no method returns a bare string at all.

Asking whether the trace is still there had to remain possible. RFC 025's limit case says a decision
whose trace is gone stands only if its sources and policy are recoverable without it, so a caller
has to be able to find out which case it is in. The rule is not "nothing mentions the trace"; it is
"anything that does answers yes or no".

The material itself is AES-256-GCM at rest under **its own key**, not the vault's. They protect
different things for different reasons and last for different lengths of time; sharing one would
mean a trace kept for a week and a secret kept indefinitely stand or fall together. The
deliberation's id is the associated data, so a trace cannot be moved between deliberations and still
decrypt. It is **replaced rather than appended** — rule 4 says minimise, and an append-only trace is
the opposite of minimising — and discarded after seven days. What survives is the `Thought`, which is
the part that was ever meant to.

## Rule 3, and why the explanation is composed rather than written

"The system MUST NOT state 'I am thinking' as evidence of work." A free-form explanation field is
exactly where that sentence gets in, so there isn't one. `user_explanation` is built from three
stated parts — because, sources, next — and the shape has no clause for a claim about ongoing
internal activity. A test checks for the obvious phrasings, but the structure is the actual control.

## Claims without evidence stay hypotheses

`Assertion.IsHypothesis` is `EvidenceRefs.Count == 0`, and that is not an error state. The rule is
that such a claim stays a hypothesis, not that it may not be made — Aurora reasons with unsupported
guesses like anything else does. What must not happen is one quietly becoming part of the answer, so
summarising carries every unsupported claim into the `Thought`'s uncertainty, where a reader can see
it was never established.

## Ending honestly

`CONCLUDED` is refused while questions remain open, and `INCONCLUSIVE` is refused when none do. Both
directions matter: reporting a dead end as a conclusion is the first failure, and calling a finished
piece of work inconclusive is a different kind of dishonesty. An inconclusive deliberation leaves
its questions behind, which is what makes RFC 025's `ASK`/`WAIT` limit case actionable.

Phases run forward only. Deliberation that can revisit any phase at will has no shape, and a record
of it explains nothing about the order things were considered in.

## Wired in, not sitting beside

Every MCP call now deliberates: `KernelDispatcher` opens a deliberation, records what it resolved
and what it recalled, decides, summarises, and closes — and the cycle's Decision stage carries the
thought's id alongside the decision's. `GET /v1/cycles/{id}/why` returns those explanations.

That is the point of the whole step. Before this, Aurora could show *that* it decided something.
Now it can say why, from its own record, rather than from a model asked afterwards to reconstruct a
reason that sounds right.
