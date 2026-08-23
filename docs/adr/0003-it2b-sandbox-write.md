# Design 0003 — `files.write_sandbox` (It.2, second increment)

**Status:** Implemented · **Date:** 2026-08-23
**Depends on:** `docs/adr/0002-it2a-persistent-approval.md` (persisted approval)

## Objective

It.2a gave ≥MEDIUM capabilities an approval path, but the only capability with a
real effect wrote to a SQLite table of our own. This increment delivers the first
**filesystem** write, which is where path hardening starts to matter:
`files.write_sandbox` (MEDIUM, `approval_required`), confined to a sandbox root.

It reuses the It.2a gate entirely — there is no new consent mechanism here. What
is new is the filesystem boundary.

## Three defences, in order

**1. Lexical validation** (`SandboxPathValidator`, no I/O, in `Aurora.Core`)

It rejects rather than sanitises: a path that is not obviously safe is refused,
never rewritten into something "close enough". Sanitising is how bypasses are
born.

Refused: traversal (`..`), `.` segments, absolute or separator-anchored paths,
UNC (`//`, `\\`), device namespaces, `:` (covering alternate data streams and
drive-relative paths), control characters, empty segments, segments ending in a
space or a dot, Windows reserved device names (`CON`, `NUL`, `COM1`–`COM9`,
`LPT1`–`LPT9`, with or without an extension), and paths over 512 characters.
Finally it confirms the resolved path still sits under the root, compared
**ordinally** — we build the path from the root, so a legitimate child always
shares the exact prefix; a case-insensitive compare would only widen what counts
as "inside".

**Deliberate decision:** the Windows-specific rules are enforced on **every**
platform. A sandbox written on macOS may later be opened on Windows, and a name
that is inert here resolves to a device there. One rule set also means the tests
cover the same behaviour everywhere.

**2. Linked components** (`SandboxFileWriter`)

It walks root → target and refuses if any existing component is a symlink or
reparse point, including the target file itself — overwriting a link would write
through it. The check runs **before** creating directories (so we never `mkdir`
through a link) and **again** afterwards, once every component exists.

The sandbox root is resolved through its own links once at construction: a root
that is itself a symlink is the operator's choice and keeps working; only links
*inside* the sandbox are treated as an escape attempt.

**3. Atomic write**

A temporary file in the same directory (`FileMode.CreateNew`,
`FileOptions.WriteThrough`), flush, then `File.Move(..., overwrite: true)`. A
reader never observes a half-written file. The temporary file is removed if
anything fails.

## Residual risk, stated plainly

.NET has no portable `openat`/`O_NOFOLLOW`, so the link check and the write are
separate syscalls. **Anyone able to create files inside the sandbox root between
those two steps can still win a TOCTOU race.** Closing this properly needs
per-platform interop and is deferred. Today's mitigation is operational: the
sandbox root should be writable only by the Aurora process's own user.

This is not a footnote — it is the known security limitation of this increment,
and it must be revisited before the sandbox is ever shared with another user or
service.

## Error surface

A sandbox violation raises `SandboxViolationException`, which the Kernel reports
as a generic `execution_failed`. The reason does **not** travel back to the
caller: otherwise a client could map the sandbox one rejected path at a time. The
cost is poorer diagnostics; audit pre-image enrichment (It.3) is the right place
to record the reason server-side.

## Configuration

`Aurora:SandboxRoot`, defaulting to `{LocalApplicationData}/Aurora/sandbox`,
created at startup. Integration tests use a temporary root per factory instance,
so a test write never lands in the real sandbox.

## Tests

34 new tests. Validator: every refusal family above, plus acceptance of
legitimate nested paths and of names that only *look* like devices
(`consortium.txt`). Writer: creation, nested directories, overwrite, no leftover
temporary files, traversal refused, symlinked directory and symlinked file to the
outside refused, and a symlinked root still working. Integration: approval
required before anything touches disk, and — the test that matters most —
**traversal still fails after the approval is granted**, because approval
authorises the action, never the escape.

## Deferred

Closing the TOCTOU race through interop; binary writes (UTF-8 text only for now);
reading and listing inside the sandbox; size and file-count quotas.
