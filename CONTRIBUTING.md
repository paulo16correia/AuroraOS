# Contributing to Aurora OS

Two things here work differently from most repositories. Knowing them first will save you a
rejected pull request.

**The tests are the specification.** Not a safety net for the code — the statement of what the code
must do. A test named `ARefusalDiscordWillRepeatIsNotAskedAgain` is a rule, and deleting it to make
a change pass is deleting the rule. If a test is in your way, the honest move is to argue that the
rule is wrong, not to quietly remove its evidence.

**An architectural decision needs an ADR before it needs code.** There are
[88 of them](docs/adr/). They are short, they say what was decided and what was given up, and they
are written in the past tense because they record something that happened. A pull request that
changes a boundary without one will be asked for one.

**Fewer documents, kept true.** Before writing a new one, change the one that already covers it: the
runbook for how to operate Aurora, platform support for what runs where, an ADR's status line for a
decision a later one changed. A new ADR is for a decision that moves a boundary, not for every
change. `DocumentationTests` fails the build when a document names a setting, a command, a link or an
ADR that does not exist.

---

## Getting set up

```bash
git clone https://github.com/paulo16correia/AuroraOS.git
cd AuroraOS
dotnet build
dotnet run --project src/Aurora.Server -- doctor   # tells you what your machine is missing
dotnet test
```

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Python 3.13 is needed only if
you touch the plugins.

If `dotnet test` is not green before you start, that is the first bug to report — not something to
work around.

---

## What a good change looks like

**Small, and about one thing.** A pull request that fixes a bug and tidies imports is two pull
requests.

**With a test that fails without it.** For a bug fix, write the failing test first and put it in
the description. "Fixed the thing" without a test that would have caught the thing is how the thing
comes back.

**Honest in its description.** Say what you did not do. `IMPLEMENTED / TESTED / VERIFIED /
UNVERIFIED` is used throughout this repository and means exactly what it says: *verified* means
somebody watched it work on real hardware, and nothing else earns the word.

---

## Code style

There is no formatter to run and no lint config to satisfy. There is one rule, and it is harder:

> **Write code that reads like the code around it.**

Which in practice means:

- **Comments say *why*, never *what*.** `// increment the counter` is noise. `// Paced against a
  fixed start rather than sleeping a flat 20ms, so encoding time does not accumulate into audible
  drift` is why the line is not the obvious one.
- **Name the failure, not the mechanism.** A refusal that says "the call requires DAVE end-to-end
  encryption and this machine cannot take part" is useful. "Error 4017" sends somebody to a search
  engine.
- **Fail closed and say so.** If something cannot be verified, the code refuses and explains.
  Nothing here is "probably fine".
- **C#:** nullable enabled, warnings are errors. Do not suppress; fix.
- **Python plugins:** the standard library only. This is not a preference — a plugin may be started
  before it has been granted a network, so `pip install` is not available to it. The Discord plugin
  writes its own WebSocket and its own AEAD for this reason.

---

## The boundaries not to cross

These are laws rather than style, and a change that breaks one will not be merged whatever it
improves:

| Boundary | Why |
|---|---|
| **The plugin protocol is one-way.** | A plugin reports; it never requests. Adding a request frame would hand that ability to every plugin in order to give it to one. [LAW-002](docs/laws/LAW-002-mind-tool-isolation.md) |
| **Nothing acts without the Kernel.** | resolve → validate → policy → consent → idempotency → execute → audit. No shortcuts, no "just this once". [RFC 045](docs/045-aurora-kernel.md) |
| **No silent autonomy.** | Every effect is attributable and auditable. [LAW-006](docs/laws/LAW-006-no-silent-autonomy.md) |
| **Secrets never travel in argv.** | They are typed into a prompt and stored in the vault. A command line is visible to every process on the machine. |
| **`Aurora.Core` has no I/O and no vendors.** | It is the part that must survive a rewrite of everything else. |

---

## Working on plugins

Plugins are separate processes behind a sandbox boundary. That has a consequence worth internalising
before you debug for an hour: **a confined plugin's `PATH` is System32 only**, it cannot write to the
owner's `%TEMP%`, and it cannot read files outside its own directory. If something works when you run
it by hand and fails under Aurora, confinement is the first thing to suspect, not the last.

Python test suites are run *from* the .NET suite, with their test count pinned:

```csharp
RunPython("test_transport", 29);
```

That number is not bureaucracy. A suite that silently loses tests still passes; pinning the count
turns that into a failure. If you add tests, update the number in the same commit.

---

## Reporting bugs

Include what you expected, what happened, and the output of:

```bash
dotnet run --project src/Aurora.Server -- doctor
```

Never paste a secret, a token, or a database. `doctor` is written to report whether a secret is
present without reporting its value; please keep it that way in what you paste.

**Security issues do not go in the issue tracker.** See [SECURITY.md](SECURITY.md).

---

## Licence of contributions

Aurora OS is [AGPL-3.0](LICENSE). By contributing you agree your contribution is licensed under the
same terms. If you are contributing on behalf of an employer, make sure you are allowed to.
