# Core conformance — audit result

**Date:** 2026-08-26 · **Suite:** 949 tests, 0 failures, 0 warnings

Read from the implementation, not from intent. The question this pass asked was not "does each
component work" — they did, and their tests passed — but "do the components add up to the system
the RFCs describe".

They did not, in nine places. All nine are closed.

## What the audit found

| # | Finding | Where | Closed by |
| --- | --- | --- | --- |
| 1 | `AuthenticationAbuse` declared, raised by nothing | RFC 09 | `adr/0064` |
| 2 | `PrivilegeEscalation` declared, raised by nothing | RFC 09 | `adr/0064` |
| 3 | Expired `PENDING` approval blocked its scope for ever | RFC 01 | `adr/0064` |
| 4 | `InstallationStatus.REMOVED` unreachable — no way to finish with a plugin | RFC 060 | `adr/0065` |
| 5 | A removed plugin could be released back in, restoring revoked permissions | RFC 060 | `adr/0065` |
| 6 | `PluginRefusal.UNDECLARED_ENDPOINT` declared, returned by nothing | RFC 060 | `adr/0065` |
| 7 | `MindStatus` declared six states, reached two | RFC 020 | `adr/0065` |
| 8 | Local-only rested on nobody having added an `HttpClient` | RFC 045 | `adr/0065` |
| 9 | The test suite leaked 5,800 temp files and filled the disk | — | `adr/0065` |

Findings 1, 2, 4, 6 and 7 share one shape: **declared, compiled, documented, and produced by
nothing**. Invisible to the suite, because every part compiled and every test of the part passed.
Finding 5 was a live bypass that only latency hid — it needed finding 4 to be reachable.

## The three tests that keep it closed

Rather than fix nine things and move on, three tests now assert the properties themselves:

**`DeadContractTests`** walks every declared event type, state constant, refusal reason and
security event type by reflection, and fails on any that nothing in `src/` produces. A constant
that stops being reachable fails on the day it stops, not in a review six months later.

**`LocalOnlyTests`** reads the source tree and fails on any construct that could open an outbound
connection — with the one loopback call named explicitly, pinned to a literal `127.0.0.1`. Also
asserts Kestrel binds to loopback and the host-header guard is installed.

**`CoreEndToEndTests` and `RestartTests`** exercise the loop through the real MCP and HTTP
surfaces: the cycle runs in the RFC 021 stage order; the refusals hold when reached the long way
round; a restart keeps what must survive and loses what must not.

## What the end-to-end tests prove

| Scenario | Result |
| --- | --- |
| Mission → goal → plan → capability → approval → kernel → audit | Closes; every stage recorded in order |
| Action outside the catalogue | Refused at resolution, before policy is asked |
| Action without an approval | Refused; nothing with an effect happens |
| Approval reused for different input | Refused — an approval is for one input |
| Approval reused for the same input | Refused — an approval is for one use |
| No room left on the machine | Reads still work; effects refused |
| Capability that fails | Recorded as failed; path not echoed back; not replayed as success |
| Aurora describing its own abilities | Grants nothing; the same capability still needs approval |
| Restart | Memory and audit chain survive; consumed approvals and consent sessions do not |
| Audit chain after all of the above | Verifies; no broken sequence |

## What remains open, and is recorded as open

- **Linux plugin confinement is UNVERIFIED.** The bubblewrap code exists and its plan is asserted;
  nothing has run it on Linux. See `docs/reference/platform-support.md`.
- **Windows plugin confinement is UNSUPPORTED.** No AppContainer token, so the host refuses to
  invoke rather than running third-party code loose. The default is safe.
- **Argon2 and OS keystore** are deferred, not missing — both are better answers to rules already
  met. See `docs/reference/rfc-status.md`.

Neither of the first two is a silent gap: `aurora health` reports which applies to the machine it
is running on.
