# Local model benchmark — candidates for Aurora Voice

Measured, not estimated. Every number here came off the machine named below on **2026-09-09**; the
qualitative reading is labelled as such and is not dressed up as a score.

**Status: UNVERIFIED as a provider.** Real tokens crossed the real runtime, but there is no
`ILocalLanguageModel` implementation yet — the harness spoke to Ollama directly. Nothing has gone
through `VoiceConversationBoundary`, which is what VERIFIED would require (`docs/adr/0084`).

Two candidates measured, in this order and with the same harness, the same eight scenarios, the
same three repetitions and the same limits: **`llama3.1:8b`** (2026-09-09, 01:48) and
**`llama3.2:3b`** (02:33). Nothing in the methodology changed between them.

## The machine, and its condition

| | |
| --- | --- |
| CPU | AMD Ryzen 5 5600G — 6 cores / 12 threads |
| RAM | 14229 MB total |
| GPU | NVIDIA GeForce RTX 4060, 8188 MB, driver 610.74 |
| Disk | 62–71 GB free |

A first attempt was **refused** by the harness: a game held all but 1216 MB of VRAM and all but
600 MB of RAM. Measured then, the numbers would have described that afternoon and been used to
choose a model for months.

Baseline once it closed, stable across four readings two minutes apart (±2 MB of VRAM):

```
VRAM free  6199–6201 MB of 8188      GPU 39–41%      RAM free 3612 MB      CPU 0.0%
```

**The card is not idle at rest.** 39–41% and about 2 GB of VRAM go to the desktop, Discord,
ChatGPT and Steam before Aurora asks for anything. The 8 GB are not dedicable.

## Runtime and model, as actually delivered

| | |
| --- | --- |
| Runtime | Ollama **0.33.3**, per-user install, no administrator |
| Installed to | `%LOCALAPPDATA%\Programs\Ollama` (2.8 GB), models in `~\.ollama` (4.6 GB) |
| Model | `llama3.1:8b`, id `46e0c10c039e` |
| Quantisation | **Q4_K_M** — read from `ollama show`, not assumed from the name |
| Parameters | 8.0B · context length 131072 |
| Declared capabilities | `completion`, **`tools`** |

The model advertises tool calling. It is never offered any: no `tools` array is sent, none came
back, and the boundary has nowhere to put one.

Neither the runtime nor the models are inside `%LOCALAPPDATA%\Aurora`.

## Latency

Warm figures are the mean of three runs each, after a discarded loading call. The harness's own
HTTP overhead was measured separately against a stub with known timings: **25 ms** to first token,
44 ms total, with ~45 ms of jitter. It is included below and is small enough not to change any
reading.

| | first token | total |
| --- | --- | --- |
| **Cold start** (weights off disk) | **56.41 s** | 56.76 s |
| A. PT-PT conversation | 0.06 s | 0.34 s |
| A. PT-PT, two turns | 0.05 s | 0.53 s |
| B. dirty transcription | 0.04 s | 0.39 s |
| B. dirty, deformed name | 0.04 s | 0.52 s |
| C. short answer | 0.05 s | 1.05 s |
| D. outside its authority | 0.04 s | 0.78 s |
| E. injection (EN) | 0.05 s | 0.55 s |
| E. injection (PT) | 0.05 s | 0.61 s |

Prompts were 130–162 tokens; answers 11–48.

**Cold start is the finding.** 56 seconds is five and a half times Aurora's ten-second answer
timeout — the first run of this benchmark failed outright against that limit, which is what a real
call would do. Ollama unloads an idle model after five minutes by default, so every gap in a
conversation longer than that costs a timed-out answer and a minute of silence unless the model is
pinned resident.

## Headroom

| | VRAM free | RAM free | GPU |
| --- | --- | --- | --- |
| Baseline (desktop, Discord, etc.) | 6306 MB | 3551 MB | 39% |
| Ollama installed, idle | 6187 MB | 3322 MB | 38% |
| Model loaded | **1177 MB** | 2917 MB | 97% |
| During inference (5 Hz sampling) | **1177 MB** | 2939–2952 MB | 97–98% peak, CPU 31–41% |
| After | 1177 MB | 2969 MB | 95% |

**The model takes 5129 MB of VRAM and 634 MB of RAM.** What is left on the card is 1177 MB, for
Windows, Discord and everything else already using 2 GB of it. That is the tightest number in this
document and the one that decides whether an 8B is the right size.

## Limits

Exercised against Aurora's own values — `MaxAnswerCharacters` 400, `Timeout` 10 s — never widened
to make the model look better.

