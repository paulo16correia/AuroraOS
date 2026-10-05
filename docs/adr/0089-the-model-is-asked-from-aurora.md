# Design 0089 — The model is asked from Aurora

**Status:** Implemented · **Date:** 2026-10-05
**Supersedes:** `docs/adr/0087` entirely. Restores where `docs/adr/0084` put the model, for a reason
0084 did not give.
**Rests on:** `docs/adr/0045` (Aurora binds loopback), `docs/adr/0084` (a model that only makes
sentences), LAW-002

## What happened

Two days ago `docs/adr/0087` moved the local model from Aurora's side of the plugin protocol to
behind it. The argument was that 0084 justified its placement with the graphics card — a confined
plugin is denied the GPU — and that this does not support the conclusion, because the model runs in
Ollama's process either way and neither Aurora nor the plugin loads weights.

That argument was correct and the conclusion drawn from it was wrong.

**Windows refuses loopback to an AppContainer.** It is written in this repository, in
`AppContainerProfile.cs`, beside the capability that grants network access:

> an AppContainer with this capability still cannot reach 127.0.0.1, because loopback is refused to
> app containers by default

A confined plugin cannot see a runtime on 127.0.0.1 at all. Measured rather than deduced, with
Ollama serving and the plugin installed and running:

```
voice.model → available: false
  "Ollama could not be reached at http://localhost:11434 (timed out)"
```

So 0084 put the model in the right place and gave the wrong reason for it. 0087 corrected the reason
and broke the placement. This record keeps the correction and undoes the move.

## What this costs, and how narrowly

`LocalOnlyTests` fails the build over anything in Aurora that could open an outbound connection, and
had one named exception: the console's health verb, allowed because the address it uses is a literal
and so cannot reach another machine.

There are two now. `OllamaLanguageModel` earns it the same way and is held to it differently,
because which port the runtime answers on is the owner's to choose and a literal would take that
away. So the **host** is checked at construction against a fixed list of addresses that mean this
machine, and anything else is refused outright — not defaulted, because an installation that asked
for a model on another host has said something Aurora will not do, and quietly substituting a
different address would be answering a question nobody asked.

A test holds that, with the cases that matter: `10.0.0.5`, a hostname, `api.openai.com`. If a
configurable host ever gets through, "Aurora reaches nothing" becomes a claim about today's settings
rather than about the code, and the exception stops being earned.

## What went with it

`PluginLanguageModel` is gone, and so are the two capabilities it called — `voice.answer` and
`voice.model` — along with their fifteen tests and `Thinking.identify`. They were written so Aurora
could reach the model through the plugin, they cannot work where Aurora runs, and leaving them would
leave a path that fails for a reason nobody reading them would guess.

What is **not** gone: `thinking.py`, which the voice plugin's own session loop uses. That loop has
the same problem on Windows and no caller yet, so it is dormant rather than broken, and
`docs/guides/voice.md` says so.

## What 0087 got right and keeps

Everything else. The outcome of asking a model is a result and not a refusal — `answered`,
`unavailable`, `timed_out`, `too_long`, `malformed` — because a failed capability cannot explain
itself: the Kernel answers `"Execution failed."` and keeps the reason in the audit, by design. That
reasoning applied to a capability; it applies here too, and `OllamaLanguageModel` returns outcomes
rather than throwing, which is what `ILocalLanguageModel` asked for in the first place.

The direction also stands. The plugin reports and Aurora decides;
`VoiceConversationConsumer` is still the edge where a report becomes a decision, and the model is now
on the same side of the seam as the thing that decides, which is where it always belonged.

## What this means for a conversation

Aurora can now be asked for a sentence on the machine she runs on. That was the last structural piece
missing: the plugin hears and speaks, the boundary decides, and nothing between them is unreachable.

It does not make the conversation good. The transcripts on this machine come back approximate —
"Ponganara da Abelheira" for *Sobral da Abelheira* — and that is the model and the prompt, not the
wiring. See `docs/adr/0090` if one gets written about it.

## What would make this VERIFIED

A sentence travelling the whole path with a person at the other end. The tests drive a real HTTP
server on loopback and a real request, which proves the request is the one the runtime expects and
proves nothing about whether the answer is worth hearing.
