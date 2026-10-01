# Design 0077 — One name for a time zone

**Status:** Implemented · **Date:** 2026-09-07
**Rests on:** RFC 026 rule 1 — a schedule's zone is mandatory and is never assumed

## Why now

On Windows, everything that knew what time it was stopped working at once:

```
'Europe/Lisbon' is not a time zone this machine knows.
```

The scheduler, the situation service, retention, curiosity, the daily review, incidents,
maintenance, and the status endpoint — which returned 400 for a zone that had been valid on every
machine Aurora had run on until then. Roughly a third of the Windows failures were this one line,
raised from two places.

## The comment in the code was wrong

`SqliteScheduler.ResolveZone` said:

> IANA ids resolve on every platform .NET supports, so a schedule written on macOS means the same
> thing on Windows.

That is true of .NET and not true of Aurora. `TimeZoneInfo.FindSystemTimeZoneById` accepts an IANA
id on Windows through **ICU**, and `Directory.Build.props` sets `InvariantGlobalization`, which does
not load ICU. Without it, `FindSystemTimeZoneById` sees only the Windows registry's own names, and
`TimeZoneInfo.TryConvertIanaIdToWindowsId` — the API that exists for exactly this — returns `false`
on every input.

Two things were therefore load-bearing and unstated: that Aurora's canonical zone id is the IANA
one, and that resolving it needs a mapping Aurora does not get for free.

## Canonical means IANA, and canonicalising never changes the city

`AuroraTimeZones` is the one place a zone id becomes a zone. It asks the platform first, so a
runtime that does have ICU — or a Windows registry that has the id verbatim — is believed before
anything else. Only when the platform does not know the id is the embedded CLDR mapping consulted,
and only to translate the id into the other family's spelling; the zone itself always comes from the
operating system, and Aurora never invents an offset or a DST rule.

The rule that took the most care: **an IANA id is returned unchanged, always.** `Europe/Lisbon` and
`Europe/London` are one Windows zone, `GMT Standard Time`, so canonicalising through Windows would
answer "Europe/London" and quietly rewrite what the owner said into a different city that happens to
keep the same clock this decade. Canonicalisation converts Windows ids to IANA and does nothing
else.

Where a caller names no zone, the default is `AuroraTimeZones.LocalId` rather than
`TimeZoneInfo.Local.Id`, because on Windows the latter is `GMT Standard Time` and persisting it into
a schedule writes a row the same installation cannot read after being moved to a Mac. The scheduler
canonicalises on create for the same reason.

Unknown ids are refused by name. Nothing falls back to UTC or to the machine's own zone: a schedule
that silently ran an hour out would be worse than one that refused to be created.

## The table is data, and it is embedded on purpose

`WindowsZoneTable` is CLDR's `windowsZones.xml` — 139 Windows zones, 591 IANA ids including
tzdata's compatibility links — as one grouped list.

It is embedded rather than read from the platform because the platform does not have it here. It is
also the more honest answer even where ICU is present: the same input gives the same output on every
machine and in every run, which a lookup through whichever ICU version the host happens to ship
would not.

Four ids are deliberately absent. tzdata's unqualified `CET`, `EET`, `MET` and `WET` have no CLDR
mapping, so on Windows they are refused by name rather than guessed at. They resolve normally on
Unix, where the platform knows them.

## Where it is not

Not in `SituationService`, not in `SqliteScheduler`, and not behind an `OperatingSystem.IsWindows()`
in either. A platform seam in the middle of business logic gets copied, and the fourth copy is the
one that gets the fallback wrong. Both call sites now ask `AuroraTimeZones` and turn a refusal into
their own exception type, with the messages they had before.

## What it fixed

The whole cluster, including three failures that did not look like it: `aurora_review` over MCP,
which returned an error string where a test parsed JSON — `'A' is an invalid start of a value` was
the MCP error envelope, not a contract mismatch — and the two status endpoints returning 400 and 500
for a zone they should have accepted.
