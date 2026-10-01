# Design 0083 — Tests do not write to the installation

**Status:** Implemented · **Date:** 2026-09-08
**Rests on:** `docs/adr/0036` (owner-only), `docs/adr/0079` (fail-closed key protection)

## What was suspected, and what was true

The suspicion was that `dotnet test` had created `aurora.deliberation.key` in the operator's live
installation. It had not. Measured — every file under the deployment root hashed before and after
a full run — the suite created nothing, deleted nothing and altered nothing. The key was created
by the only code path that creates it, `ServiceRegistration` inside `AddAurora`, during an ordinary
server start. That is a server doing its job.

Recording the correction rather than the suspicion, because the suspicion is what motivated this
and a design note that quietly keeps the wrong reason teaches the wrong lesson.

What *was* true is narrower and still worth fixing: nothing prevented a test from writing there,
and two already resolved paths into it.

## The real hazard, which is not hypothetical

`test_engine_discovery.py` wrote a four-byte stub over the installed `piper.exe` and removed it in
teardown. That deleted a working speech engine off the machine it was testing, and the failure
surfaced hours later as a voice that had gone silent for no visible reason. The same shape on the
.NET side reaches the live database, the live keys, and `aurora.passphrase.json` — which holds the
operator's failed-attempt count and lockout, so a test that rewrote it could lock a person out of
approving anything, or quietly clear the evidence that somebody had been trying.

`AuroraServerOptions.FromConfiguration` defaults every unconfigured path to
`%LOCALAPPDATA%\Aurora`. That is right for a server somebody started. For a test it means
forgetting one setting aims the test at the operator's installation, silently. Resolving
`Aurora:SandboxRoot` is not even a read: it creates the directory and re-applies owner-only
hardening to it.

## The fix: no silent fallback

`AURORA_REQUIRE_EXPLICIT_PATHS=1` makes an unconfigured `Aurora:DbPath` or `Aurora:SandboxRoot`
throw, naming the setting and saying why. The test assembly sets it in a `[ModuleInitializer]`, so
it covers tests written after this one — a rule that has to be remembered in a hundred places is a
rule that will be forgotten in one. Production behaviour is unchanged: nothing else sets the
variable.

Arming it was also the audit. Rather than reading a hundred test files and hoping, the suite was
run with the guard on and the failures enumerated the offenders exactly: **two**, both in
`ServerOptionsTests`, whose helper supplied only a bearer token and so read *deployment* defaults
while claiming to test *configuration* defaults. They now supply their own temporary paths.

The Python suites were audited the same way and were already correct: the Microsoft and voice
plugin tests copy the plugin into a `mkdtemp` directory and write `config.json` there, one of them
documenting precisely why the checkout is the wrong place. `test_engine_discovery` was corrected
when the `piper.exe` deletion was found, and carries a test that fails if the stubs ever land
beside the plugin again.

## What the regression tests pin

Not "nothing was harmed this run" — that measurement stops being true the moment somebody adds a
test. What is pinned is the mechanism:

- the guard is armed for the assembly;
- an unconfigured database path throws, and the message names the setting and the deployment;
- an unconfigured sandbox root throws, and it is the one that would have *written* rather than
  merely resolved;
- every path a configured options object produces — database, sandbox, all six keys, passphrase,
  plugin root — is outside the deployment root, compared as paths rather than as text;
- `AuroraAppFactory`, the one place in the suite that starts the real program and therefore creates
  real keys, keeps its whole deployment elsewhere;
- no test path ever equals the operator's own `aurora.db`, `aurora.passphrase.json`, or any of the
  key files, each named individually so that adding a path is a compile error here rather than a
  gap nobody notices.

## What did not change

No deployment file was deleted, moved or replaced to make any of this pass; the isolation is
entirely on the test side. No capability, kernel path, policy or confinement behaviour changed.
