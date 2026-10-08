# Design 0092 — The operating system is the sandbox, not a container

**Status:** Implemented, by what was already built · **Date:** 2026-10-08
**Rests on:** `docs/adr/0045` (local only), `docs/adr/0052` (plugin confinement), `docs/adr/0072`
(confinement that proves itself), `docs/adr/0078` (the AppContainer, actually run), `docs/adr/0089`
(the model is asked from Aurora)

## The question

Somebody asked why this project does not use Docker.

The first honest answer is that nobody had written the decision down. Ninety records, and not one of
them mentions containers: `docs/adr/0052` went straight to `sandbox-exec`, `bubblewrap` and
AppContainer, and the alternative was never argued against because it was never argued about. A
choice taken by default is not a decision. This is the decision, written late.

## The decision

**Plugins are confined by the operating system, one sandbox per plugin.** macOS gets a deny-by-default
SBPL profile, Linux a `bubblewrap` namespace, Windows an AppContainer token.

**Aurora herself is not containerised, and will not be while she is one person's.**

## Why the boundary falls in the wrong place

A container isolates an application *from the machine*. Aurora needs the opposite of that, and needs
it in two directions at once: she must reach deep into the machine, and her plugins must not reach
any of it.

What she needs from the machine is not incidental:

- **The graphics processor.** Measured in `docs/adr/0091`: `ggml-large-v3-turbo` takes 11.6 seconds a
  sentence on this processor and 3.3 seconds on the RTX 4060 — and that 3.3 includes loading 1.6 GB
  of model from cold. Recognition is only viable on the card. On Windows, reaching a card from inside
  Docker means WSL2 and the NVIDIA container toolkit: two more layers between Aurora and the one
  piece of hardware that makes her able to hear.
- **Audio devices.** She sits in a voice channel. Passing real audio in and out of a container is
  awkward even on Linux, where it means binding PulseAudio's sockets; through Docker Desktop's Linux
  virtual machine there is no path at all.
- **The owner's own machine.** The database, the four key files and everything personal live at
  `%LOCALAPPDATA%\Aurora`. Aurora is the program that is *supposed* to hold them.

And on Windows and macOS, Docker is a Linux virtual machine, which makes the whole question moot
where most of this runs: **an AppContainer cannot be created inside a Linux container.** Containerising
Aurora would throw away the confinement that works in order to gain isolation she must not have.

## Why a container is also the weaker confinement here

This is the part that surprises people, so it is worth stating plainly: for the thing that actually
needs confining, the operating system draws a *finer* line than a container does.

- **Per plugin, not per application.** Each plugin gets its own sandbox with an exhaustive list of
  capabilities — on Windows, exactly one exists at present, `InternetClient` — and an explicit list
  of paths on the ACL. A container, by default, hands the process a whole userland and a network
  stack. More isolation from the outside, less say over what is permitted inside.
- **Windows refuses loopback to an AppContainer, and that is load-bearing.** A confined plugin cannot
  reach `127.0.0.1`, so it cannot reach Aurora's own MCP endpoint. That is the operating system's
  behaviour rather than something configured, and `docs/adr/0089` is built on top of it: the language
  model had to be moved back to Aurora's side precisely because the confined plugin could never reach
  it. A container on a bridge network reaches its host trivially.
- **A container per plugin would be a virtual machine per plugin** on the two platforms where Docker
  is a virtual machine. For a program that starts a handful of short-lived script plugins, that is a
  cost with nothing bought by it.

## Where Docker would earn its place

The question was a good one, and there are two places where the answer is "yes, and it is not done
yet":

- **Continuous integration.** A reproducible Linux test environment is exactly what containers are
  good at, and this repository has none running today.
- **The Python runtime the script plugins use.** It is a virtual environment at
  `%LOCALAPPDATA%\Aurora\runtime\python`, assembled by hand. Reproducibility there is a real problem
  and a container is a real answer to it.

Neither of those is about confining a plugin, which is the thing this record is about.

## What this does not claim

It does not claim containers are weak isolation. They are not, and the things built on top of them
for running untrusted code — gVisor, Firecracker — go further than anything here does. The claim is
narrower and only about where the line falls for *this* program.

It also does not claim to be measured. The confinement that exists is verified — `docs/adr/0072` and
`docs/adr/0078` exist because unverified confinement was reported as none — but nothing here was
compared against a containerised Aurora, because building one to compare would have meant giving up
the graphics processor and the audio devices first. This is an argument from where the boundary has
to fall, not a benchmark.

And it is conditional on the premise in `docs/adr/0045`: one instance, on one person's machine,
holding that person's data. If Aurora ever becomes something several people share, the cost of a
virtual machine per plugin stops being absurd and the reproducibility of a deployment starts
mattering. Then this record is wrong, and this paragraph is where to start.
