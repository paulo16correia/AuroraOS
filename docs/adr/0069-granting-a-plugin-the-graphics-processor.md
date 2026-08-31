# Design 0069 — Granting a plugin the graphics processor

**Status:** Implemented · **Date:** 2026-08-31

## Why it came up

Local speech recognition is unusable without it. Measured on this machine, three seconds of audio
through `ggml-large-v3-turbo`:

| | |
| --- | --- |
| Processor alone | 70–90s |
| Graphics processor | 8–19s |

Seventy seconds to hear one sentence is not listening. The smaller model runs in about real time
and mishears a name often enough to matter — "Aurora" came back as "Aura a hora" — so the choice
was a recogniser too slow to use or one too weak to trust.

## What it is, and what it is not

Compute, not the owner's files and not the network. That is why it can be granted at all.

A graphics driver is also a large kernel surface reached from third-party code. That is why it is
not granted by default, and why it is a separate question at install from the permissions and from
the network. Somebody can reasonably want a plugin's capabilities without wanting it to reach the
internet, or want it to reach the internet without handing it a graphics driver.

## The two rules, found by measuring

```
(allow iokit-open)
(allow file-write* (regex #"/com\.apple\.metal"))
```

Neither was obvious and neither could be read off a document.

**Without the first**, Metal loads its backend and the process dies without a word. From Aurora's
side that is exit -11 and one log line about loading Metal — the segmentation fault that reads as a
broken recogniser rather than a denied permission.

**Without the second**, it is worse, because it works. Metal initialises, reports the GPU by name,
and computes on the processor anyway: the same answer twenty times slower, with nothing anywhere to
say why. Bisecting from a fully open profile is what found it, and the cost was two hours of
plausible-looking failure.

The write is matched by name rather than by allowing the directory it sits in. A plugin gets
somewhere to compile shader pipelines and not the rest of what lives beside them in the user's
cache.

## What is not granted

The plugin still cannot read the owner's files, cannot reach the network without its own grant, and
cannot accept a connection. Nothing about the GPU grant widens any of those, and a test asserts a
plugin without the grant cannot open a Metal device at all — because a grant that leaks to the
plugins that did not ask for it is not a grant.

## What this does not fix

The recogniser is still spawned once per utterance and reloads the model each time. At eight
seconds for the large model that is tolerable; it is not a design that would survive a busy
channel, and the fix is a resident recogniser rather than a wider sandbox.