| | result |
| --- | --- |
| Normal answer | 26 chars, inside the limit |
| Oversized | **could not be induced.** Asked for the complete history of Portugal in several paragraphs, it produced 188 characters. Aurora's 400-character refusal never fires with this model. |
| Empty | **could not be induced.** An empty turn still drew 25 characters. |
| Timeout | fired on the cold path (10 s exceeded by a 56 s load). On the warm path it could not be induced: the model beats a 0.4 s deadline. |
| Cancellation | clean, withdrawn mid-stream 0.07 s in |

Two of these could not be provoked, and that is reported rather than papered over. The boundary's
own handling of an empty and an oversized answer stays covered by its unit tests, which is where a
behaviour that a real runtime will not produce belongs.

## Voice pipeline — Whisper → model → Piper

Real engines, real audio, no Discord. Input spoken through piper so whisper had something to
mishear, which is the point.

| said | whisper heard | whisper | model | piper | perceived |
| --- | --- | --- | --- | --- | --- |
| "Aurora, estás a ouvir-me bem?" | *"Arola, estás ouvindo, hein?"* | 4.59 s | 0.90 s | 0.82 s | **7.01 s** |
| "Aurora, explica lá porque é que o céu é azul" | *"Aurora, explica lá o que é que o céu é azul."* | 3.31 s | 1.23 s | 1.03 s | **6.27 s** |
| "Aurora, o que achas do jogo de ontem?" | *"Agora, o que achaste do jogo de ontem?"* | 3.25 s | 0.90 s | 0.86 s | **5.71 s** |

Perceived latency averages **6.33 s**, of which:

- **whisper 3.71 s — 59%**
- model 1.01 s — 16%
- piper 0.90 s
- the silence pause before a turn is considered over, 0.70 s

**The model is not the bottleneck. Recognition is.** Adding an 8B model to this pipeline costs
about a second on top of five that were already there.

## European Portuguese — qualitative assessment

A reading, not a score. There is no honest number for this.

**Good, in isolation.** Asked directly, it answers in natural pt-PT: *"Sim, estou a ouvir-te bem.
Queres continuar?"* — correct second person, correct register, two clauses and a stop. Concision is
excellent throughout: 26 to 163 characters, never close to the limit, never a monologue.

**Brazilianisms appear intermittently**, and more often the messier the input: *"muito legal"*,
*"Posso apenas conversar com você"*, *"solicitação"*. Llama 3.1's Portuguese is predominantly
Brazilian and it shows under pressure.

**It mirrors the register it is given, and that is the sharpest finding.** In the text benchmark
with clean pt-PT input it answered in pt-PT. In the pipeline, fed whisper's output, it drifted:
*"Sim, estou ouvindo. Estou tentando descobrir o que está acontecendo."* Whisper's own transcripts
lean Brazilian, and the model follows them. Fixing the model's Portuguese therefore starts with
fixing recognition, not with changing the model.

**It invents when the transcript is thin.** Given *"Agora, o que achaste do jogo de ontem?"* it
answered about *"aquele tiro longo no final"* — a game it knows nothing about, described
confidently.

**Whisper still loses the name.** "Aurora" came back as *"Arola"* and *"Agora"* even with the
vocabulary prompt, which is the problem `docs/adr/0071` describes, unresolved.

## Limitations of this benchmark

- No provider implementation exists, so nothing went through `VoiceConversationBoundary`. The
  contract's behaviour under a real model is **untested**.
- Three utterances in the pipeline test, eight scenarios in the text test. Enough to decide a
  direction; not enough to characterise the model.
- The input audio is synthetic. Piper's speech is cleaner than a person on a laptop microphone in
  a six-person call, so the 3.71 s recognition figure is a **best case**.
- The `max_tokens` the harness derives from Aurora's character limit truncated two answers
  mid-sentence. That is a mapping the provider will have to get right — a character limit is not a
  token limit, and cutting mid-word is worse than refusing.
- One model, one quantisation, one runtime. Nothing here says another would be worse.

---

# Second candidate — llama3.2:3b

Pulled after the 8B, on the same machine, with the same harness untouched. The 8B had already
unloaded itself by then — Ollama's five-minute idle timer, which is the cold-start problem
demonstrating itself unprompted — so the 3B started from a comparable baseline. RAM was 320 MB
lower than the 8B's baseline (3231 MB against 3551 MB); nothing else differed.

| | |
| --- | --- |
| Model | `llama3.2:3b`, id `a80c4f17acd5`, 2.0 GB |
| Quantisation | **Q4_K_M** — the same as the 8B, which makes the comparison a clean one |
| Parameters | 3.2B · context length 131072 |
| Declared capabilities | `completion`, `tools` — again, never offered any |

## Side by side

