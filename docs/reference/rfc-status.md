# RFC implementation status

What each normative RFC has in the code, as of the Core closure pass. Read from the implementation
rather than from intent: an RFC is not IMPLEMENTED because a type exists with the right name.

| Status | Meaning |
| --- | --- |
| **IMPLEMENTED + VERIFIED** | Every mandatory rule has code, and a test exercises the real path. |
| **IMPLEMENTED + UNVERIFIED** | The code exists; something about the environment stops it being run here. |
| **EXPLICIT DECISION** | Deliberately not implemented as written, with an ADR saying why. |

| RFC | Subject | Status | Notes |
| --- | --- | --- | --- |
| 000, 00, 01 | Philosophy, vision, principles | IMPLEMENTED + VERIFIED | Default-deny policy, tool disable, session revocation |
| 010, 011 | Architecture map, layers | IMPLEMENTED + VERIFIED | Enforced by the LAW-002 reflection tests |
| 02 | Cognitive kernel | IMPLEMENTED + VERIFIED | `WorkItem` built in `adr/0058`; `ContextBundle` is `AttentionSet` + `WorkingMemoryFrame` |
| 020 | Mind | IMPLEMENTED + VERIFIED | Aggregate and change set in `adr/0058`; status trimmed in `adr/0065` |
| 021 | Cognitive cycle | IMPLEMENTED + VERIFIED | Stage order asserted end to end |
| 022 | Decision engine | IMPLEMENTED + VERIFIED | Six axes required by construction |
| 023, 024, 025 | Attention, working memory, deliberation | IMPLEMENTED + VERIFIED | Exclusion reasons recorded; trace encrypted |
| 026 | Scheduler | IMPLEMENTED + VERIFIED | DST handled; occurrences idempotent |
| 027 | Self model | IMPLEMENTED + VERIFIED | Reachable over MCP since `adr/0054` |
| 028, 029 | Beliefs, relationships | IMPLEMENTED + VERIFIED | A belief never carries a high-risk purpose alone |
| 03, 04 | Memory, knowledge graph | IMPLEMENTED + VERIFIED | Inferred material starts as a candidate |
| 030–034 | Signals, needs, curiosity, resources, situation | IMPLEMENTED + VERIFIED | Disk judged on room left (`adr/0061`) |
| 035 | Constitution | IMPLEMENTED + VERIFIED | Mechanism added in `adr/0057` |
| 036 | Genome | IMPLEMENTED + VERIFIED | Effective genome now on life episodes too |
| 037, 038, 039 | Development, life history, lifecycle | IMPLEMENTED + VERIFIED | |
| 040, 041, 042 | Domain, world, time | IMPLEMENTED + VERIFIED | `Time.parse` is an EXPLICIT DECISION (`adr/0059`) |
| 043 | Mind state | IMPLEMENTED + VERIFIED | Snapshot, restore, export |
| 045 | Kernel boundary | IMPLEMENTED + VERIFIED | Per-plane identities are an EXPLICIT DECISION (`adr/0059`) |
| 05 | Planner | IMPLEMENTED + VERIFIED | |
| 050 | Event bus | IMPLEMENTED + VERIFIED | Consumers pumped by the heartbeat since `adr/0063` |
| 051 | Capabilities | IMPLEMENTED + VERIFIED | |
| 052 | Missions | IMPLEMENTED + VERIFIED | Active goals carry a mission or a review date |
| 06 | Tool system | EXPLICIT DECISION | Complete and dormant: Aurora ships no connector (`adr/0059`) |
| 060 | Plugin SDK | IMPLEMENTED + VERIFIED | Authorable since `adr/0062`; confinement per platform table |
| 07 | Personality | IMPLEMENTED + VERIFIED | |
| 08 | Learning | IMPLEMENTED + VERIFIED | Evaluator in `adr/0055`, rollback in `adr/0056` |
| 09 | Security | IMPLEMENTED + VERIFIED | All six event types raised since `adr/0064` |
| 090 | Review gate | IMPLEMENTED + VERIFIED | |
| 10 | API and MCP | IMPLEMENTED + VERIFIED | |
| 11 | User interface | IMPLEMENTED + VERIFIED | All five rules cited in `app.js` |
| 12 | Deployment | WITHDRAWN | `adr/0045` — Aurora is local only |
| 13 | Roadmap | Process | |

## Deferred, and recorded rather than hidden

- **Argon2** instead of PBKDF2 — needs a new package and a supply-chain verdict (design 0001).
- **OS keystore** (Keychain, DPAPI) instead of owner-only key files — the interface takes raw key
  bytes, so swapping the source touches one class.

Neither is a gap in a rule. Both are a better answer to a rule already met.
