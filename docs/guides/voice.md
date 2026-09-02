# Voice

**Status:** IMPLEMENTED · TESTED against fakes · **UNVERIFIED** — no call has been made or answered,
no Realtime session opened, no telephone number exists. See `docs/reference/platform-support.md`.

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

## Known gaps

- Nothing has met a real provider, a real number or a real Realtime session.
- Inbound PSTN is unsupported without owner-supplied ingress.
- Discord voice is not yet on the shared session model.
- Audio quality, latency and PT-PT recognition are entirely unmeasured.
- The plugin has no SIP path; whether one is worthwhile depends on measurements nobody has taken.
