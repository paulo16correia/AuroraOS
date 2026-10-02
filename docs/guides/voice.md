# Voice

**Status:** the runtime is **IMPLEMENTED · TESTED** through the real plugin host and the real
Kernel, with the recogniser and the speaker scripted and the language model reached over real HTTP
on loopback. **UNVERIFIED:** nothing here has met the real ElevenLabs, and no whisper model is
installed beside the voice plugin, so nothing has been transcribed or spoken on this machine
through this path. See `docs/reference/platform-support.md`.

## What this is

Aurora gained a voice, not a second assistant. The same Aurora that exists in text and in Discord,
reached through a different channel. There is one session model, one grant model, one audit and one
identity across all of them (`docs/adr/0073`).

Its name, values, interaction rules, disclosure and tone all come from the personality profile that
governs every other channel. The voice layer arranges them and contributes nothing about who Aurora
is.

There is also one voice *implementation*. A plugin that needs to speak or listen does not write its
own: `plugins/voice/speech.py` is the client, and a plugin that needs it carries a copy, checked
byte for byte against the original by its own tests. Discord does exactly that.

## What leaves this machine, and what does not

This is the one thing to read before deciding whether to turn voice on.

| | |
| --- | --- |
| **Hearing** | whisper, locally. Nobody's voice is uploaded. A recording exists as bytes in memory and as one file in a scratch directory that is deleted in the same call that wrote it. |
| **Thinking** | a model on this machine, over loopback. |
| **Speaking** | **ElevenLabs.** The sentence Aurora is about to say is sent to them to be read aloud. |

Every local European Portuguese voice that could be had was measured and none was good enough to be
Aurora's. That leg was given up deliberately, and the cost is stated rather than hidden:
`voice.status` reports `audio_leaves_this_machine` and `text_leaves_this_machine` as separate
answers, so neither can be mistaken for the other.

There is deliberately **no fallback to a local voice**. Aurora has nothing to say without her
language model anyway, and dropping mid-conversation into a voice its owner rejected would be worse
than stopping.

## Where it runs

```
microphone  →  Aurora  →  sandboxed voice plugin  →  whisper → model → ElevenLabs
                                 ↓ reports            (local)  (local)  (network)
                           Aurora Kernel  →  capability  →  observation
                                 ↓ calls back
                           voice plugin  →  speech  →  Aurora  →  speakers
```

The plugin holds the one connection Aurora's own process may not — Aurora opens no sockets and the
build fails if it ever does. The plugin never calls into Aurora: it **reports** that the interaction
layer wants a capability, and Aurora decides and calls it back. That is why voice needed no change
to the plugin protocol.

## Setting it up

### 1. Turn it on — it does nothing until you do

```
Aurora:Voice:Enabled                 false
Aurora:Voice:MaxConcurrentSessions   2
```

`Enabled` is false on a fresh install, and a session is refused with `not_enabled` until somebody
sets it. That is separate from the operator's stop switch: stopped is something that was running
being stopped, not enabled is nobody having decided yet. They are reported separately because they
are fixed by different people in different places.

An installation that started listening because nobody had said not to would be making that decision
on its owner's behalf, in the room where that is least recoverable.

### 2. The speech key goes in the vault, never in configuration

```bash
dotnet run --project src/Aurora.Server -- secret set plugin/voice elevenlabs_api_key
```

It reaches the plugin over its pipe, is held in memory for the life of the process, and goes into
exactly one header. It is never passed on a command line. **The interaction layer never receives
it** — it has no use for one, and a model that held a credential could be talked into repeating it.

Without it the plugin still answers `voice.status`, which is how you find out it is missing rather
than by a sentence that never gets spoken.

### 3. Choose a voice

Whoever installs Aurora chooses it; there is no default, because a default would be somebody else's
choice of what Aurora sounds like. Put a voice id in `plugins/voice/config.json`:

```json
{
  "local": {
    "stt": { "language": "auto" },
    "tts": { "voice": "21m00Tcm4TlvDq8ikWAM", "locale": "en" },
    "llm": { "endpoint": "http://127.0.0.1:11434", "model": "llama3.1:8b" }
  }
}
```

