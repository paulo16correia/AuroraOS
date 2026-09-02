# Voice

**Status:** the runtime is **IMPLEMENTED · TESTED**, and so is the real OpenAI Realtime transport —
driven over a real socket against a Realtime service on loopback, with a real RFC 6455 handshake and
real masked frames. **UNVERIFIED** against the real endpoint: no OpenAI key exists here. Twilio and
a real +351 number remain UNVERIFIED; inbound PSTN remains UNSUPPORTED. See
`docs/reference/platform-support.md`.

## What this is

Aurora gained a voice, not a phone bot. The same Aurora that exists in text and in Discord, reached
through a different transport. There is one session model, one grant model, one audit and one
identity across every channel (`docs/adr/0073`).

Aurora is not a second assistant on the phone. Its name, values, interaction rules, prohibited
claims, disclosure and tone all come from the personality profile that governs every other channel;
the voice layer arranges them and contributes nothing about who Aurora is.

## Where it runs

```
telephone  →  provider  →  sandboxed voice plugin  →  OpenAI Realtime
                                    ↓ reports
                              Aurora Kernel  →  capability  →  observation
                                    ↓ calls back
                              voice plugin  →  Realtime  →  speech
```

The plugin holds both connections, because Aurora's own process opens no sockets and the build
fails if it ever does. The plugin never calls into Aurora: it **reports** that the interaction layer
wants a tool, and Aurora decides and calls it back. That is why voice needed no change to the plugin
protocol.

## Setting it up

### 1. Decide what voice may do — it does nothing by default

```
Aurora:Voice:InboundEnabled          false
Aurora:Voice:OutboundEnabled         false
Aurora:Voice:AllowedDestinations     (empty — allows nothing)
Aurora:Voice:MaxConcurrentSessions   2
Aurora:Voice:MaxCallDuration         00:15:00
```

An empty destination list allows nothing. Having a number is not a decision to ring people with it,
which is why inbound and outbound are separate switches.

For Portugal:

```
Aurora:Voice:AllowedDestinations     +351
Aurora:Voice:Number                  +351XXXXXXXXX
```

An entry is a whole E.164 number or a country prefix. There is no other pattern language, because
one would end up allowing more than whoever wrote it meant.

### 2. Secrets go in the vault, never in configuration

```bash
aurora secret set plugin/voice provider_auth_token
aurora secret set plugin/voice provider_account_sid
aurora secret set plugin/voice openai_api_key
```

They reach the plugin over its pipe. **The interaction layer never receives a provider credential**
— it has no use for one, and a model that held one could be talked into repeating it.

### 3. Outbound calls need a reason each time

An outbound call carries an `OutboundCallIntent`: purpose, objective, target, grant, constraints,
authorising actor and an approval reference. Without one it is refused, and a mission or a plan is
not one.

## What is not possible here

**Inbound calls.** Twilio delivers them by POSTing to a public URL and its media streams need Twilio
to dial a WebSocket. Aurora binds loopback unconditionally. There is no endpoint to give it.

The plugin validates what arrives if you put ingress in front of it yourself — that is a decision
about your network, with a plugin that holds no Aurora keys behind it — but nothing here ships a
listener, and inbound is UNSUPPORTED until you do.

**Joining a Teams call.** Not implemented; the abstraction exists so it can be.

## Security

| | |
| --- | --- |
| Identity | Composed from the personality profile. Nothing invented in the voice layer. |
| Authority | A grant issued at session start that never grows. Speech does not widen it. |
| Relationship, memory, mission, plan | Not inputs to the decision. The function has no parameter for any of them. |
| Caller ID | `claimed_from`. The network carries whatever the originating carrier says. |
| Tool requests | Session grant first, then the Kernel. Both real, in that order. |
| Outcomes | Four words, never embellished. Unknown is never narrated as done. |
| Webhooks | Signature, freshness, replay — in that order, before the payload is read. |
| Stop | One switch, every channel, every live session, audited. |

