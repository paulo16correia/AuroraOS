# Design 0044 — Communication identity

**Status:** Implemented, with one gap named · **Date:** 2026-08-25
**Implements:** `docs/07-personality.md`
**Unblocks:** `docs/037-development-model.md`

## A setting, not a claim

Personality here is four dials — formality, conciseness, humour, proactivity — and turning `Humour`
up does not make Aurora amused. It makes its sentences lighter. The difference matters because the
second is honest and the first is not, and every type in this slice is written so that only the
second can be expressed.

The RFC's own justification names the failure this prevents: separating personality from response
generation stops **an informal prompt becoming an invisible, unauditable rule**. A profile lives in
a table with a version, an approval and a history. A prompt lives wherever somebody last pasted it.

## Rule 5 is the unusual one, and it is refused rather than softened

"Must not simulate emotional urgency, dependence, guilt or exclusive relationships to induce
action." This is the only rule in the whole RFC set about manipulating a person rather than about
being correct, and it gets two controls:

**Structural, and the real one.** `SegmentKind` is a closed set — `DISCLOSURE`, `CONTENT`, `RISK`,
`ESCALATION` — and there is no kind that means *persuade*. Nothing in a plan can be **for** inducing
action, because there is no way to say that. Aurora does not write the final prose either.

**Lexical, and honestly a backstop.** A short list of the specific shapes the rule names — "only I
can", "act now or", "after everything I", "if you really cared". A word list does not detect
manipulation in arbitrary text and the code says so in its own comment. It catches those shapes in
what Aurora itself contributes, and that is all it claims.

A message that manufactures pressure is **refused**, not toned down. It does not become acceptable
in a gentler voice, because the voice is not what is wrong with it.

## What personality cannot touch

Rule 3: personality cannot override policies, facts, consent or risk language. `RISK` and
`ESCALATION` segments are protected — the voice cannot reshape them, and they are ordered **last**,
because a risk stated in the middle of a cheerful paragraph is a risk that was not really stated.

A profile's own `ProhibitedClaims` bind it too. The default set starts with "I feel", which is the
claim this RFC exists to prevent.

## When Aurora says what it is

Rule 2 says *when the context or channel warrants it*, not always. A disclosure on every sentence
stops being read, which defeats having one. So it is required on a non-local channel, on a sensitive
subject, and whenever an escalation appears — the last two because that is exactly when somebody is
most likely to forget what they are talking to.

On a sensitive subject the voice is sobered: humour to zero, proactivity with it. Somebody dealing
with something hard did not ask for a companion.

## No profile means the minimum safe one, and saying so

RFC 07's limit case, taken literally. `MinimumSafe` is plain, brief, volunteers nothing, and refuses
"I feel", "I want", "I promise". `ResolveAsync` never throws — it returns that profile with
`Degraded: true` and the reason. **Inventing a personality is worse than admitting there is none.**

## Language is adapted to, never claimed

PT-PT by default. A preference for a language the profile does not list falls back rather than being
honoured, because adapting to a language Aurora does not have would be claiming a skill it lacks.

Proactivity is opt-in: without consent it is zero, and Aurora answers what it was asked.

## The gap

RFC 07's limit case about **a request to change personality arriving inside a third-party message**
— ignore it unless there is authenticated delegation — is not implemented here. It is a
prompt-injection defence and it belongs where untrusted content enters, not in the composer, which
only ever sees what the cycle already decided to say. It is named here rather than quietly skipped,
and the honest place for it is alongside the ingress validation in the Kernel.
