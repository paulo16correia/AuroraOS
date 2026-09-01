# Design 0071 — Microsoft 365 does not make Aurora networked

**Status:** Decided · **Date:** 2026-09-01
**Rests on:** `docs/adr/0045` (local only), `docs/adr/0067` (plugins that hold a connection)

## What was asked for

A Microsoft 365 capability family — Teams, Outlook, Calendar, OneDrive, SharePoint, Planner — and
Windows as a first-class platform.

## The trap, which I walked into

Plugin execution is refused on Windows. `PluginSandbox.ForThisMachine()` has no confinement to
offer there, and both hosts refuse to launch a plugin they cannot confine unless the owner
explicitly accepts that. So the obvious reasoning runs: *if plugins do not run on Windows, and
Microsoft 365 must run on Windows, then Microsoft 365 must not be a plugin — it must be in-process
C#, like `files.read_sandbox`.*

That reasoning is valid and its conclusion is wrong, because it quietly assumes the thing it should
have checked: that Aurora's own process is allowed to open a socket.

It is not. `LocalOnlyTests` reads the whole source tree on every run and fails on anything that
could reach another machine — `new HttpClient`, `new Socket`, `Dns.GetHostAddresses`, eleven
patterns in all — with one loopback call named as the single exception. I built an outbound HTTP
seam with a host allowlist, connect-time address validation, credential attachment deferred until
after the destination check, bounded retries and `Retry-After`. It was decent work. The suite
failed it in one line, with a comment that had been waiting for me:

> If this fails, somebody added a way for Aurora to reach another machine. That may be the right
> thing to do — and it is a change to the architecture, not a change to a file.

## Why the architecture is right and the shortcut was not

An in-process allowlist and a sandboxed subprocess are not two implementations of one idea.

The allowlist is enforced by code running inside the process that holds the Kernel, the audit key,
the vault key and every credential. It is a check that a bug elsewhere in the same address space
can be persuaded around.

The plugin boundary is enforced by the operating system, in a different process, which holds no
Aurora key and has no route to one. Whether it may open a socket at all was decided by the owner at
install, in words they read. That is not a stronger version of the same control. It is a different
kind of control, and it is the one every other guarantee in Aurora is stacked on.

Building the in-process seam would have moved Microsoft 365's blast radius from "a subprocess that
can reach graph.microsoft.com" to "the process that can read everything Aurora knows". To make
Windows work sooner.

## The decision

**Microsoft 365 is a plugin.** It follows Discord: its own process, its own manifest, declared
hosts, the token delivered over the pipe rather than the environment, and the sandbox as the
boundary. Aurora's process stays local-only and `LocalOnlyTests` stays exactly as strict.

**Windows enterprise support therefore depends on Windows plugin confinement**, not on avoiding it.
That reorders the work: AppContainer is not a nice-to-have at the end of the Windows phase, it is
the prerequisite for the enterprise capability families existing on Windows at all.

**Until that lands, the honest position is the one the platform already takes** — the plugin is
refused on Windows and says why, rather than running unconfined. An owner who wants it anyway sets
`Aurora:Plugins:AllowUnconfined` and is told exactly what they are accepting.

## What this costs, stated plainly

Microsoft 365 will work on macOS and Linux before it works on Windows, which is the opposite of the
order that was asked for. The alternative was to have it work everywhere by making Aurora itself a
networked process, and that is a worse trade than a late platform.

## What was thrown away

`GovernedHttpClient` and its fourteen tests, deleted rather than left in the tree. The design is
described here because the same reasoning will occur to somebody else, and this is the reply: the
governance it implemented already exists, one process boundary further out, enforced by something
that a bug in C# cannot talk around.