## The runtime

`plugins/voice/voice_service.py` carries words; `VoiceRuntime` decides. Both exist, and the store,
the policy, the bridge and the runtime are all registered in `ServiceRegistration`.

One round of a conversation:

1. a provider event arrives → `voice.inbound` validates signature, freshness and replay;
2. `VoiceRuntime` checks policy — stopped, inbound enabled, concurrency — and opens a
   `VoiceSession` with its grant;
3. `VoiceIdentity` composes the instructions from the active `PersonalityProfile`;
4. `voice.session.start` begins the interaction with those instructions and only the granted tools;
5. the speech layer asks for a capability → the plugin queues it and says so;
6. `VoiceRuntime.PumpAsync` drains it → `VoiceToolBridge` → session grant → **the real Kernel**;
7. the outcome goes back through `voice.tool_result`, in one of four words, never embellished.

**Pumped rather than pushed.** The plugin queues and Aurora drains, which is how the Discord
plugin's pending turns already work — the one voice design here that has met a real service. A
callback would need the plugin to call into Aurora, which the plugin protocol exists to prevent.

## The speech layer

`realtime.py` speaks the real OpenAI Realtime protocol over a real WebSocket — the RFC 6455 client
vendored from the Discord plugin, which has performed a real handshake against a real service and
been disconnected for masking a frame wrongly.

| | |
| --- | --- |
| Endpoint | `wss://api.openai.com/v1/realtime?model=…` |
| Headers | `Authorization: Bearer …`, `OpenAI-Beta: realtime=v1` |
| Audio | base64 PCM16, 24 kHz mono, both directions |
| Turn taking | `server_vad` — the service decides when somebody stopped talking |
| Interruption | `input_audio_buffer.speech_started` → `response.cancel` |
| Tools | function calls in, `function_call_output` out |

Audio is **appended, never committed**. With server-side turn detection the service decides when a
turn ended, and committing by hand takes that decision back and does it worse.

**Without a key there is no session.** The plugin refuses rather than falling back to a stand-in,
because a stand-in would let a call appear to happen.

### The audio granularity Aurora imposes

A capability is capped at 600 calls a minute, which puts a floor of about **100 ms** under an audio
chunk carried through `voice.listen`. That is the granularity a governed capability can have, and
it costs up to 100 ms of latency. Worth knowing before measuring the conversation and blaming the
model.

## Running the slice locally

```bash
export OPENAI_API_KEY=sk-...
python3 plugins/voice/local_slice.py
```

Needs `ffmpeg` for the microphone and `ffplay` or `afplay` for the speakers; it says which is
missing rather than failing obscurely.

**It is a harness, not a capability.** It opens your microphone, and a program that does that on
somebody's behalf should be one they started. It talks to the real service and plays what comes
back — and it does **not** go through Aurora's Kernel: a tool the model asks for there is answered
with a refusal, because the governed path runs inside Aurora and the harness is outside it.

Use it to hear the voice. Use the test suite to prove the governance.

## Known gaps

- **Nothing has met the real service.** No OpenAI key exists on this machine, so every claim about
  how the real endpoint behaves rests on its documentation.
- **The microphone has never been opened from inside the sandbox.** The harness runs unsandboxed;
  whether `sandbox-exec` and macOS TCC will let a confined plugin capture audio is untried, and it
  is the next thing a real slice would hit.
- **Audio does not yet flow through Aurora end to end.** `voice.listen` and the audio returned by
  `voice.poll` exist and are tested; nothing is wired to a device on Aurora's side.
- Nothing has met a real provider or a real number.
- Inbound PSTN is unsupported without owner-supplied ingress.
- Discord voice is not yet on the shared session model.
- Audio quality, latency and PT-PT recognition are entirely unmeasured.
- The plugin has no SIP path; whether one is worthwhile depends on measurements nobody has taken.
