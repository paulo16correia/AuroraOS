# Design 0004 — Reasoner (It.1)

**Status:** Superseded in part · **Date:** 2026-08-23
**Depends on:** `docs/adr/0001-mcp-pipeline-slice1.md`
**Superseded in part by:** `docs/adr/0051-no-model-proposer.md` — the `AzureOpenAiReasoner`
described below was removed on 2026-08-26. The `KeywordReasoner` half of this design stands.

## Objective

Until now `NullReasoner` always returned `null`, so half of the public surface of
`aurora_execute` — natural-language `objective` mode — was advertised and did
nothing. This increment implements it.

## Divergence from design 0001: no `Azure.AI.OpenAI`

Design 0001 lists `Azure.AI.OpenAI` among the dependencies. **It was not added.**
The same design demands "pinned versions with a supply-chain verdict BEFORE any
`restore`" and builds in `--locked-mode`; the SDK's transitive tree has not been
audited, and adding it without vetting contradicts the repository's most explicit
rule.

The adapter speaks REST over an `HttpClient`: a single POST, no new dependencies,
and an injected handler makes it testable offline. If a supply-chain verdict for
the SDK arrives later, the swap is local to one class.

## Two proposers, chained

`CompositeReasoner` tries each in order and returns the first proposal.

**1. `AzureOpenAiReasoner`** — joins the composition only when endpoint,
deployment and key are all configured. It asks for strict JSON
(`{action_id, input, confidence}`) at temperature 0. Any transport failure,
non-2xx response, malformed JSON, unexpected envelope, or refusal by the model
yields `null` rather than an exception — the caller sees "objective mode
unavailable" instead of a half-understood action.

One decision that looks counter-intuitive: when the model proposes an `action_id`
that is not in the catalog, the adapter **passes the proposal through anyway**.
Filtering it here would produce a silent "I couldn't"; letting it through makes
the Kernel answer `unknown_action`, which is honest diagnostics.

**2. `KeywordReasoner`** — the offline fallback, deliberately timid. It considers
only LOW capabilities with no effects. It proposes only when it can build an input
the schema describes: an empty object when nothing is required, or the leftover
text when exactly one required string field is expected. Anything more would be
inventing argument values, so it declines.

This means an install with no Azure configuration degrades `objective` mode to
LOW read-only actions rather than losing it entirely.

## The LOW restriction belongs to the Kernel, not the adapter

Design 0001 says "keyword fallback restricted to LOW". `KeywordReasoner` honours
it, but the Kernel **checks again**: a proposal with `via = keyword` pointing at
anything above LOW, or with effects, is refused with
`keyword_resolution_restricted`.

The reasoner is untrusted by definition — that is the premise of the whole
architecture. A security invariant that lives only inside the untrusted component
is not an invariant. A future proposer that widens its own reach is stopped in the
right place.

The restriction applies to the *resolution mode*, not to the model: a proposal
with `via = reasoner` may reach a MEDIUM capability and is then subject to the
normal policy and consent gates, like anything else.

## Prompt injection

The system prompt states explicitly that the objective text is data and never
instructions. This is mitigation, not a guarantee — the real defence is
structural: the model only proposes, and the Kernel validates catalog membership,
canonical size, schema, policy and consent before any effect. A malicious
objective that convinces the model to propose `files.write_sandbox` still needs
explicit human approval.

## Configuration

`Aurora:AzureOpenAI:Endpoint`, `:Deployment`, `:ApiKey` (or the
`AZURE_OPENAI_API_KEY` variable), `:ApiVersion` (default `2024-10-21`). If any of
the first three is missing, the model-backed proposer is not registered.

## Tests

18 new tests, all offline. Keyword: an action with no input, filling the single
required field, refusing MEDIUM, declining when it would have to invent the
argument, and no match at all. Azure with a stub handler: the happy parse, the
`api-key` header and deployment URL, 401/500, an unusable envelope, the model
refusing or rambling, and an unknown action passed through for the Kernel to
reject. Kernel: keyword blocked at MEDIUM, `reasoner` allowed to reach it and
stopping at consent, keyword accepted at LOW. Integration: `objective` resolves
via keyword with no model configured.

## Deferred

Validating the adapter against a real Azure OpenAI service (needs credentials);
choosing model or deployment by action risk; retries and a circuit breaker; token
and cost accounting (the per-session ceiling belongs to the full It.2).
