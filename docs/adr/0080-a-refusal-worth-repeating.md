# Design 0080 — A refusal worth repeating, and one that is not

**Status:** Implemented · **Date:** 2026-09-07
**Rests on:** `docs/adr/0068` (voice, and the retry), `docs/adr/0074` (a voice that needs nobody),
`docs/adr/0072`/`0078` (Windows confinement)

## What happened

Aurora was asked to join a voice channel and, from inside the call, appeared and disappeared six
times in a row. The audit record says why:

```
discord.voice.join did not complete: voice_unavailable
  — Discord closed the voice socket (4017): E2EE/DAVE protocol required
  | after heartbeating -; …; …
```

Two things met. Discord refused the session with **4017** — the channel is end-to-end encrypted and
Aurora had said it could not take part — and the join path **retried the refusal five times**. The
retry is not a bug in itself: `docs/adr/0068` established it because Discord genuinely does refuse
sessions that are valid, and its own client leaves and rejoins between attempts. But the retry's
cost is paid in public. Every attempt sends a voice state update, so every attempt is one more
appearance and disappearance in front of everybody in the call.

Spending that on a refusal that cannot change is the mistake.

## A close code is an answer, and some answers are final

Discord's voice close codes divide cleanly, and the division is about whether *asking again with
fresh credentials* could produce a different answer:

| Retried | Why | Not retried | Why |
| --- | --- | --- | --- |
| 4006, 4009 | the session is one Discord has forgotten — a new one is precisely the fix | 4004 | the token was rejected |
| 4015 | the voice server fell over — another one will answer | 4012, 4016 | Discord does not implement what Aurora offered |
| *no close code at all* | a timeout or a socket that never opened; Discord has refused nothing | 4014 | Aurora was disconnected: moved, removed, or the channel is gone |
| | | 4017 | the call is end-to-end encrypted and this machine cannot take part |

`TERMINAL_CLOSE_CODES` in `voice_transport.py` is that right-hand column. `voice_join` consults it
after a failed attempt and stops, so a settled refusal now costs exactly one appearance in the
channel instead of five — and it still leaves, because not retrying must not turn into staying.

The code is carried **on the exception**, not parsed out of its message. A refusal's close code is
the one part of it that is a fact; matching on the sentence around it would break the next time the
sentence is worded better.

## The E2EE half: what a plugin needs ships with it

4017 is not a setting to route around. It means the people in the call have been told their audio
is end-to-end encrypted, and a client that opted out would make that untrue for all of them.
`dave.py` has always refused to claim a protocol version it cannot back — establishing the call
would have been the worse outcome, because it would have succeeded while being a lie.

So the fix is to *be able to*, not to pretend. `davey` — the MLS implementation, in the same
category as libopus — was installed for the owner, in the per-user site-packages. The confined
plugin cannot read that, correctly and by design: `docs/adr/0072` grants it its own directory and
nothing else of the owner's. `dave.py` already searched `vendor/` beside the plugin first and said
why: what a plugin needs ships with it. It now actually lives there, and the same applied to
`piper`, whose libraries were beside the plugin while the program itself was not — readiness said
`can_speak` because the *owner's* PATH could reach it, which the plugin never could.

Verified by running the readiness check and a full synthesise-and-transcribe round trip under a
stripped environment — `PATH` of System32 alone, no user profile — which is what the AppContainer
gives the plugin: `can_join`, `can_speak`, `can_listen` all true, `e2ee` true, nothing missing.

## What did not change

The retry itself, its five attempts and its backoff, for every refusal that another attempt could
change. No capability, no kernel path, no policy, no approval, no confinement grant. Nothing was
added to the sandbox's reach: the vendored library and the missing program went *inside* the
directory the plugin was already allowed to read, which is the whole point of that grant.
