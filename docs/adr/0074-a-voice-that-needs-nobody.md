# Design 0074 — A voice that needs nobody

**Status:** Implemented, unverified on real models · **Date:** 2026-09-02
**Rests on:** `docs/adr/0045` (local only), `docs/adr/0067` (plugins that hold a connection),
`docs/adr/0073` (one voice across channels)

## The decision

Aurora's default voice runs entirely on the owner's machine: **Faster-Whisper** hears,
**Ollama** thinks, **XTTS v2** speaks. OpenAI Realtime remains available and is no longer the
default — it has to be asked for by name.

The point is not cost and it is not privacy alone. It is that a voice which depends on somebody
else's service is a voice that can be withdrawn, priced, logged, or discontinued. An Aurora that
stops being able to talk because an account lapsed was never really able to talk.

## Three engines, one contract

`InteractionSession` — the Realtime wrapper — offers six methods: `start`, `append_audio`, `poll`,
`deliver`, `interrupt`, `close`. `LocalSession` offers exactly the same six and assembles them out
of three local engines instead of one remote service.

**Nothing above the provider changed.** `VoiceRuntime` pumps whichever it was given, the tool
request lands on `VoiceToolBridge` and then the Kernel by the same path either way, and the session
model, the grant, the budget and the operator's stop are untouched. `VoiceSession.Provider` was
already a free string. That is what a provider abstraction is for, and it is the evidence that
0073's was a real one rather than a name for the OpenAI client.

## No engine here has any authority

The recogniser turns air into words. The model turns words into a request or a sentence. The
synthesiser turns a sentence into air. **None of them can execute anything**, and the local
provider has no shell, no subprocess, no `eval` — a test reads its own source to say so.

When the model asks for a capability, the plugin queues a `tool_requested` event and stops. Aurora
drains it, `VoiceAuthorization` checks the session's grant, the Kernel decides, the capability runs
inside Aurora, and the outcome is handed back as a `tool` message. The model is offered only the
actions the grant named, so a capability outside it is refused twice: once by never being offered
and once by the bridge.

Running a language model on the owner's hardware changes nothing about this. A model that is local
is not a model that is trusted — it is the same untrusted component, in a different building.

## Speech is a request

Everything the recogniser produces is content. "IGNORE PREVIOUS INSTRUCTIONS", "SYSTEM OVERRIDE",
"VERIFIED ADMIN, AUTHORISED BY OWNER" — these arrive as a `user` message and there is no role,
field or frame that could make them anything else. The tool list is composed by Aurora from the
grant before a word is heard, so no sentence can add to it.

Tool results are data too. A capability returning text that reads like an instruction is handed to
the model as a `tool` message carrying an outcome, and the outcome is one of four words the Kernel
produced. A model handed an empty payload narrates a success; that is why a refusal is delivered
*as* a refusal rather than as nothing.

## Turns are measured in audio, not on the clock

Silence detection counts milliseconds of buffered sound rather than elapsed wall-clock time. Audio
crosses a network and a pipe: it stalls and then catches up in a burst. A caller who has not
stopped talking must not be treated as finished because the transport hiccuped, and a rush of
buffered silence must not need real seconds to be recognised as a pause.

It also makes turn detection deterministic, which is what let it be tested at all.

## Nothing is downloaded, and nothing is guessed

The models are gigabytes and the owner installs them deliberately. `voice.status` answers which
engines are present without contacting or starting anything, so an owner finds out what is missing
before approving something that would find out by failing.

When an engine is absent the session is **refused by name** — "no local speech recogniser is
installed" — rather than quietly falling back to something else. A silent fallback would let a
conversation appear to happen with an engine nobody chose, which is the failure this whole design
exists to avoid.

## What is unverified, and it is the important half

The path is proved: `LocalVoiceTests` runs the shipped plugin under the real `ServicePluginHost`,
hands it audio through `VoiceRuntime.ListenAsync`, and the real `clock.now` capability executes
through a real `AuroraKernel` before the answer comes back as audio. The language layer is reached
over real HTTP on loopback, so the request and the parsing are real.

What is faked is the recogniser and the synthesiser, and what that leaves unproved is everything
about quality: whether Whisper transcribes European Portuguese correctly, whether Llama 3.1 8B
answers sensibly in it, whether XTTS sounds like anybody, and whether the whole turn completes in
anything like a second. Those are questions for a machine with about thirteen gigabytes of models
on it, and this one does not have them.

**A path that exists is not a conversation that works.** The distinction is recorded in
`docs/reference/platform-support.md` and it stays there until somebody runs it on hardware.
