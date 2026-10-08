# Design 0093 — Listening may leave the machine, if the owner chooses it

**Status:** Implemented, off by default · **Date:** 2026-10-08
**Rests on:** `docs/adr/0045` (local only), `docs/adr/0091` (hearing is measured), `docs/adr/0092`
(the operating system is the sandbox)
**Reverses, in part:** the clause in `docs/adr/0045` that made recognition unconditionally local

## What this gives up

Until now one sentence held in every file that touched voice: *listening is local and stays local.*
A recording is somebody's voice, and sending it would take a private conversation off the owner's
machine without anybody deciding to.

This record adds a recogniser that **uploads the recording**, and the sentence becomes conditional.
That is a real loss and it is written at the top rather than at the bottom.

## Why

`docs/adr/0091` measured hearing instead of arguing about it, and the answer was bad. Sixteen known
European Portuguese sentences through the production audio path:

| | clean | with noise |
|---|---|---|
| `ggml-small`, fully tuned | 33.7% of words wrong | 66.2% |
| `ggml-large-v3-turbo` | 23.1% | 32.6% |
| **ElevenLabs Scribe** (published: FLEURS pt / Common Voice pt) | **3.1% / 5.5%** | — |

Five to seven times fewer errors than the best local option, and the best local option needs the
graphics processor to run in a conversation at all — `large-v3-turbo` takes 11.6 seconds a sentence
on this processor.

A third of the words wrong is not a system that mishears occasionally. It is the system the owner
described as *"toda trocada"*: Aurora answering questions nobody asked, because what came back from
the recogniser was not what was said. Local recognition that does not work is not privacy; it is a
feature nobody can use, and the privacy of a conversation Aurora cannot take part in is not worth
very much.

The price is cents. $0.22–0.40 per hour of audio, so an evening in a voice channel costs less than a
coffee. Money was never the question.

## What was built

`ElevenLabsListener` in `plugins/voice/speech.py`, beside the speaker that was already there, posting
to `POST /v1/speech-to-text` with `model_id=scribe_v2`. The same host the speaker already uses, so
there is **one** place audio can go and one thing to approve, rather than two.

The whisper path is untouched and is still the default. This is a second recogniser, not a
replacement: a machine with a graphics processor and no account should still hear as well as it can.

## How the cost is kept visible

The thing that makes this defensible is not that it is optional. It is that nobody can end up in it
by accident, and nobody has to read source code to find out which recogniser is listening.

- **Off by default.** `stt_provider` is `local` unless the owner writes `elevenlabs`, and a typo
  leaves the private default in place rather than guessing at what was meant. A fresh installation
  that began uploading a channel's audio because nobody had said otherwise would be the same failure
  the default-off posture was built to prevent.
- **The consent text changed, so the approval changed.** The secret's stated purpose used to end
  *"Speech recognition stays on this machine."* It now says the key also uploads recordings when
  `stt_provider` names the remote recogniser. The owner approved a different sentence than the one
  they approved before, which is the point: a secret granted for one purpose should not quietly
  acquire another.
- **`readiness` answers it at runtime.** `audio_leaves_this_machine` follows the setting rather than
  being a constant `false`. That field exists precisely so this question is never inferred from a
  comment, and a test holds that it tracks the setting in both directions.
- **Two function names, not one flag.** `transcribe` and `transcribe_remotely` are separate calls,
  because the two differ in the one way that matters at a call site: one sends a recording of a voice
  to another company. A boolean argument would let that change happen in a diff nobody noticed.
- **The request is built in one file.** `xi-api-key` and the endpoint path appear only in
  `speech.py`; the Discord plugin borrows it rather than writing a second client that could disagree
  about what gets sent. A test holds that too.

## What is still wrong with it, honestly

**The people in the channel did not choose this.** The owner chose it. Aurora sits in a voice channel
with other people, and their voices are in the recording that gets uploaded. Nothing here tells them.
That is the part of this decision that does not sit comfortably, and it is not solved by a setting:
the right answer is probably that Aurora says so when she joins, and it is not built.

**It is one more thing that fails when the network does.** Speaking already failed that way;
listening now does too, so a dropped connection costs the whole exchange rather than half of it. The
local path remains as the thing to switch back to, which is the main reason it was kept.

**Nothing here has met the real service.** The request is built against the published API reference;
no recording has been sent. The published accuracy numbers are ElevenLabs' own, on their chosen
benchmarks, and FLEURS and Common Voice are clean read speech — comparable to the "clean" column
above, not to a noisy channel. The same harness that measured whisper will measure this against the
same sixteen sentences, including the town names, once a key reaches it.

## The alternative, which is still open

`docs/adr/0092` explains why the graphics processor is not reachable from a confined plugin. Making it
reachable would put `large-v3-turbo` at 23.1% and well under a second a turn, with nothing leaving the
machine — proven working on this card, outside the sandbox, at 3.3 seconds including loading 1.6 GB of
model cold. That is still five times worse than Scribe and it keeps the promise this record gives up.

Both should exist. An owner who would rather be misheard than recorded is not making a mistake.
