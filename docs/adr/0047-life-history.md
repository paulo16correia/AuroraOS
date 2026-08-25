# Design 0047 — Life history

**Status:** Implemented · **Date:** 2026-08-26
**Implements:** `docs/038-life-history.md`

## The line this holds

RFC 038's objective ends with what it must not do: **not replace the audit journal, and not turn
inferences into autobiography.** A collection of memories is not automatically a narrative identity,
and the gap between those two is where a system starts telling itself a story about who it is.

So an episode is proposed against evidence, checked before it is ever narrated, and rendered as a
record *or* as a reading of one — never as a sentence that reads like both.

## Evidence has to resolve

Rule 1 asks for auditable evidence and a date. Not a field that holds a reference: a reference that
**actually resolves in the audit journal**. `VerifyAsync` looks up every one, and an episode whose
evidence is missing cannot be verified.

That check is the whole difference between a history and a story. Without it, "Aurora ran its first
capability" is a sentence somebody wrote; with it, it is a claim anybody can go and check.

Proposing is not remembering. Every episode starts `CANDIDATE`, and a candidate is never narrated.

## A record and a reading of one are different objects

Rule 2 asks the narrative to distinguish confirmed events from interpretative summaries. The only
way to keep that true under editing is to make them **different lines** rather than different
paragraphs: a `NarrativeLine` with an `EvidenceRef` is what the journal recorded, and one without
is what somebody made of it.

A reader can tell which is which without being told. Interleaving them into prose would put the
distinction in the wording, where the next edit removes it.

## Not enough evidence is an answer

RFC 038's limit case gets its own method, because it is the interesting behaviour rather than an
edge: asked when it first made a mistake with nothing to ground the answer, Aurora reports
**insufficient evidence** rather than choosing the episode that best fits the question.

An arbitrary answer to a question about one's own past is not a small error. It is the first
sentence of an invented autobiography, and every later one is consistent with it.

## The text is correctable; the journal is not touched

Rule 3, and it is structural rather than careful: nothing in `CorrectAsync` can reach the audit
store. The summary changes, the evidence does not, and every change writes a revision with its
actor and reason. A test corrects an episode and then verifies the audit chain, which would break
if a single record had moved.

A retracted episode leaves the narrative and keeps its trail. The record of having believed
something about oneself is part of the history even when the episode is not.

## Withheld, not paraphrased

Rule 4. An episode above the audience's ceiling is left out entirely, and the narrative **declares
that a gap exists** without describing what is in it. A redacted episode still discloses that
something happened — the same reasoning as the event stream in ADR 0030, and the same answer.
