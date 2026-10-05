# Design 0087 — The model is reached through the plugin

**Status:** **Superseded by `docs/adr/0089`** · **Date:** 2026-10-02

> The argument below about the graphics card is sound and the conclusion drawn from it is not.
> Windows refuses loopback to an AppContainer, so a confined plugin cannot reach a model on
> 127.0.0.1 at all — measured, with Ollama serving. The model is asked from inside Aurora again. What
> this record got right and 0089 keeps: an outcome is a result and not a refusal, and the plugin
> reports while Aurora decides.
**Supersedes:** the location `docs/adr/0084` chose for the model. Its contract, its boundary and
everything it says about what a model may not do stand unchanged.
**Rests on:** `docs/adr/0045` (Aurora binds loopback), `docs/adr/0073` (one voice, one direction),
`docs/adr/0084` (a model that only makes sentences), LAW-002

## What was wrong

Every part of holding a voice conversation existed and nothing was joined to anything.

`VoiceConversationBoundary` — which checks whether Aurora may speak, prepares the only context a
model will see, and validates what comes back — was written, tested, and **registered by nothing**.
The Discord plugin reported `voice.wants_to_answer` into the event bus and **no consumer subscribed
to it**. `VoiceRuntime` was a singleton in the container that nothing resolved. There was no API
route, no verb, and no device at either end.

So the only thing that had ever held a voice conversation in this repository was the test suite. That
is not a gap in a feature; it is a feature that does not exist, described by tests that pass.

The reason it stayed that way is one open question, and this record answers it.

## The question

`ILocalLanguageModel` shipped empty. `docs/adr/0084` explains why — the runtime is a deployment
decision and the architectural one should not wait for it — and says where the implementation would
go:

> The model is not in the plugin. It sits on Aurora's side of the seam, where the card is available.
> … The voice plugin gained no permission and no network access — it does not talk to a model and
> never will; Aurora does.

Two things happened after that was written. The voice plugin gained exactly what that sentence says
it never would: `thinking.py` holds an HTTP connection to Ollama, streams the answer clause by
clause, and is tested. And `LocalOnlyTests` continued to fail the build over an `HttpClient`
anywhere in Aurora, with one documented exception — a liveness probe at a literal `127.0.0.1`.

Implementing the seam where 0084 put it therefore meant carving a second exception into the rule that
Aurora reaches nothing, for an endpoint that is **configurable**, which is precisely what that rule
is about. And it meant a second implementation of "ask the model" beside the plugin's.

## Why the reasoning in 0084 does not reach its conclusion

The justification is the graphics card: the plugin is confined and denied the GPU, so a model held by
a plugin could not use it.

The model is not held by either of them. It runs in Ollama's process, started by the owner, outside
Aurora and outside the sandbox. Neither Aurora nor the plugin loads weights; both would make the same
HTTP request to the same port. **Which process makes that call has nothing to do with which process
can see the card.** The premise is true and does not support the conclusion.

What the choice does decide is whether Aurora's own process opens a connection.

## The decision

`PluginLanguageModel` implements `ILocalLanguageModel` by calling the voice plugin through the
Kernel: `voice.answer` for a sentence, `voice.model` for what is actually loaded.

The seam itself anticipated this. Its own words:

> Deliberately unaware of any runtime. Whether the answer comes from a process on a loopback port,
> an in-process library or a stub in a test is a deployment decision.

A plugin is a deployment decision. Nothing in the contract changes, nothing the boundary does
changes, and the four properties 0084 made structural are untouched: the window is still the
authority and still checked first, what people said still travels as `ConversationTurn` values and is
never joined to the instruction, the answer is still validated before it can be spoken, and the only
thing the model's output can become is words.

What this buys:

- **Aurora still opens nothing.** `LocalOnlyTests` needs no new exception.
- **One implementation of asking the model.** The plugin's, which already streams and is tested. Two
  would have been two places for an answer to come back wrong, which is the defect this project spent
  a day removing from voice.
- **Only text crosses the capability path.** The alternative that also unified the model — routing
  Discord's audio through the voice plugin — would have put base64 PCM through the Kernel with a
  ~100ms floor per chunk, and taken half the work from a transport that has met real Discord.

## An outcome is a result, not a refusal

`voice.answer` returns `{outcome, text, detail, model}` rather than refusing when the model cannot
answer. This is not a style preference; it is forced, and finding out why corrected the first
implementation.

A capability that fails cannot explain itself. The Kernel answers a failed execution with
`"Execution failed."` and keeps the reason in the audit — deliberately, so a caller cannot read a
plugin's internals out of a refusal. The first version of `PluginLanguageModel` tried to tell an
unreachable runtime from a slow one by matching substrings in the error message, and it was matching
against a string that never arrives.

So the words come back as data: `answered`, `unavailable`, `timed_out`, `too_long`, `malformed` —
the outcomes `LanguageModelOutcome` already names. An outcome Aurora does not recognise is `Failed`
rather than a guess, because a plugin inventing a word is a plugin Aurora should not act on. This is
the same rule the contract states for itself: *"an unreachable runtime, a timeout, a cancellation and
an oversized answer are all results, not exceptions."*

`IdentifyAsync` keeps the job 0084 gave it, and it matters more now: a runtime that has unloaded its
model takes about a minute to come back, five times the boundary's own timeout. `voice.model` asks
Ollama what it is holding rather than reading a name out of a settings file, because a recorded name
says what somebody intended to run.

## What joins the halves

`VoiceConversationConsumer` subscribes to `PluginObservationReported` and acts on the ones whose kind
ends in `voice.wants_to_answer`. The direction is the one `docs/adr/0073` established and nothing
here bends it: the plugin reports a fact, Aurora decides. The consumer decides nothing — whether a
window is open, whether a model may be asked, and whether the answer may be spoken are all on the far
side of one call.

Aurora choosing silence acknowledges the event rather than retrying it. An expired window, a model
that is not running and an answer that was not usable are outcomes, and retrying an event because
Aurora stayed quiet would make silence into a loop. The refusal travels in the consume result, so a
conversation Aurora declined is legible afterwards rather than invisible.

The Discord plugin's report now carries the pending turns and where they were said. It carried one
transcript, and a plugin that hands over one line has chosen for Aurora which line matters.

## What this deliberately does not do

No proactive speech, no memory writes, no tool use by the model — all still true, and still
structural rather than advisory. The model reached through this path is given **no tools at all**:
`voice.answer` builds its thinking layer with an empty tool list, so there is no function for it to
call and no field in which it could ask.

And it does not make voice reachable by a person yet. A conversation now starts when a plugin reports
that somebody spoke to Aurora in a channel it is already in. Nothing on Aurora's side is wired to a
microphone, and `VoiceRuntime` — the session, grant and audit model for a voice conversation — is
still resolved by nothing. Discord voice does not go through it. That is the next thing, and it is
not this record.

## What would make this VERIFIED

Nothing has run. `plugin/voice` is not installed on this machine, no ElevenLabs key exists, and the
Discord plugin is installed against a system-wide Python that an AppContainer cannot be granted. The
tests drive the whole path with a fake Ollama on loopback, a fake Kernel, and a boundary whose
decisions are real — which proves the pieces are joined, and proves nothing about a sentence reaching
somebody's ear.
