# Design 0082 — A media path that can say it died

**Status:** Implemented (detection only) · **Date:** 2026-09-08
**Rests on:** `docs/adr/0068` (voice transport), `docs/adr/0080`/`0081` (voice refusals and the circuit)

## What happened

Aurora joined a voice channel at 22:55 and was still in it at 01:21. In between:

| | |
| --- | --- |
| Gateway | connected the whole time |
| Participant events | arriving until 00:21 |
| `in_call` | `true` |
| `e2ee_ready` | `true` |
| Last transcription | **23:01** |
| RTP received in a 30s sample | **0** |
| RTCP received in the same sample | **0** |

The media socket had carried nothing for over two hours. Discord sends RTCP through a call where
nobody is speaking, so zero of *both* is not a quiet room — it is a dead path. Aurora sat there,
visible to seven people, hearing nothing, and — once its conversation window expired — unable to
say anything either. `voice.status` reported a healthy session throughout.

The counters to notice this already existed. Nothing read them.

## Four signals that are not evidence

The reason it went unnoticed is that every health signal Aurora had was about something else:

- **the gateway** is a different websocket, and it was genuinely fine;
- **participant events** arrive over that gateway, so they kept coming from a channel Aurora could
  no longer hear;
- **`in_call`** means a voice state was set, not that packets flow;
- **`e2ee_ready`** means an MLS session was established once, which stays true forever afterwards.

Each is true and none of them says a packet came back. `media_liveness` is the one that does.

## The rule

In `voice_transport.py`, and pure — no socket, no clock of its own, so it is tested at its
boundary rather than by waiting:

```
media_liveness(last_media_ms, now_ms, participants, observing, timeout_seconds)
```

- **Either** RTP **or** RTCP updates `last_media_ms`. RTP alone would be the wrong test: a call
  where nobody speaks legitimately produces none, and reading that as death would report a failure
  every quiet minute. Recorded before the two are told apart, because the distinction matters for
  audio and not for liveness.
- **`observing`** is whether anything is draining the socket. The receive loop only runs while
  Aurora is listening, so with it off no packet would be counted even over a perfect path. Silence
  is then not evidence, and the answer is `unobserved` — not a verdict Aurora has not earned.
- **No participants** is the same kind of nothing: an empty channel produces no media because
  there is nobody to send it.

| State | Meaning |
| --- | --- |
| `healthy` | something arrived within the window |
| `suspect` | quiet for more than half the window, with people present |
| `dead` | quiet for the whole window, with people present |
| `unobserved` | nothing is draining the socket, or nobody is in the channel |

`MEDIA_TIMEOUT_SECONDS` is 45 and lives in exactly one place; `media_timeout_seconds` in the
plugin's settings overrides it. Conservative deliberately — RTCP arrives every few seconds, so 45
is already many missed reports, and the cost of being wrong is telling somebody their call is
broken when it is merely quiet. Recovery needs no cooldown: a packet is evidence whenever it
arrives.

## The watcher

One thread per session, started with the transport and cancelled in `voice_leave` *before* the
transport is closed. The guard is the cancellation — clearing `voice_media_watch` ends the loop —
and it is cleared on leave, so a later session can watch again. It re-reads the session each tick
and stops if it is gone, rather than looping against a torn-down transport. It is a daemon and
sleeps in one-second steps, so it cannot hold shutdown open.

It reports `voice.media_dead` on the transition and `voice.media_recovered` when traffic returns,
carrying what tells a dead path from a quiet call: how long the silence has lasted, the threshold
it was judged against, how many participants were present, the RTP and RTCP counts, and whether
Aurora was listening. Counts and timings only — nothing anybody said. The failure is that packets
stopped arriving, and no transcript makes that clearer.

## What this deliberately does not do

**It does not reconnect.** Detection and recovery are separate decisions, and the second one is
not made here: whether a dead path should be re-established, rejoined, or simply left is a policy
question that deserves its own record. Today Aurora says so and an operator decides.

No capability, kernel path, policy, approval or confinement behaviour changed.
