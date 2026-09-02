# Design 0073 — One voice, across channels

**Status:** Implemented, unverified · **Date:** 2026-09-02
**Rests on:** `docs/adr/0045` (local only), `docs/adr/0067` (plugins that hold a connection),
`docs/adr/0071` (an integration does not make Aurora networked)

## The decision

Aurora gets a **voice presence**, not a phone bot. One session model, one grant model, one audit,
one identity — with phone, Discord and eventually Teams as transports of the same entity.

The alternative was three voice systems that happen to share a name: a Twilio integration with its
own session handling, the Discord voice code with its own, and a Teams one later with a third. They
would each have a personality assembled from whatever the author had to hand, and the operator's
stop would reach whichever ones somebody remembered to wire it to.

## Identity is not written in the voice layer

`VoiceIdentity.Compose` builds the interaction layer's instructions from `PersonalityProfile` — the
name, the values, the interaction rules, the prohibited claims, the disclosure, and the tone dials
already in RFC 07. **Nothing about who Aurora is originates in the voice code.**

This is the failure mode worth naming, because it is quiet and it is easy: somebody writes a good
prompt in the phone adapter — warm, curious, direct — it works, and from then on there are two
Aurora personalities drifting apart every time one is edited and the other is not. A test composes
instructions from an *empty* profile and asserts that what survives contains no invented character;
what is left is only the arrangement.

What the voice layer does contribute is the part that is about the channel rather than about
Aurora: that speech is a request rather than an instruction, that asking confers no authority, and
that an outcome is never narrated unless Aurora reported it. Those are properties of the
arrangement, not traits of a personality.

The instructions also say what a persona must not become. Aurora speaks naturally, does not preface
answers with what it is, and does not describe itself as an assistant or a model — and it never
claims to be a particular person or invents a body, a family or a life. Natural digital presence,
not human impersonation. When somebody asks directly, it says plainly what it is and carries on.

## Realtime is the interaction layer, never the authority

OpenAI Realtime does speech: recognition, generation, turn detection, interruption, timing. It does
not decide anything.

**The loop runs the opposite way from the obvious design**, and this is the part worth reading
twice. A plugin cannot call into Aurora. The plugin protocol is one-way on purpose, so that a
process holding a connection to somewhere else can report what happened and can never ask Aurora to
do something. Adding a request frame would hand that ability to every plugin in order to give it to
one.

So:

```
speech → interaction layer → "it wants a tool" → reported to Aurora as an observation
                                                          ↓
                                            VoiceToolBridge, inside Aurora
                                                          ↓
                                        session grant, then the Kernel
                                                          ↓
speech ← interaction layer ←  outcome  ←  Aurora calls the plugin back
```

The plugin reports; Aurora decides and acts. That is LAW-003's action–observation loop and LAW-007's
event-mediated communication doing what they were written for, and it is why voice needed no change
to the plugin protocol.

Two gates, in order. The session's own grant first — narrower than the person who started the call,
and refusing there means the Kernel is never asked, so a caller cannot burn a budget probing for
capabilities. The Kernel second: the same Kernel, policy and approval path as every other action.
Nothing in the voice layer can allow what the Kernel would refuse, and a test proves it by running
a real kernel with policy and consent set to refuse.

## A session's authority is held apart from the caller's identity

`VoiceGrant` names the actions a session may ask for, a tool budget, a maximum duration and an
expiry. It is issued when the session is created and **does not grow**. Nothing said during a call
adds to it.

`VoiceAuthorization` is a pure function, and the interesting thing about it is its signature: there
is no parameter for the participant's relationship, none for what Aurora remembers about them, none
for the mission that produced the session, none for the planner. They are absent because they are
not inputs, and a parameter that existed would eventually be read. A test asserts the parameter list
for exactly that reason.

So: memory is not permission, relationship is not permission, familiarity is not permission, and a
caller who is fully authenticated by the channel gets precisely the grant the session was opened
with.

## Calling somebody needs a reason

An inbound call is somebody choosing to speak to Aurora. An outbound call is Aurora appearing in
somebody's day whether or not they wanted it, so it needs an `OutboundCallIntent`: a purpose and an
objective somebody wrote, a target, a grant, constraints, an authorising actor and an **approval
reference**.

No intent, no call. A mission may create a goal and a planner may propose a task, and neither is an
authorisation — which is one branch in the decision and one test. Destinations are an allowlist
where **empty allows nothing**, because empty meaning "anywhere" makes an unconfigured install able
to dial the world. Outbound is off until somebody turns it on, separately from a number existing.

## The stop

One table for every channel, which is what makes the operator's stop reach all of them. It sets the
flag before ending sessions, ends every live one in a single statement, and both halves are audited
in the ordinary chain. A stop that prevented new calls while leaving the current one talking would
be the wrong half of the job.

## What is structurally out of reach, and why it is Aurora's reason

**Inbound calls need an endpoint the provider can reach.** Twilio delivers a call by POSTing a
webhook to a public URL, and its media streams require Twilio to dial a WebSocket. Aurora binds
Kestrel to loopback unconditionally and its security model rests on being unreachable.

This is the same wall that made Microsoft Teams change notifications UNSUPPORTED two days ago, and
the answer has to be the same or one of them is wrong. **Aurora will not become reachable.** What
the plugin does is validate what arrives *if* an owner puts ingress in front of it — a tunnel, a
forwarded port — which is their decision about their network, made outside Aurora, with a plugin
that holds no Aurora keys on the other side of it.

Until somebody does that, phone is outbound-capable in code and inbound-blocked in deployment, and
the plugin has no listener of its own. That is a real gap and it is stated rather than papered over.

## Discord

Not rewritten. Discord voice works, it is the only voice Aurora has ever actually used, and
replacing working verified code with unverified code would be a bad trade. What this design says
about it is that it *fits*: `VoiceChannel.Discord` exists, the session model describes what its
conversation window already does, and the operator's stop is tested across a phone session and a
Discord session together. Migrating it onto the shared store is a separate change with its own
verification, and it has not been made.
