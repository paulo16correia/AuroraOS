# Design 0091 — Hearing is measured, not argued

**Status:** Implemented · **Date:** 2026-10-06
**Rests on:** `docs/adr/0071` (the name in the prompt), `docs/adr/0089` (the model is asked from Aurora)

## What was broken

Aurora held a conversation in a Discord voice channel and answered the wrong things. Sometimes she
answered a question nobody had asked. Sometimes she answered in the wrong language.

Every previous attempt to fix recognition on this project — including the ones recorded in the
comments this record corrects — was an argument about what ought to work. There was no way to tell
whether a change made hearing better or worse, so each one was a guess defended by a paragraph.

## The decision

Recognition is tuned against known speech, and no claim about it is kept that has not been measured.

The harness is three pieces that were already on the machine:

- **Piper**, with the European Portuguese voice already in `models/`, says sentences we know.
- The **production audio path** converts them. `_wav` is read out of `discord_service.py` rather
  than reimplemented, so what is measured is what runs.
- **whisper** hears them, and the distance between what was said and what came back is counted.

Sixteen sentences, chosen for what fails rather than what is easy: her own name, the town names,
short questions, times and numbers. Each is measured clean and again at 15 dB of noise, because a
voice channel is not a studio and a number that only holds in a studio is not a number.

The harness lives in the scratchpad rather than in the repository. It needs a 1.6 GB model and
several minutes to say anything, which is not a test; it is an instrument, and it is described here
so it can be rebuilt.

## What the measurements said

Four things, three of which contradicted what the code said about itself.

**The encoder window was costing accuracy.** `-ac` shortens whisper's window for a short utterance,
and it was on by default because it looked like free speed. It is not free: 33.7% word error became
40.8% clean, and 65.5% became 74.1% with noise, to save about a second a turn. A turn Aurora
mishears is a turn she answers wrongly, which costs the exchange rather than a second of it. It is
off, and still available for slower hardware.

**The prompt comes back as the transcript.** This is the one that explains answering questions
nobody asked. whisper takes the initial prompt as text that came immediately *before* the audio, so
when the audio is unclear the likeliest continuation is more of the prompt. Measured: *"Aurora,
estás a ouvir?"* transcribed as *"Falamos de Sobral da Abelheira."* — which is the prompt, spelled
as though somebody had said it. Five of sixteen noisy sentences came back carrying words nobody
said.

The prompt still earns its place: removing it costs twelve points of word error, because it is what
teaches the decoder the names it has never seen. So it stays, and `is_prompt_echo` refuses to
believe it when it comes back. The threshold is on the words the prompt does *not* contain rather
than on a percentage — what makes a transcript real is that it says something the prompt could not
have supplied, and one such word is enough.

**Temperature fallback is where the wrong language comes from.** When a segment fails whisper's
confidence checks it re-decodes at rising temperature, and a decoder sampling at temperature one on
audio it could not read produces a confident invention in whatever language the sampled tokens
spelled. `-nf` turns that off. It changed no word of the transcripts that were already right and
removed most of the time this used to spend.

**The audio path was not the problem.** Worth recording because it was the obvious suspect and it
was innocent: resampling the same speech three ways — straight to 16 kHz, and through the
production 48 kHz path with a crude and with a proper converter — lands within one and a half
points. `_wav`'s box filter is good enough.

## What this does not fix

The model is the ceiling, and the ceiling is low. With everything above applied, `ggml-small` hears
these sentences at 33.7% word error clean and 66.2% with noise. `ggml-large-v3-turbo` hears the same
audio at 23.1% and **32.6%** — half the errors in the condition that matters — and takes 11.6
seconds a sentence on this processor, which is not a conversation. Shortening its window to buy the
time back destroys it (272% at `-ac 200`).

So the large model is affordable only on the graphics processor, and today it is not reachable:
this whisper build carries no CUDA backend, and `GpuGranted` is wired to the macOS sandbox only —
on Windows nothing consults it. That is the next piece of work and it is not this record's.

## Why the harness rather than a recording

Nobody had to be asked to record anything, and the instrument can be rerun against any change
without asking again. The cost is that synthetic speech is not a person: it has no room, no accent
that drifts, no breath. What makes it trustworthy anyway is that it reproduces the failures seen in
the real channel — *"Aurora"* heard as *"Agora"*, the town names dissolving — so a change that helps
here is a change that was going to help there.

A recording of the owner saying these same sentences would be strictly better, and the harness takes
one the moment it exists: the sentences are already written down.
