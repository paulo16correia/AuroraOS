# Design 0085 — An encoder window the size of what was said

**Status:** Implemented · **Date:** 2026-09-09
**Rests on:** `docs/adr/0068` (voice), `docs/adr/0071` (the name), `docs/adr/0084` (the local model)

## Where the time actually went

Benchmarking a local model for the voice loop produced a result that was not about the model:
recognition took 3.71 s of a 6.33 s answer — 59% — and the model took 1.01 s. Before choosing
between an 8B and a 3B it was worth asking what those 3.71 s were made of. Whisper says, if you do
not silence it:

```
load time   =  482 ms      per call, every call
encode time = 2042 ms      73% of the work
decode time =  145 ms
total       = 2803 ms      for 1.5 seconds of speech
```

And the encoder's cost does not depend on the speech. Measured at 0.69 s, 1.46 s and 6.56 s of
audio, it was 2149 ms, 2109 ms and 1880 ms. **Whisper's encoder always walks a thirty-second
window** — 1500 context units, fifty a second — however little was said. In a conversation, where
turns are two or three seconds, that is almost entirely padding.

## The window is sized to the utterance

`--audio-ctx` shortens it. `audio_context_for(seconds)` asks for **twice what the audio needs**,
floored at 384 and capped at the full window:

```
ctx = clamp(seconds × 50 × 2, 384, 1500)
```

Every number in it was measured rather than picked.

**Fifty a second** is whisper's own ratio, not an estimate. **Twice** rather than 1.5 because a
six-second clip lost accuracy at 1.5× (10 characters of error against 4 at 2×) while the short
utterances that dominate a conversation did not notice the difference. **Floored at 384** because
256 was measurably worse on audio whisper struggles with — one hard clip went from 21 characters of
error to 9 for two tenths of a second more window — and because below the floor the decoder starts
falling back and takes *longer* than the whole window would have: in the first sweep, two settings
were slower than doing nothing. **Capped** because past fifteen seconds the margin exceeds the
window, and asking for all of it and asking for nothing are the same thing to whisper.

## What it bought, and what it did not

A/B on fixed audio — synthesised once and reused, four runs each way, because re-rendering per run
compares two recordings and calls the difference a setting:

| | whole window | sized window |
| --- | ---: | ---: |
| Median time | 2.98 s | **1.38 s** |
| Spread across clips | 2.91–3.06 s | 1.28–1.46 s |
| Median error (chars) | 8.4 | 8.6 |

**The speed is the result. The accuracy is not a result at all**, and saying so is the point. 8.4
against 8.6 is inside the noise of the method: the same sentence re-rendered by piper and given to
the whole window scored 20 characters of error in one run and 11 in another. What is being measured
there is piper's own variation and whisper's fallback behaviour, not the setting. The honest claim
is: **2.2× faster, with no measurable effect on accuracy either way, on synthetic speech.**

In the full pipeline the perceived answer went from **6.33 s to 3.86 s**, and recognition's share
of it from 59% to 42%.

## What this does not fix

**The name.** "Aurora" still comes back as *Agora*, *A doura*, *A Lora*, *Alora*, *Arola*,
*Ourora* — before this change and after it, at every context size, with the vocabulary prompt
already naming it. `docs/adr/0071` describes the problem and it is untouched here. The obvious
repair is dangerous: *agora* is an ordinary Portuguese word meaning *now*, so rewriting it to the
name would corrupt real speech. If it is worth attacking, the safer shape is fuzzy matching for the
*addressed* decision only, leaving the transcript exactly as heard — and that needs its own
measurement, on real voices, before it goes anywhere near a call.

**The per-call load.** 482 ms goes on loading the model from scratch for every utterance, because
recognition is a program the plugin runs per turn. A resident process would remove it, and the
confined plugin cannot reach a local server: AppContainer blocks loopback without an exemption
this design has not asked for.

## Limitation of every number here

The audio is piper's, not a person's — cleaner than a laptop microphone in a six-person call, and
pitch-shifted, which changes formants. Latency is unaffected by that: audio is audio, and the
encoder does not care what is in it. Accuracy conclusions from synthetic speech are weak, which is
why none are claimed.

`stt_audio_ctx` in the plugin's settings turns the sizing off and restores the whole window, for
whoever wants to check this on real voices.
