# Design 0031 — MCP runs through the cognitive cycle

**Status:** Implemented, with one named consequence · **Date:** 2026-08-25
**Implements:** `docs/045-aurora-kernel.md` rule 3, `docs/021-cognitive-cycle.md`
**Step:** 10b of `docs/100-implementation-order.md`

## What was wrong

RFC 045 rule 3 says the Kernel "validates MCP ingress and applies **Mind semantics**, policies,
versioning, isolation, and event delivery before returning a result."

Aurora did the policies and the audit. It did not do the Mind semantics. A tool call went
validate → resolve → policy → consent → execute → audit and returned. Nothing was attended to,
nothing was decided, nothing was observed afterwards, and nothing reflected on. Meanwhile the
cognitive cycle existed and ran — for the conversation pilot, beside the MCP surface rather than
underneath it. Two paths through the same system, one governed and thoughtless, one thoughtful and
unreachable from outside.

## The Kernel now has three phases

`ExecuteAsync` was one method doing nine numbered steps. It is now:

- **`ResolveAsync`** — what is being asked for. Mode check, reasoner, catalog, keyword restriction,
  size, schema. Reserves nothing, permits nothing.
- **`AuthorizeAsync`** — may it run. Idempotency reservation, policy, consent.
- **`CommitAsync`** — run it, and settle exactly once.

`ExecuteAsync` is now the three in sequence, so every existing caller and every existing test sees
identical behaviour.

The split is not tidying. A decision has to know what it is deciding about: the Decision stage
prices an option by the capability's declared risk and effects, and pricing it after policy had
already run would be deciding after the fact. Separating resolution from authorization is what lets
cognition happen between them.

## Who decides what

The cycle decides **what Aurora will do**. The Kernel decides **what it may do**, and the Kernel's
answer is final in both directions: it can refuse an action the cycle chose, and it never runs one
the cycle did not.

When the Kernel refuses, the decision is marked **SUPERSEDED, not COMMITTED**. Recording it as
committed would say Aurora made a call it never got to make.

## The decision is real, and here is where it bites

The obvious failure mode for this kind of work is a Decision stage that always says yes — a record
of a choice that was never available. The engine's own bias makes that easy to spot: it prefers
whichever option reaches nothing outside Aurora, so **asking wins by default** and the interesting
question is when asking is blocked.

It is blocked in exactly two cases:

1. **The caller named the action.** Asking what was meant when it was already said is not caution;
   it is a round trip that answers nothing.
2. **The action reaches nothing outside Aurora.** The answer could not prevent anything, because
   nothing is being changed.

What that leaves open is real and live: an action **inferred** from an objective that reaches
outside Aurora. There, Aurora asks rather than acting on its own reading of what was wanted, and
returns `status: "asked"` — which is not a refusal. Nothing forbade the action, and nothing was
reserved or run.

That branch is unreachable with today's keyword fallback, which is confined to low-risk read-only
actions. It becomes reachable the moment a real reasoner is configured. So it is covered by a test
with a stub reasoner that proposes an effectful capability, rather than left as a claim.

## What a client gets back

`aurora_execute` now returns `cycle_ref`, and `aurora_cycle` reads that cycle back: which stages
ran, which were deliberately omitted and why, what was decided and what was observed. A client is
never asked to take an outcome on trust.

`aurora_converse` exposes the conversation pilot, which already ran the full cycle and had no door
to the outside. It returns references rather than prose, because RFC 021 leaves the wording to the
LLM client and keeps Aurora authoritative for what is true and what happened.

## Known consequence: cycles accumulate

Every tool call now writes a cycle, its thirteen stage records, an attention set and a working
memory frame. The frame is disposed and attention released at the end of each call, so working
state does not grow — but the **cycle and stage history is permanent and has no retention policy**.

That is the correct default for now: the whole point is that what Aurora did can be read back, and
a system that forgets its reasoning on a schedule nobody chose is worse than one that grows. But it
does grow, and on a long-running instance it will need a retention decision. Recorded here rather
than discovered later.
