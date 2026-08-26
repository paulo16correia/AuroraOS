# Design 0051 — Aurora proposes with its own catalogue, or not at all

**Status:** Decided by the owner · **Date:** 2026-08-26
**Supersedes in part:** `docs/adr/0004-it1-reasoner.md` (the `AzureOpenAiReasoner` half)

## The question that ended it

> "mas porque que tá aí o Azure OpenAI? para que serve no mcp?"
> "tava nos docs? sim ou não?"

No. `Azure.AI.OpenAI` appears once in design 0001, in a dependency list, and design 0004 already
recorded that the package was never added. What design 0004 did instead was write a REST adapter
against the same service, and that adapter is what has been sitting in `src/Aurora.Adapters/Reasoning`
ever since. No RFC asks for it. Nothing in the specification describes a model call inside Aurora.

## Why it should not have been there

Aurora is an MCP server. The thing on the other end of the protocol **is already a language model**,
and it is the component whose job is to turn a sentence into an intention (`docs/10-api.md`,
RFC 045). When that client sends `objective` instead of `action_id`, a second model inside Aurora
does not add understanding — it adds a second opinion that Aurora has no way to check, produced by
a party Aurora cannot audit, reached over a network connection from a machine whose entire premise
is that it does not need one (`docs/adr/0045`).

It also could not be tested. Design 0050 recorded it as one of two things that had never run: there
is no endpoint and no key here, so the live path had never executed once. A component that ships
untested, unspecified, and contrary to the local-only decision is not a feature waiting for
configuration. It is something to remove.

## What was removed

- `AzureOpenAiReasoner` and `AzureOpenAiOptions`.
- `AuroraServerOptions.AzureOpenAi` and the configuration block that read
  `Aurora:AzureOpenAI:Endpoint`, `:Deployment`, `:ApiKey`, `AZURE_OPENAI_API_KEY` and `:ApiVersion`.
- The `IReasoner` composition branch and `services.AddHttpClient()`, which existed only to serve it.
- Thirteen tests that exercised the adapter against a stub handler.

`ServiceRegistration` now registers one proposer. The only `HttpClient` left in the source tree is
in `OperationsConsole`, which calls Aurora's own loopback health endpoint.

## What remains

`KeywordReasoner` matches an objective against Aurora's own capability catalogue, fills a single
required string argument from the remaining words, and **declines rather than inventing one**. It
never proposes anything above `LOW`. Objective mode therefore still works for the low-risk reads it
was always safe for, and everything else needs an explicit `action_id` — which is what a competent
MCP client sends anyway.

`CompositeReasoner` stays with a single entry. It is the seam where a *local* proposer would go, and
it is the shape that keeps a proposal a suggestion: the kernel resolves, authorizes and commits on
its own terms regardless of who proposed.

## The cost, stated plainly

An objective phrased in a way the keyword matcher does not recognise now resolves to nothing, and
Aurora answers that it could not resolve it. Before this change it *also* resolved to nothing on
every installation in existence, because none had a deployment configured. The difference is that
the failure is now the designed behaviour instead of a degraded one.

## Consequences

Aurora makes no outbound network call, on any code path, in any configuration. That was true in
practice; it is now true by construction, and `docs/adr/0045` no longer needs an asterisk.
