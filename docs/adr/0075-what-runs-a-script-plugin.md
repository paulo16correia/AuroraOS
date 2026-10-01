# Design 0075 — What runs a script plugin

**Status:** Implemented · **Date:** 2026-09-07
**Rests on:** `docs/adr/0062` (plugins people can actually write), `docs/adr/0067` (plugins that
hold a connection), `docs/adr/0072` (confinement that proves itself)

## Why now

Aurora ran on Windows for the first time. Every service plugin failed identically:

```
SERVICE_UNAVAILABLE: could not start: Win32Exception
```

Voice, Microsoft 365, Discord, the local voice stack, and `ServicePluginTests` itself — a hundred
or so failures that looked like four broken integrations and were one missing sentence.

`docs/reference/platform-support.md` had already predicted it and left it open: "`CreateProcess`
will not run a `.py` the way a shebang does on Unix, so a script plugin needs its interpreter named
in the manifest. This is unaddressed."

## The two halves of the same bug

**A `.py` file is not a program Windows can start.** Every plugin Aurora ships is a Python script
beginning `#!/usr/bin/env python3`. On macOS and Linux the kernel reads that line and runs Python.
`CreateProcess` wants an executable image and has no equivalent, so the launch fails before the
plugin exists.

**A process with no `SystemRoot` cannot open a socket.** Fixing the first half produced plugins that
started and could reach nothing: `[WinError 10106] could not load or initialise the requested
service provider`, from Winsock, whose service-provider catalogue is found through that variable.
Aurora builds a plugin's environment rather than inheriting it (`docs/adr/0062`), and the built one
named only `PATH`. An empty environment is not neutral on Windows; it is a broken one.

Both are in the launch, not in any plugin. Neither is visible on Unix.

## A closed set of runtimes, and never a path

Naming the interpreter makes "which program does Aurora start" a security question. The answer is
`PluginRuntimes`: a fixed list of runtime **names**, currently one, and nothing may supply anything
else.

- A manifest may declare `"interpreter": "python3"`. It may not declare `/usr/bin/python3`, and the
  reader refuses a path with the same reasoning it already refuses one in `executable` — a manifest
  that names a path is describing the machine rather than the plugin.
- Where the manifest says nothing, Aurora reads the script's own shebang. That is not Aurora
  choosing on the author's behalf: it is the declaration the author already wrote, read in user
  space because this kernel will not. Only the **name** is taken from it — `#!/opt/anything/python3`
  and `#!/usr/bin/env python3` both resolve to `python3`, and the directory is discarded.
- `#!/bin/sh` resolves to nothing and is refused. There is no generic command execution in Aurora
  and this is not the way in. Neither is `.bat` or `.cmd`, which Windows would run through the
  command processor, so neither counts as an executable image here.

Resolution to an actual path is either the owner's, through
`Aurora:Plugins:Interpreters:python3`, or a search of `PATH` for that runtime's fixed file names.
A named interpreter that is not there is a refusal, not a reason to start a different one. A
candidate is rejected unless it is a real file with a non-zero length: that is what excludes the
Microsoft Store app-execution aliases in `WindowsApps`, which are zero-length reparse points that
look exactly like `python.exe` and open the Store instead.

**It is only ever consulted where the platform cannot start the program itself**, which is Windows
and nowhere else. macOS and Linux take the same path they took before this design, byte for byte.

## The interpreter is confined, not exempted

`SandboxRequest.Executable` stays the plugin's own file. The confinement is written around it — the
directory it lives in, the policy naming it — and swapping in Python's path would silently move all
of that to wherever Python is installed.

The interpreter travels beside it, and `PluginCommand.For` is the one place that turns the pair into
a command line, so the three sandboxes cannot disagree about it. On Windows the AppContainer grants
the interpreter's directory **read-and-execute** — the container's default is deny and it applies to
Python exactly as it applies to the script — and never write, because that is a shared installation
of somebody else's software and a plugin able to write to it could replace the interpreter every
other plugin is then started with.

Nothing about `docs/adr/0072` is relaxed. The process is still created suspended, its token is still
questioned, and it still runs its first instruction only after Aurora has demonstrated the
confinement.

## What `SystemRoot` is doing in a cleared environment

It names where Windows is installed. It varies with nothing about who is running Aurora or what
they had exported, which is the property the clearing exists to protect — the same argument the
fixed `PATH` was accepted on in `docs/adr/0062`. It is read from the operating system rather than
copied from Aurora's own environment, so a variable set in the shell that started Aurora cannot
redirect where a plugin looks for Windows.

Both hosts now get their environment from one `PluginEnvironment`, because the entry that only one
platform needs is exactly the kind that drifts when there are two copies.

## What this cost

Nothing on Unix. Three shipped manifests gained an `"interpreter": "python3"` line that those
platforms never read, and the scaffolded plugin gained the same line and a comment saying why both
it and the shebang are there.

## What it fixed

Every service-plugin failure on Windows, and the cascade behind them: `VoiceRuntime`,
`MicrosoftRuntime`, `DiscordGateway`, `LocalVoice`, and the voice security tests that were failing
at plugin startup and therefore never reaching the signature, replay and policy paths they exist to
check.
