# Design 0081 — A refusal is not a fault

**Status:** Implemented · **Date:** 2026-09-07
**Rests on:** `docs/adr/0080` (terminal voice refusals), `docs/adr/0068` (voice), RFC 060 (plugins)

## What happened

Aurora was in a voice channel, listening, with a conversation window open. It was asked to say one
sentence. Six people were talking, so the turn-taking rule declined — correctly, and by design:

```
discord.voice.reply did not complete: floor_taken — somebody is speaking; Aurora does not talk over people
```

On the third of those, the plugin was **quarantined**:

```
CIRCUIT_OPEN: 3 consecutive failures
```

Everything after that failed, including `voice.status`, which is why the call looked dead from
outside. Aurora was refused permission to speak by its own circuit breaker, for the offence of
being polite in a busy room.

## The distinction that was missing

`SqlitePluginRegistry` counted every `Ok: false` as a failure. But two very different things arrive
that way:

| | |
| --- | --- |
| **A fault** | the process died, the call timed out, the host never got a frame |
| **A refusal** | the plugin received the call, considered it, and said no |

The circuit exists to stop calling something *broken*. A refusal is evidence of the opposite: the
plugin is running, parsing, applying its rules and answering. `SandboxUnavailable` was already
exempt for the same reason, recorded in that code as "the plugin never ran... quarantine a plugin
for a property of the machine". `floor_taken` is the mirror image — the plugin ran, and did its job.

## How it is told apart

`PluginResult` gained `Answered`, set only where a well-formed frame came back from the plugin
itself (`ServiceProcess`), and the registry skips the failure count for `{ Ok: false, Answered:
true }`.

It is recorded at the point the result is built rather than inferred later from the refusal string.
A plugin can put any text in that field, so a rule that read it would be a rule a plugin could talk
its way around — a misbehaving plugin could return `floor_taken` forever and never be quarantined.
What cannot be forged from inside the plugin is *who built the result*.

Success still zeroes the counter and refusals still leave it exactly where it was, so failures
cannot be laundered: two faults, a refusal, then a third fault still opens the circuit. Both halves
are tested.

## What did not change

The circuit itself, its threshold, and its behaviour on real faults. Quarantine still ends only
because somebody looked and decided — `plugin release` — which is how this one was lifted, after
the cause was found and fixed rather than because time had passed.

## The rest of the same evening

Three other things in the voice path were fixed alongside it, none of them related except by being
found the same way — by running it with people in the call:

- **`tempfile.mkdtemp` cannot be used under the sandbox.** From Python 3.13 it applies its `0700`
  as a real Windows ACL, replacing the inherited one and blocking inheritance, so the plugin
  created its scratch directory and could not write a file in it. Every utterance came back as
  `PermissionError` reported as "not understood" — a recogniser that looked broken and was not.
  `voice_engines._workspace()` inherits on Windows and keeps `0700` on POSIX, where `/tmp` is
  shared and the mode is the protection.
- **The turn watcher was started behind a one-shot guard** whose loop ended when listening stopped.
  Turning listening off and on again started nothing and reported `listening: true` over a plugin
  that could no longer hear anybody.
- **Recognition ran on four threads** whatever the machine had, and with `stt_language` unset it
  spent a pass per utterance detecting a language the owner never changes — and got it wrong, in a
  Portuguese call, as Greek, Turkish and Persian in the space of a minute. Both are settings now,
  and the voice model is chosen by name rather than by whichever file sorts first, which had a
  Brazilian voice reading European Portuguese.