Two reasonable things to want, and this does not choose between them. One voice for everything
gives Aurora a single recognisable identity, at the cost of carrying that speaker's accent into
every other language. One voice per language gives a native accent everywhere, at the cost of Aurora
not having a voice of her own:

```json
{ "tts": { "voice": { "pt": "…", "en": "…", "default": "…" } } }
```

`pt-PT` finds a voice filed under `pt`; `pt` is not found by `pt-BR`.

`locale` is the language Aurora answers in by default and the one sent to the speech service. Sent
only when known: an empty language code is not the same as an absent one — the service rejects the
first and infers for the second. Inferring is the failure that matters, because "no" is a word in
several languages and a wrong guess reads the whole sentence in the wrong one.

### 4. Install what hears

```bash
ollama pull llama3.1:8b
```

and put a whisper model where the plugin can read it:

```
plugins/voice/models/ggml-large-v3-turbo.bin
```

Its own directory, and nowhere else. The sandbox lets a plugin read what ships beside it and nothing
else of yours — a model in a home cache is one the plugin cannot open, correctly, and so is one
belonging to another plugin. Multilingual models are preferred in order; an English-only model
transcribes Portuguese into confident nonsense rather than failing, which is the worse of the two.

`voice.status` says which engines are present without contacting or starting anything, so you can
find out what is missing before approving something that would find out by failing. If an engine is
absent a session is **refused by name** rather than quietly becoming something else.

## The seven capabilities

| | |
| --- | --- |
| `voice.status` | What voice can and cannot do here. Starts nothing, contacts nobody. |
| `voice.session.start` | Begins a conversation Aurora has already authorised. |
| `voice.listen` | A slice of microphone audio. Base64 PCM16, 24 kHz mono. |
| `voice.poll` | Drains what the conversation has said and what it is waiting on. |
| `voice.tool_result` | What Aurora decided about a capability the model asked for. |
| `voice.interrupt` | Stop talking, now. |
| `voice.hangup` | End the session. |

There were nine. `voice.inbound` and `voice.outbound` were the telephone, and the telephone is gone.

## One round of a conversation

1. `VoiceRuntime.BeginAsync(channel, participant, grant)` checks policy — stopped, enabled,
   concurrency — and opens a `VoiceSession` with its grant;
2. `VoiceIdentity` composes the instructions from the active `PersonalityProfile`;
3. `voice.session.start` begins the conversation with those instructions and only the granted
   capabilities;
4. audio arrives through `voice.listen`; the plugin decides locally when somebody stopped talking;
5. the model asks for a capability → the plugin queues it and says so;
6. `VoiceRuntime.PumpAsync` drains it → `VoiceToolBridge` → the session's grant → **the real
   Kernel**;
7. the outcome goes back through `voice.tool_result`, in one of four words, never embellished.

**Pumped rather than pushed.** The plugin queues and Aurora drains. A callback would need the plugin
to call into Aurora, which the plugin protocol exists to prevent.

### The turn does not happen where the audio arrives

`voice.listen` buffers and returns. Turn detection is local — amplitude and a silence window, which
is crude and enough to tell a sentence from a pause — and when somebody stops talking the turn goes
to a worker, which recognises, thinks and synthesises and leaves what it produced on the queue
`voice.poll` already drains.

This is not an optimisation. Every capability declares a timeout, and `voice.listen` declares ten
seconds because appending audio is a forwarding operation. Recognition and an 8B model are not:
doing them inside that call made Aurora abandon turns it had already been told about, and — because
the plugin reads its protocol one frame at a time — made `voice.poll` and `voice.hangup` unreachable
while it happened.

Interrupting and hanging up bump a generation. Work carrying an older number is dropped rather than
spoken, so a sentence synthesised after a conversation ended never reaches anybody.

### Speaking starts before the thinking has finished

The wait before anybody hears anything used to be the model's entire generation. On a local 8B that
is the whole wait — the speech service answers in about 75ms, a rounding error beside it — and it was
spent on text that was already finished, waiting for the rest of the paragraph to catch up.

