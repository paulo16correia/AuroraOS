# Design 0094 — One recogniser, and it is not local

**Status:** Implemented · **Date:** 2026-10-10
**Supersedes:** `docs/adr/0093` (which made this a choice, off by default)
**Narrows:** `docs/adr/0045` (local only) for the voice path
**Rests on:** `docs/adr/0091` (hearing is measured)

## The decision

The owner asked for ElevenLabs and nothing else, for both legs of voice. So the Discord voice path
has one recogniser, it is ElevenLabs Scribe, and **there is no local option left to fall back to.**

`docs/adr/0093` added Scribe beside whisper and spent most of its length on how to keep the local
default safe: off unless chosen, a setting that had to match exactly, a report that said which was
in force. That arrangement is gone. The setting is gone. What `0093` called the cost is now simply
what this plugin does.

## What is given up

**A recording of whoever speaks in the voice channel is uploaded.** Not the owner's voice only —
everybody's, including people who joined a channel and were never told. `0093` named this as the
part that did not sit comfortably and left it unsolved; removing the local alternative removes the
answer that used to exist for anybody who minded.

The `audio_leaves_this_machine` field survives and is the honest record of it. It is `false` only
when there is no key, which means nothing can listen at all — it never means privacy again.

## Why the owner is not wrong

Measured, not asserted (`docs/adr/0091`): sixteen known European Portuguese sentences through the
production audio path, `ggml-small` got a third of the words wrong and `large-v3-turbo` a quarter,
with the large model needing eleven seconds a sentence on this processor.

Then this, in a real channel, which settled it. A 1.2-second clip of the owner speaking was pulled
out of the live pipeline and handed to both recognisers. ElevenLabs returned nonsense; the local
`large-v3-turbo` returned *"A hora, o que eu estou fazendo é o que eu estou fazendo"* — the same
"Aurora" lost to "a hora", then an invented phrase to fill the time. **Both failed on the same
audio**, which is how the fragmentation underneath was found. But it also made the local option
look like what it was: not a safety net, a worse recogniser that happened to run here.

Keeping a second code path for something nobody would choose is not caution. It is two things to
maintain and one of them untested in anger.

## What went with it

All of it existed only because whisper.cpp did:

- **The initial prompt.** A whisper feature — text that conditions the decoder as though it came
  before the audio. ElevenLabs takes no such thing.
- **`is_prompt_echo`.** It existed because whisper returned its own prompt when it heard nothing
  (`docs/adr/0091`). With no prompt there is no echo.
- **`is_foreign_script`.** It took the expected alphabet from the prompt, and what it was written
  for — a Cyrillic transcript in a Portuguese channel — was whisper's temperature fallback
  inventing. Neither thing exists here now.
- **`stt_model`, `stt_threads`, `stt_audio_ctx`, `stt_vocabulary`.** Parameters of a program that
  no longer runs in this path.

The functions stay in `plugins/voice/speech.py`, which the voice plugin still uses with whisper.
What was removed is this plugin's use of them.

## The audio goes up as it arrived

Worth its own line because it was a real defect, found while doing this. The uploaded audio was
being passed through `_wav`, which decimates 48kHz to 16kHz — a conversion that exists, as its own
docstring says, because whisper.cpp will take nothing else. Scribe accepts the original. Two thirds
of the samples and everything above 8kHz were being thrown away before upload, and that band is
where the difference between "Aurora" and "a hora" lives.

`_wav_as_received` folds the two channels to one and changes no rate.

## What this does not fix

Nothing here makes a fragment transcribable. The same investigation showed turns arriving with
60ms, 400ms and 1200ms of audio in them, and a better recogniser does not help a clause with its
first word missing — it only invents more fluently. That is the turn boundary's problem and it is
being worked on separately.

And this is now a plugin that cannot hear at all without a network and a paid account. That is a
real loss of independence for a project whose first promise was local-only. It is the owner's
machine and the owner's call, made twice; it is written down here so that nobody has to guess
whether it was deliberate.
