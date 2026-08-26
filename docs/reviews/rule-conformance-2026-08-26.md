# Rule conformance pass — 2026-08-26

**Scope:** the 186 numbered mandatory rules across the 42 RFCs that carry a `## Mandatory rules`
section.
**Cause:** `docs/adr/0053` established that RFC coverage had been decided by grepping each RFC's
principal symbol — a method that had already missed LAW-008's trace control and RFC 08's evaluator.

## Method, stated so the result can be weighed

1. **Existence sweep, all 42.** Every structure and interface named in each RFC's `Data structures`
   and `Interfaces` blocks, checked against the whole source tree.
2. **Rule reading where the sweep flagged something**, and for every rule that describes a
   *refusal* — the easiest kind to leave out and the hardest to notice missing.
3. Not every one of the 186 was read against the code individually. The RFCs that passed the sweep
   carry 445 rule citations in the source, and the ones I read were implemented rule by rule; the
   remainder is inference from that, not verification.

## Unimplemented

**RFC 020 rule 2 — `MindChangeSet`.** There is no `Mind` aggregate, no `MindChangeSet` and no
`MindService.open/propose/apply`. Every component the Mind is supposed to own exists as its own
service; what is missing is the object that owns them and the propose/validate/apply discipline for
writing to them. `GraphChangeSet` exists for the knowledge graph, so the pattern is established in
this codebase — just not where RFC 020 requires it. LAW-001 covers the *spirit* (nothing enters Mind
without provenance) and is tested; the change-set object itself does not exist.

**RFC 035 rules 1 and 2 — the Constitution has no mechanism.** No `ConstitutionalAssessment`, no
`Constitution.assess`, no `Constitution.reviewPolicy`. Nothing checks a policy against the eight
Articles at publication, and a high-risk `Decision` carries `PolicyDecisionIds` and `RiskLevel` but
no reference to the constitutional rules it was judged against. The genome refuses an override that
would relax the Constitution, which is the nearest thing that exists and is not the same thing.

**RFC 09 rule 5 — incidents.** No `SecurityEvent`, no `Incident.open`, no OPEN/CONTAINED/RESOLVED
lifecycle. "High risk incidents MUST revoke affected capacity, preserve evidence, and notify owner"
is three separate half-measures today: the development model restricts a stage after an incident,
life history has an `INCIDENT` episode kind, maintenance notifies for one, plugins quarantine
themselves. Nothing ties them together and nothing raises the event.

**RFC 08 limit case — rollback on failed application.** Already recorded in `docs/adr/0055`.
`RollbackPlan` is now required before a change is applied; nothing executes it, and nothing opens an
incident when application fails. It is the same gap as the one above, seen from the other end.

## Partial

**RFC 036 rule 3 — the effective genome.** "preserves reference to the effective Genome in Mind
State **and Life History**". `MindStateSnapshot.EffectiveGenomeRef` exists; life history carries no
genome reference at all.

**RFC 02 — `WorkItem` was never modelled.** `CognitiveCycle.WorkItemId` and `tool_call.work_item_id`
both reference an object that has no type, no table and no lifecycle. RFC 02 gives it a status
machine, a deadline, a retry count and an idempotency key; none of that exists. What the field
actually holds is a subject reference — `conversation/{ref}` from the pilot, `review/{client}` from
the review application, and `mcp/{action_id}` from the dispatcher. The last of those is the same
value for every call of the same capability, so "the cycles of this work item" is not a question the
column can answer. Nothing queries it today, which is why it has cost nothing so far.

RFC 02's other structures are present under RFC 023/024's names: `ContextBundle` is `AttentionSet`
plus `WorkingMemoryFrame`, with the item limit, budget, sensitivity ceiling and recorded exclusion
reasons rule 4 asks for.

## Divergences that are defensible and were never written down

**RFC 042 — `TemporalExpression` and `Time.parse`.** Aurora parses no natural-language dates.
Under the MCP-first architecture that is the client's job (RFC 045, RFC 10), and Aurora receives
explicit instants — but the RFC states the interface and no ADR records the divergence. Rule 2's
substance ("do not assume a date when it implies action") is met: the scheduler requires an explicit
timezone. `ValidityInterval` is implemented as half-open `[valid_from, valid_to)` columns.

**RFC 045 rule 1 and RFC 09 rule 3 — service identities.** One process, one bearer token, one
operator session. The planes are namespaces, not separately-identified services. Per-plane
identities would be theatre in a single local process, which is a defensible reading — and exactly
the kind of thing `docs/adr/0012` records for SQLite and nobody recorded here.

## Implemented, tested, and reachable from nowhere

**`IToolManager`.** Registered in DI, injected into nothing, called by nothing outside tests. RFC 06's
entire connector path — propose, authorize, dispatch, reconcile, secret leasing per tool — is
dormant, because Aurora has no external connectors and executes through `ICapabilityRegistry`
instead. Coherent with being local-only; it also means LAW-002's compliance test exercises a path
production never takes. This is the same shape as `ISelfModel.DescribeAsync` before `aurora_self`
(`docs/adr/0054`), and it is bigger.

## Checked and sound

The remaining RFCs passed both the sweep and, where read, the rules themselves — among them RFC 01
(default-deny policy engine, tool disable, session revocation), 03, 04, 05 (rule 3 by name), 06
(rule 5 by name), 07 (`DisclosureText`, `ProhibitedClaims`), 11 (all five rules cited in `app.js`,
including the ten-second reveal), 023, 024, 026, 050 (rule 5), 060.