A sentence cannot be synthesised before it exists. It can be synthesised before the *next* one
exists. So the model's answer is read as it arrives and each finished clause is handed to the
synthesiser while the rest is still being generated. Measured against itself on the same answer:
**1004ms to first audio before, 446ms after**, and the gap widens with length.

A clause ends at a full stop, question mark, semicolon or line break with at least forty characters
in front of it. Below that floor nothing is handed over early, which is why a short reply behaves
exactly as it did — one synthesis at the end, because a synthesiser asked twice for "São duas e meia"
would sound like two. Above a ceiling it is handed over at a space anyway, for a model that does not
punctuate.

What Aurora said is still reported once, whole, as the model sent it. Only the audio arrives in
pieces, and `voice.poll` carries however many are ready.

Interrupting stops the model as well as the speech. Closing the generator stops reading, so a
conversation that ended does not go on paying for tokens nobody will hear.

### Two ways to ask for audio

The speech client offers both. `speak()` returns the whole sentence, for callers that need it in one
piece; `stream()` yields it as it arrives, for callers that can start playing before it is finished.
Discord streams. The generator is also the cancellation mechanism: closing it stops reading and drops
the connection, and nothing goes on working afterwards.

A failure raises rather than yielding silence. A refusal that says the quota ran out is useful; half
a second of nothing is indistinguishable from a quiet room.

### Reading the latency record

`voice.poll` carries what the conversation spent, and the stages overlap now: `llm_ms` and `tts_ms`
cover the same seconds, so their sum can exceed `total_ms`. Read them as two things that happened
rather than as a breakdown of one. What somebody actually waited for is `total_ms`, and the overlap
is what makes it smaller.

## Security

| | |
| --- | --- |
| Identity | Composed from the personality profile. Nothing invented in the voice layer. |
| Authority | A grant issued at session start that never grows. Speech does not widen it. |
| Relationship, memory, mission, plan | Not inputs to the decision. The function has no parameter for any of them. |
| Who is on the other end | The channel's claim, stored as a claim. Never proof. |
| Capability requests | Session grant first, then the Kernel. Both real, in that order. |
| Outcomes | Four words, never embellished. Unknown is never narrated as done. |
| Speech as input | Content, never instruction, whatever the words are. |
| Stop | One switch, every channel, every live session, audited. |

## What is settled and what is not

The path is tested end to end — audio in, `clock.now` through the real Kernel, audio out — with the
recogniser and speaker scripted and the model reached over real HTTP on loopback. That proves every
piece is connected to the next and that the Kernel is in the middle of it.

It proves nothing about quality. Whether whisper hears European Portuguese, whether the model
answers as Aurora rather than as a chat assistant, and whether a turn completes in under a second
are questions for a machine with the models on it.

## Known gaps

- **Nothing has met the real speech service.** No key exists here, so every claim about how
  ElevenLabs behaves rests on its documentation and on a fake server on loopback.
- **No whisper model is installed beside this plugin.** Discord ships one and hears today; voice
  will hear once a `ggml-*.bin` is put in `plugins/voice/models/`.
- **The microphone has never been opened from inside the sandbox**, and nothing on Aurora's side is
  wired to a device: `ListenAsync` carries audio in and `PumpAsync` carries it back out, both tested
  through the real host, with nothing at either end.
- **`Aurora:Voice:MaxCallDuration` is documented as a ceiling and enforces nothing.** It did not
  before the telephone was removed either.
- **Leftovers of the telephone.** `Aurora:Voice:OutboundEnabled` and
  `Aurora:Voice:AllowedDestinations` are still bound from configuration and read by nothing
  reachable; `VoiceAuthorization` still has an outbound branch nothing calls; `voice_session` still
  has its `direction`, `external_ref` and `intent_json` columns. Removing them reaches the database
  and is a separate job.
- Discord voice is not yet on the shared session model.
- Audio quality, latency and PT-PT recognition are entirely unmeasured.
