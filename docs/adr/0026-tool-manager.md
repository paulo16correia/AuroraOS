# Design 0026 — Tool Manager (step 8b)

**Status:** Implemented · **Date:** 2026-08-24
**Implements:** RFC 06 · **Completes:** step 8 of `docs/100-implementation-order.md`

## `UNKNOWN` is the point

RFC 06's own justification names it: *in distributed systems, "we did not receive a response" does
not mean "it did not happen".*

A call that times out **after dispatch** becomes `UNKNOWN`, keeps whatever external reference it
has, and is not executed again. Executing an `UNKNOWN` call is refused outright. The only way
forward is `ReconcileAsync`, which asks the remote side what became of it — a test asserts the
connector was dispatched exactly once across a timeout and a reconcile.

Automatically resending after a timeout is the single most tempting thing to do here, and it is how
one email becomes two.

## Rule 3: the external result is untrusted

A connector's output is validated against the manifest's output schema before any of it is believed.
Three separate failures are distinguished, because they mean different things to whoever
investigates: `output_schema_invalid` (the remote side changed shape), `output_not_json` (something
is very wrong) and `output_too_large` (rule 4's limit, so a remote side cannot flood us).

None of them become `SUCCEEDED`. A payload that does not match its contract is a defect to surface,
not something to read loosely.

## Rule 2: a writing tool needs an idempotency key

Refused at proposal, not at execution. A tool with effects and no key leaves reconcile with no way
to tell "do it" from "do it again", which is precisely the situation `UNKNOWN` exists to survive.
A read-only tool needs no key, so the rule stays about effects rather than ceremony.

## Rule 5: a connector cannot use another's credential

The manager leases from the vault using `(callId, toolId)`, and the vault already enforces
`allowed_tool_ids` (`docs/adr/0014`). A connector that names another tool's secret reference is
refused by the vault, and a test does exactly that.

The connector receives an `EphemeralSecretHandle`, never a value: single-use, cleared on dispose,
and redacted when printed. The two increments compose without either knowing about the other.

## Rule 1: no implicit access

A tool declaring no capability is refused at registration. A call for a capability the manifest does
not list is refused at proposal. There is no path by which a connector offers more than it
described, which is what "no `shell.execute_anything`" means in practice.

Inputs are validated and stored redacted, with a hash of the original for audit.

## Rate limits defer, they do not spin

A tool over its limit is `QUEUED` with a `retry_after`, rather than retried immediately. RFC 06
forbids tight repetitions and a queue with a time on it is the whole difference.

## A changed remote schema disables the tool

`DisableAsync` records a reason and every later proposal is refused with it. The RFC asks for the
affected capability to be disabled and the operator alerted; refusing with the reason attached is
the smallest honest version of that.

## Tests

20 conformance tests: a tool with no capability refused; an undeclared capability refused; a writing
tool without an idempotency key refused and a read-only one allowed; invalid input refused; stored
input redacted; execution without authorization refused; authorization needing a policy decision and
an approval where required; output failing schema, not JSON, and oversized all failing the call; a
valid output succeeding; a timeout becoming `UNKNOWN` and never resent; reconcile resolving it with
one dispatch; only `UNKNOWN` calls reconcilable; a rate limit queueing with a retry time; a
connector receiving its own secret and refused another's; and a disabled tool refusing calls.

## Step 8 is complete, and the gate is half-wired

The gate for steps 8–9 reads *"capability does not couple to provider; each action generates
Observation and reconciles `UNKNOWN`"*.

- **Capability does not couple to provider** — `docs/adr/0025`.
- **Reconciles `UNKNOWN`** — here.
- **Each action generates Observation** — the cognitive cycle already refuses to close an executed
  cycle without Observation and Reflection (`docs/adr/0023`). The two halves exist; **wiring the
  tool manager into the cycle is step 10 work**, and until then the guarantee holds in each piece
  rather than across them. Stated plainly because a gate that is nearly met is not met.

## The frozen capabilities stay frozen

`files.write_sandbox` and `files.read_sandbox` are still off (`docs/adr/0012`). Step 8 was where
they could return, and the mandatory conditions from `docs/reviews/architecture-review-v1.0.md` are
not all satisfied:

| Condition | State |
| --- | --- |
| Laws 001–007 as compliance tests | **not done** |
| Kernel independent of Mind semantics | holds — there is no Mind dependency |
| Outbox, idempotence, DLQ, `UNKNOWN` reconciliation | done |
| Isolated snapshot and restore | done |
| Published event contracts and authorization matrices per capability | **partial** — manifests and resolution reports give the authorization matrix; event contracts per capability are not published |

Unfreezing before those two are closed would be doing exactly what the re-baseline was for.

## Deferred

RFC 06's future expansions: browser sandbox, composite capabilities, a signed marketplace, connector
compliance tests and short-lived OAuth delegation. Cancellation is on the connector interface and
not yet driven by the manager. Rate limiting counts dispatches in the last minute rather than using
a token bucket.

## Next

Laws 001–007 as compliance tests, which is the largest remaining condition and belongs before any
external-effect capability is offered again.
