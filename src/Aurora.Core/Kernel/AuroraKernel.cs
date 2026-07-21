using System.Text;
using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;
using Aurora.Core.Serialization;

namespace Aurora.Core.Kernel;

/// <summary>
/// The Aurora kernel. It is the sole authority that selects and commits an action: the reasoner
/// only proposes, and every effect passes validation → policy → consent → execute → audit, with an
/// idempotency reservation wrapping execution. Fail-closed throughout.
/// </summary>
public sealed class AuroraKernel
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly IReasoner _reasoner;
    private readonly ICapabilityRegistry _registry;
    private readonly ISchemaValidator _validator;
    private readonly IPolicyEngine _policy;
    private readonly IConsentGate _consent;
    private readonly ICapabilityExecutor _executor;
    private readonly IAuditStore _audit;
    private readonly IIdempotencyStore _idempotency;

    public AuroraKernel(
        IReasoner reasoner,
        ICapabilityRegistry registry,
        ISchemaValidator validator,
        IPolicyEngine policy,
        IConsentGate consent,
        ICapabilityExecutor executor,
        IAuditStore audit,
        IIdempotencyStore idempotency)
    {
        _reasoner = reasoner;
        _registry = registry;
        _validator = validator;
        _policy = policy;
        _consent = consent;
        _executor = executor;
        _audit = audit;
        _idempotency = idempotency;
    }

    public CatalogResult Catalog(string? query) => new(_registry.List(query));

    public async Task<ExecuteResponse> ExecuteAsync(ExecuteRequest request, Principal principal, CancellationToken ct)
    {
        // 1. Mode: objective XOR action_id.
        var hasObjective = !string.IsNullOrWhiteSpace(request.Objective);
        var hasAction = !string.IsNullOrWhiteSpace(request.ActionId);
        if (hasObjective && hasAction)
        {
            return Invalid(ErrorCodes.BothModes, "Provide either 'objective' or 'action_id', not both.");
        }

        if (!hasObjective && !hasAction)
        {
            return Invalid(ErrorCodes.NoMode, "Provide either 'objective' or 'action_id'.");
        }

        if (hasObjective && request.Objective!.Length > AuroraLimits.MaxObjectiveChars)
        {
            return Invalid(ErrorCodes.ObjectiveTooLong, "Objective exceeds the maximum length.");
        }

        if (request.IdempotencyKey is { Length: > AuroraLimits.MaxIdempotencyKeyChars })
        {
            return Invalid(ErrorCodes.KeyTooLong, "Idempotency key exceeds the maximum length.");
        }

        // 2. Resolve. The reasoner only proposes; the kernel commits below.
        string actionId;
        JsonElement input;
        double confidence;
        string via;
        if (hasAction)
        {
            actionId = request.ActionId!;
            input = request.Input ?? EmptyObject;
            confidence = 1.0;
            via = ResolutionVia.Explicit;
        }
        else
        {
            var proposal = await _reasoner.ProposeAsync(request.Objective!, _registry.List(null), ct).ConfigureAwait(false);
            if (proposal is null || string.IsNullOrWhiteSpace(proposal.ActionId))
            {
                return Invalid(ErrorCodes.ObjectiveUnavailable, "Objective-mode resolution is not available in this iteration.");
            }

            actionId = proposal.ActionId;
            input = proposal.Input ?? EmptyObject;
            confidence = proposal.Confidence;
            via = proposal.Via;
        }

        // 3. Capability must exist in the catalog.
        if (!_registry.TryGet(actionId, out var capability))
        {
            return Invalid(ErrorCodes.UnknownAction, $"Unknown action '{actionId}'.");
        }

        var descriptor = capability.Descriptor;

        // 3a. Size guard on the canonical input.
        var canonicalInput = CanonicalJson.Canonicalize(input);
        if (Encoding.UTF8.GetByteCount(canonicalInput) > AuroraLimits.MaxInputBytes)
        {
            return Invalid(ErrorCodes.InputTooLarge, "Input exceeds the maximum size.");
        }

        // 4. Schema validation. Unknown/extra fields are rejected by the schema (additionalProperties:false).
        var validation = _validator.Validate(descriptor.InputSchema, input);
        if (!validation.IsValid)
        {
            return Invalid(ErrorCodes.SchemaInvalid, "Input failed schema validation.", validation.Errors);
        }

        var resolved = new ResolvedAction(actionId, input, confidence, via);
        var inputHash = Hashing.Sha256Hex(canonicalInput);
        var requestHash = Hashing.Sha256Hex($"{actionId}\n{canonicalInput}");
        var key = request.IdempotencyKey;

        // 5. Idempotency reservation (only when a key is supplied).
        var reserved = false;
        if (!string.IsNullOrEmpty(key))
        {
            var begin = await _idempotency.BeginAsync(principal, key, requestHash, ct).ConfigureAwait(false);
            switch (begin.Disposition)
            {
                case IdempotencyDisposition.ReplayCompleted:
                case IdempotencyDisposition.ReplayFailed:
                    return DeserializeStored(begin.StoredResultJson)
                        ?? new ExecuteResponse(ExecuteStatus.Failed, resolved,
                            Error: new ExecuteError(ErrorCodes.UnknownState, "Stored idempotent result was unavailable."));

                case IdempotencyDisposition.InProgress:
                    return new ExecuteResponse(ExecuteStatus.InProgress, resolved,
                        Error: new ExecuteError(ErrorCodes.ExecutionInProgress, "A request with this idempotency key is already in progress."));

                case IdempotencyDisposition.Conflict:
                    return new ExecuteResponse(ExecuteStatus.Conflict, resolved,
                        Error: new ExecuteError(ErrorCodes.IdempotencyConflict, "Idempotency key reused with a different input."));

                case IdempotencyDisposition.Unknown:
                    return new ExecuteResponse(ExecuteStatus.Conflict, resolved,
                        Error: new ExecuteError(ErrorCodes.UnknownState, "Idempotency key is in an indeterminate state; reconciliation required."));

                case IdempotencyDisposition.Begin:
                default:
                    reserved = true;
                    break;
            }
        }

        // 6. Policy — fail-closed, evaluated with the input.
        var policy = _policy.Evaluate(descriptor, input, principal);
        if (!policy.Allowed)
        {
            return await TerminalAsync(
                principal, actionId, inputHash, "policy_denied", key, reserved, IdempotencyState.Failed,
                new ExecuteResponse(ExecuteStatus.Denied, resolved,
                    Error: new ExecuteError(ErrorCodes.PolicyDenied, policy.Reason ?? "Denied by policy."))).ConfigureAwait(false);
        }

        // 7. Consent — LOW auto; ≥MEDIUM requires It.2.
        var consent = _consent.Evaluate(descriptor, principal);
        if (!consent.Granted)
        {
            return await TerminalAsync(
                principal, actionId, inputHash, "consent_denied", key, reserved, IdempotencyState.Failed,
                new ExecuteResponse(ExecuteStatus.Denied, resolved, consent.Info,
                    Error: new ExecuteError(ErrorCodes.ConsentRequired, "Consent is required and not available in this iteration."))).ConfigureAwait(false);
        }

        // 8. Claim the reservation for execution. If we no longer own it, fail closed.
        if (reserved && !await _idempotency.MarkExecutingAsync(principal, key!, ct).ConfigureAwait(false))
        {
            return new ExecuteResponse(ExecuteStatus.InProgress, resolved,
                Error: new ExecuteError(ErrorCodes.ExecutionInProgress,
                    "The idempotency reservation is no longer owned by this request."));
        }

        // 9. Execute. ONLY the executor call is guarded; audit + settlement happen exactly once,
        //    outside the try, so a bookkeeping fault can never be mistaken for an execution failure.
        JsonElement result;
        try
        {
            result = await _executor.ExecuteAsync(capability, input, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The effect may have partially occurred; record an indeterminate outcome so a retry
            // neither replays a result nor reports a false in-progress. (Reconciliation is It.3.)
            await SettleIndeterminateAsync(principal, actionId, inputHash, resolved, key, reserved).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            return await TerminalAsync(
                principal, actionId, inputHash, "failed", key, reserved, IdempotencyState.Failed,
                new ExecuteResponse(ExecuteStatus.Failed, resolved, consent.Info,
                    Error: new ExecuteError(ErrorCodes.ExecutionFailed, "Execution failed."))).ConfigureAwait(false);
        }

        return await TerminalAsync(
            principal, actionId, inputHash, "completed", key, reserved, IdempotencyState.Completed,
            new ExecuteResponse(ExecuteStatus.Completed, resolved, consent.Info, result)).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends the audit entry and settles idempotency (if reserved) exactly once, then returns the
    /// response stamped with the audit reference. Bookkeeping is non-cancellable so a completed
    /// effect is always recorded even when the request itself was cancelled afterwards.
    /// </summary>
    private async Task<ExecuteResponse> TerminalAsync(
        Principal principal, string actionId, string inputHash, string outcome,
        string? key, bool reserved, string idempotencyState, ExecuteResponse response)
    {
        // Audit is the security record and must succeed (a failure here fails the call, fail-closed).
        var auditRef = await _audit.AppendAsync(
            principal.ClientId, principal.WindowsUser, actionId, inputHash, outcome, CancellationToken.None).ConfigureAwait(false);
        var stamped = response with { AuditRef = [auditRef] };

        if (reserved && key is not null)
        {
            try
            {
                await _idempotency.CompleteAsync(
                    principal, key, idempotencyState, AuroraJson.Serialize(stamped), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort settle: the effect is already audited and the caller gets the correct
                // outcome. A failed settle leaves the reservation for It.3 reconciliation; it can never
                // cause a double-execute or a false replay (a retry sees EXECUTING/ACCEPTED → in_progress).
            }
        }

        return stamped;
    }

    /// <summary>
    /// Best-effort settlement to an indeterminate terminal state after post-dispatch cancellation,
    /// so a replay receives a deterministic disposition instead of an eternal in-progress. Never
    /// masks the original cancellation with a bookkeeping fault.
    /// </summary>
    private async Task SettleIndeterminateAsync(
        Principal principal, string actionId, string inputHash, ResolvedAction resolved, string? key, bool reserved)
    {
        // Settle the reservation FIRST and independently — it is what prevents an eternal EXECUTING —
        // then record the audit entry. Neither failing may block the other, nor mask the cancellation.
        if (reserved && key is not null)
        {
            try
            {
                var response = new ExecuteResponse(ExecuteStatus.Failed, resolved,
                    Error: new ExecuteError(ErrorCodes.UnknownState, "Execution outcome is indeterminate."));
                await _idempotency.CompleteAsync(
                    principal, key, IdempotencyState.Unknown, AuroraJson.Serialize(response), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort; It.3 reconciliation is the backstop.
            }
        }

        try
        {
            await _audit.AppendAsync(
                principal.ClientId, principal.WindowsUser, actionId, inputHash, "unknown", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort; never mask the original cancellation with a bookkeeping fault.
        }
    }

    private static ExecuteResponse Invalid(string code, string message, IReadOnlyList<string>? details = null) =>
        new(ExecuteStatus.Invalid, Error: new ExecuteError(code, message, details));

    private static ExecuteResponse? DeserializeStored(string? json) =>
        string.IsNullOrEmpty(json) ? null : AuroraJson.Deserialize<ExecuteResponse>(json);
}