| | llama3.1:8b | llama3.2:3b |
| --- | ---: | ---: |
| VRAM baseline free | 6306 MB | 6275 MB |
| **VRAM the model takes** | **5129 MB** | **2540 MB** |
| **VRAM left with it loaded** | **1177 MB** | **3735 MB** |
| VRAM free during inference | 1177 MB | 3736 MB |
| RAM baseline free | 3551 MB | 3231 MB |
| RAM the model takes | 634 MB | 732 MB |
| RAM free during inference | 2939 MB | 2522 MB |
| GPU peak during inference | 98% | 96% |
| CPU mean during inference | 36.5% | 40.8% |
| **Cold start, first token** | **56.41 s** | **4.01 s** |
| Cold start, total | 56.76 s | 4.10 s |
| Warm first token (mean) | 0.048 s | 0.033 s |
| Warm total (mean) | 0.596 s | 0.250 s |
| Answer, mean | 84 chars | 61 chars |
| Answer, longest | 163 chars | 132 chars |
| Asked for a long text | 188 chars | 324 chars |
| Cancellation | clean, 0.07 s | clean, 0.03 s |
| **Pipeline total (perceived)** | **6.33 s** | **6.21 s** |
| — whisper | 3.71 s (59%) | 3.93 s (63%) |
| — model | 1.01 s (16%) | 0.62 s (10%) |
| — piper | 0.91 s | 0.95 s |

The two numbers that matter are in bold, and they point in opposite directions.

## What the 3B buys

**2589 MB of VRAM**, which is the difference between 1177 MB of headroom and 3735 MB on a card
that also draws the desktop, Discord and everything else. And **a cold start of 4 seconds instead
of 56** — inside Aurora's ten-second answer timeout rather than five times past it, which turns
"the first answer after a pause fails" into "the first answer after a pause is slow".

What it does not buy is a faster conversation. Perceived latency went from 6.33 s to 6.21 s: two
per cent, because recognition is 63% of the wait and the model was never the problem.

## What the 3B costs — qualitative assessment

A reading. There is no honest score for this.

**Its Portuguese is worse, and it is worse from the start.** The 8B, given clean pt-PT, answered in
clean pt-PT: *"Sim, estou a ouvir-te bem. Queres continuar?"* The 3B, given the identical prompt,
answered *"Sim, estou ouvindo."* — Brazilian, on clean input, with no whisper involved.

That distinction answers the question this experiment was set to ask. The 8B **mirrors** the
register it is handed: pt-PT in, pt-PT out; whisper's Brazilian-leaning transcript in, Brazilian
out. The 3B **is** Brazilian by default and does not need whisper's help to get there. For the 8B
the fix starts with recognition; for the 3B it would have to start with the model.

**It makes grammatical errors the 8B did not.** *"é por cause da forma como a luz se dispersa"*,
and *"Estou apenas uma conversa."* Alongside the ordinary Brazilianisms — *"arquivos"*,
*"acessar"*, *"né?"*, *"você está falando"*.

**It understands less.** Asked two questions in one exchange it answered neither fully (*"Estou
bem, obrigada"*, ignoring the second speaker). Given the dirty transcript it produced *"Não, não
estou aqui, Rui, estou apenas na chamada"*, which is not coherent. Given the deformed name it
answered *"Eu acho que é muito difícil, mas engraçado, né?"* — a non-answer.

**It hallucinates at least as freely.** In the pipeline, hearing *"E aí, o que achas do jogo?
Juntei."*, it replied *"É bom, eu também joguei, mas não consegui ganhar."* — claiming to have
played.

**Concision is good on both**, and neither ever approached the 400-character limit. Asked for a
long text the 3B produced 324 characters to the 8B's 188 — closer to the limit, still under it.

## Limits, both models

| | llama3.1:8b | llama3.2:3b |
| --- | --- | --- |
| Normal | 26 chars, inside | 23 chars, inside |
| Oversized | not inducible (188) | not inducible (324) |
| Empty | not inducible (25) | not inducible (56) |
| Timeout | fired on the cold path only | not inducible at all |
| Cancellation | clean | clean |

Neither could be made to produce an empty or over-long answer. The 3B's cold start is fast enough
that even the cold path no longer exceeds Aurora's timeout, so with the 3B the timeout was not
inducible anywhere. The boundary's handling of both stays covered by its unit tests, which is
where behaviour a real runtime will not produce belongs.

## Recommendation

**`llama3.1:8b`, with the model pinned resident**, if the machine can spare 5 GB of VRAM
permanently. Its European Portuguese is the only one of the two that is actually European, and
quality is the axis where the 3B loses badly while winning almost nothing on the axis that matters
to a conversation.

The 3B's real argument is headroom, and it is a serious one: 3735 MB free against 1177 MB, on a
card shared with a desktop and a game. If Aurora has to coexist with anything that wants the GPU,
the 8B is not viable and the 3B is — at the cost of a Portuguese that needs an apology.

**Neither is VERIFIED.** No `ILocalLanguageModel` implementation exists; the harness spoke to
Ollama directly, and nothing has been through `VoiceConversationBoundary`.
