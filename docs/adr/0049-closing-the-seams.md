# Design 0049 — Closing the seams

**Status:** Implemented, one seam left open on purpose · **Date:** 2026-08-26
**Completes:** `docs/adr/0041` (beliefs), `docs/adr/0042` (preferences), `docs/adr/0044` (identity)

Three subsystems were built complete and left unconnected, each named at the time rather than
implied. This connects them.

## A personality change is a person's act

RFC 07's limit case: a request to change Aurora's personality arriving **inside a third-party
message** is ignored unless there is authenticated delegation.

ADR 0044 said this belonged at ingress rather than in the composer. On looking, the honest answer
was better: the personality service had **no caller at all**, so the case was closed by absence,
which is not a control. It is closed by construction now — `/v1/personality/preference` and
`/v1/personality/{id}/activate` are behind the operator gate, so the agent that relays such a
message holds a token and not the credential that acts on one.

**Reading stays open to both surfaces.** The agent has to know how it was asked to speak in order
to speak that way; reading was never the half that needed protecting.

## A habit shapes how something is said

RFC 029's `Preference` and RFC 07's `Voice` were two models of the same person that never spoke.
`ResolveAsync` now asks the relationship model for tone preferences and shapes the voice with them.

The important part is the argument it passes: `PreferenceEffect.Presentational`. That is the one
effect an inferred preference may act on without confirmation, and asking under it is the whole of
the distinction — a test takes the same preference, asks under `ExternalCommunication`, and watches
the answer become no.

## A belief makes what it is about more salient

RFC 028's flow ends at "Active Belief → attention", and ADR 0041 stopped one step short. Beliefs now
adjust the score of recalled memories about the same subject, in the dispatcher.

Two decisions worth stating:

**The belief does not become an item of attention.** It is a pattern *across* memories, not one of
them, and putting it in the attention set would make it compete with its own evidence.

**The adjustment is bounded and only ever upward.** A belief that could lower a score would bury the
memory that contradicts it — which is precisely how a belief would stop being reviewable, and the
belief system exists to keep beliefs reviewable.

## Asked twice, deliberately

The dispatcher now asks `SupportAsync` twice: once under `Ordinary` for attention, once under the
action's real purpose for the decision. That is not a missed cache. **Whether beliefs may carry a
decision depends on what the decision is**, so the first answer is not reusable for the second
question — and reusing it would quietly grant an effectful action the answer given to a harmless
one.

## The seam left open

Relationships do not gate what an audience may be told. `LifeHistory.NarrateAsync` takes an audience
and filters by sensitivity, and a relationship could refine that — but on a single-user local Aurora
**there is no second audience**. Building the hook would mean writing a caller to justify it.

It stays open and named. When Aurora first speaks to somebody who is not its owner, that is the
change to make, and the shape is already there in `RelationshipAssertion.AuthorityScope`.
