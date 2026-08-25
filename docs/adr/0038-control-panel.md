# Design 0038 — The control panel

**Status:** Implemented, partial · **Date:** 2026-08-25
**Implements:** `docs/11-ui.md`
**Completes:** the half of step 10 that was left undone

## What was missing

Step 10 of the frozen order reads "API **and** control/approvals/audit UI". The API was built and
the step was closed; the UI was not built and that was not said. This is that half.

## The credential is the design

The bearer token belongs to the MCP client. If the panel had accepted only that, then everything
the panel can do — approve, correct, forget, revoke — would be something the agent could do to
itself by calling the endpoint instead of the tool. The passphrase gate (ADR 0011) covers approvals
when one is enrolled, and covers nothing when one is not.

So there are two credentials now:

- The **bearer token**, which the agent holds.
- An **operator session**, minted on the server's own console and exchanged for an HttpOnly,
  SameSite=Strict cookie.

Reading is open to both — an agent that cannot read the audit log cannot explain itself. **Deciding
requires the session**: `POST /v1/approvals/{id}/decide`, `PATCH /v1/memories/{id}` and
`DELETE /v1/memories/{id}` refuse the bearer token with 403. There are tests that hold that line
from both sides.

The link is single-use and expires in ten minutes, because a link that keeps working keeps working
for whoever finds it in a shell history or a screenshot. Sessions live in memory, so a restart ends
every one of them — a session that outlived the process would be a standing grant nobody remembers
issuing.

## The five mandatory rules

**1 — five states must not look alike.** Generated, draft, proposed, ongoing and completed each get
a badge whose *word* carries the meaning and whose *border style* repeats it. Colour reinforces and
never decides, so the distinction survives a colourblind reader and a monochrome screen.

**2 — approval cards say what would happen.** What, to what, what it discloses, how long it stands,
and a refusal that is a plain button rather than an absence of one. No "OK"/"Cancel": the labels
name the act.

**3 — memory shows origin, trust and lifecycle.** Every card carries where it came from, what
evidence supports it, what it is anchored to, who recorded it, its confidence and its status — next
to the two buttons that correct or retract it. A claim without its source is not checkable, so they
are never shown apart.

**4 — sensitive material is hidden until an explicit, temporary gesture.** Classified values render
as dots with a "Reveal for 10s" button, and the reveal times *itself* out. A panel left open on a
desk stops showing it without anyone remembering to close it.

**5 — never imply background activity.** The panel shows a running cycle only when the server
reports one, and an empty list says so in words: "A running cycle would appear here. Nothing is
running."

## The limit cases

**Stale view.** The header states how old the data is and turns to "reload before deciding anything"
after a minute. Correcting or forgetting on a stale view is refused client-side and the operator is
told why — deciding on a view that might already be wrong is the error the rule exists to prevent.

**Two windows approve.** Handled below the UI and always was: an approval is consumed one-time in
the ledger, so the second window gets a conflict rather than a second execution.

**Accessibility.** Keyboard throughout, arrow-key movement between tabs, a skip link, a live region
that announces every outcome, visible focus rings, and nothing signalled by colour alone.

**Degraded API.** A failed load switches the panel to a read-only banner and disables every deciding
button, because an action the server cannot confirm must not be accepted.

## No framework, no build step

Three embedded files: HTML, CSS, one script. No npm, no bundler, no dependency tree to vet — which
matters more here than anywhere else in the repository, since this is the page in front of a person
who is about to approve something. The CSP is `default-src 'none'` with `'self'` for script and
style and `frame-ancestors 'none'`, and the assets are embedded in the assembly rather than read
from disk, so a file dropped next to the binary cannot become script on the approval page.

## What this is not, yet

It is a **control panel**, not the whole of RFC 11. There is no conversation view — Aurora's
conversational surface is the LLM client, and building a second one here would be inventing a
product decision the RFC does not make. Goals are listed through the needs that produced them
rather than with their own editing surface. The graph viewer, the decision timeline and
notification rules are named by the RFC as future expansions and remain so.

## A bug the panel found

Memory load read as **0%**, which is not a plausible measurement on a running machine.
`GCMemoryInfo.MemoryLoadBytes` is populated by the garbage collector and reads zero until the first
collection — so the probe was reporting a measurement nobody had taken. It is unmeasured now, which
is what the whole unmeasured mechanism exists for. Seeing it took looking at the panel; no test was
going to notice a plausible-looking number.
