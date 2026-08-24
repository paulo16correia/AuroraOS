using System.Globalization;
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
    private readonly IApprovalStore _approvals;
    private readonly ICapabilityExecutor _executor;
    private readonly IAuditStore _audit;
    private readonly IIdempotencyStore _idempotency;
    private readonly IAuroraMetrics _metrics;
    private readonly IPassphraseAuthenticator _passphrase;

    public AuroraKernel(
        IReasoner reasoner,
        ICapabilityRegistry registry,
        ISchemaValidator validator,
        IPolicyEngine policy,
        IConsentGate consent,
        IApprovalStore approvals,
        ICapabilityExecutor executor,
        IAuditStore audit,
        IIdempotencyStore idempotency,
        IAuroraMetrics metrics,
        IPassphraseAuthenticator passphrase)
    {
        _reasoner = reasoner;
        _registry = registry;
        _validator = validator;
        _policy = policy;
        _consent = consent;
        _approvals = approvals;
        _executor = executor;
        _audit = audit;
        _idempotency = idempotency;
        _metrics = metrics;
        _passphrase = passphrase;
    }

    /// <summary>
    /// Why the kernel decided as it did, attached to every audit record (docs/adr/0006).
    /// Nullable throughout: a request rejected before resolution has no risk or via to report.
    /// </summary>
    private readonly record struct AuditFacts(
        string? Risk = null,
        string? Via = null,
        string? Decision = null,
        string? PolicyIds = null,
        string? Reason = null);

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

        // 3b. The keyword fallback is blunt and untrusted: design 0001 confines it to LOW,
        //     effect-free actions. Enforced here as well as in the adapter, so a future
        //     proposer cannot quietly widen its own reach.
        if (via == ResolutionVia.Keyword
            && (descriptor.Risk != RiskLevel.Low || descriptor.Effects.Count > 0))
        {
            return Invalid(
                ErrorCodes.KeywordRestricted,
                "Keyword resolution is limited to low-risk, read-only actions.");
        }

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
                    _metrics.IdempotencyConflict();
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
                new AuditFacts(
                    descriptor.Risk.ToString(), via, PolicyIds: string.Join(',', policy.PolicyIds),
                    Reason: policy.Reason),
                new ExecuteResponse(ExecuteStatus.Denied, resolved,
                    Error: new ExecuteError(ErrorCodes.PolicyDenied, policy.Reason ?? "Denied by policy."))).ConfigureAwait(false);
        }

        // 7. Consent — LOW auto; approval-gated capabilities go through the persisted approval
        //    ledger (It.2, first increment); everything else at MEDIUM+ stays refused.
        var consent = await _consent.EvaluateAsync(descriptor, input, requestHash, principal, ct).ConfigureAwait(false);
        if (!consent.Granted)
        {
            var isRetryable = consent.Info.Decision == ConsentDecision.RequiresApproval;
            var deniedResponse = new ExecuteResponse(ExecuteStatus.Denied, resolved, consent.Info,
                Error: new ExecuteError(
                    isRetryable ? ErrorCodes.ApprovalRequired : ErrorCodes.ConsentRequired,
                    isRetryable
                        ? "Approval is required. Call aurora_approve with the returned approval_id, then retry this exact request."
                        : "Consent was denied for this request."));

            // A retryable denial abandons the idempotency reservation instead of settling it as a
            // terminal failure, so a retry after the approval is decided starts fresh rather than
            // replaying this denial forever (It.2 design note, docs/adr/0002).
            var deniedFacts = new AuditFacts(
                descriptor.Risk.ToString(), via, consent.Info.Decision, string.Join(',', policy.PolicyIds));

            return isRetryable
                ? await AbandonedAsync(
                    principal, actionId, inputHash, key, reserved, deniedFacts, deniedResponse).ConfigureAwait(false)
                : await TerminalAsync(
                    principal, actionId, inputHash, "consent_denied", key, reserved, IdempotencyState.Failed,
                    deniedFacts, deniedResponse).ConfigureAwait(false);
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
            await SettleIndeterminateAsync(
                principal, actionId, inputHash, resolved, key, reserved,
                new AuditFacts(
                    descriptor.Risk.ToString(), via, consent.Info.Decision,
                    string.Join(',', policy.PolicyIds))).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            return await TerminalAsync(
                principal, actionId, inputHash, "failed", key, reserved, IdempotencyState.Failed,
                new AuditFacts(
                    descriptor.Risk.ToString(), via, consent.Info.Decision, string.Join(',', policy.PolicyIds)),
                new ExecuteResponse(ExecuteStatus.Failed, resolved, consent.Info,
                    Error: new ExecuteError(ErrorCodes.ExecutionFailed, "Execution failed."))).ConfigureAwait(false);
        }

        return await TerminalAsync(
            principal, actionId, inputHash, "completed", key, reserved, IdempotencyState.Completed,
            new AuditFacts(
                descriptor.Risk.ToString(), via, consent.Info.Decision, string.Join(',', policy.PolicyIds)),
            new ExecuteResponse(ExecuteStatus.Completed, resolved, consent.Info, result)).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends the audit entry and settles idempotency (if reserved) exactly once, then returns the
    /// response stamped with the audit reference. Bookkeeping is non-cancellable so a completed
    /// effect is always recorded even when the request itself was cancelled afterwards.
    /// </summary>
    private async Task<ExecuteResponse> TerminalAsync(
        Principal principal, string actionId, string inputHash, string outcome,
        string? key, bool reserved, string idempotencyState, AuditFacts facts, ExecuteResponse response)
    {
        _metrics.ExecutionSettled(outcome);

        // Audit is the security record and must succeed (a failure here fails the call, fail-closed).
        string auditRef;
        try
        {
            auditRef = await _audit.AppendAsync(
                Entry(principal, actionId, inputHash, outcome, facts), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Counted before rethrowing: a spike here means the security record is degrading, which
            // an operator must see even though the call itself still fails closed.
            _metrics.AuditFailure();
            throw;
        }
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
    /// Records the audit entry for a "requires approval" outcome and releases the idempotency
    /// reservation (rather than settling it as a terminal failure), so a retry after the approval
    /// is decided starts a fresh reservation instead of forever replaying this denial.
    /// </summary>
    private async Task<ExecuteResponse> AbandonedAsync(
        Principal principal, string actionId, string inputHash, string? key, bool reserved,
        AuditFacts facts, ExecuteResponse response)
    {
        var auditRef = await _audit.AppendAsync(
            Entry(principal, actionId, inputHash, "requires_approval", facts), CancellationToken.None)
            .ConfigureAwait(false);
        var stamped = response with { AuditRef = [auditRef] };

        if (reserved && key is not null)
        {
            try
            {
                await _idempotency.AbandonAsync(principal, key, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: a stale ACCEPTED row just makes an immediate retry see in_progress
                // until it is naturally superseded; it can never cause a false replay or a double-execute.
            }
        }

        return stamped;
    }

    /// <summary>
    /// Decides a pending approval on behalf of the caller. Distinct from <see cref="ExecuteAsync"/>:
    /// it never touches a capability, policy or the executor — only the approval ledger and audit.
    /// </summary>
    public async Task<ApproveResponse> ApproveAsync(ApproveRequest request, Principal principal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApprovalId))
        {
            return new ApproveResponse(ApproveStatus.Invalid,
                Error: new ExecuteError(ErrorCodes.ApprovalIdRequired, "approval_id is required."));
        }

        bool approve;
        if (string.Equals(request.Decision, ApprovalDecision.Approved, StringComparison.Ordinal))
        {
            approve = true;
        }
        else if (string.Equals(request.Decision, ApprovalDecision.Rejected, StringComparison.Ordinal))
        {
            approve = false;
        }
        else
        {
            return new ApproveResponse(ApproveStatus.Invalid,
                Error: new ExecuteError(ErrorCodes.InvalidDecision, "decision must be 'approved' or 'rejected'."));
        }

        // A decision must be a human act. aurora_approve is an MCP tool, so without a secret the
        // agent does not hold, an untrusted reasoner could approve its own request and the whole
        // gate would be decoration (docs/adr/0011). Checked before the decision is applied, and
        // required for a rejection too — otherwise the agent could bury a request a human wanted.
        if (_passphrase.IsEnrolled)
        {
            PassphraseCheck check = _passphrase.Verify(request.Passphrase);
            switch (check.Outcome)
            {
                case PassphraseOutcome.LockedOut:
                    return new ApproveResponse(ApproveStatus.Invalid, request.ApprovalId,
                        Error: new ExecuteError(
                            ErrorCodes.PassphraseLockedOut,
                            "Too many failed attempts; approvals are locked out."));

                case PassphraseOutcome.Rejected:
                    return new ApproveResponse(ApproveStatus.Invalid, request.ApprovalId,
                        Error: new ExecuteError(
                            string.IsNullOrEmpty(request.Passphrase)
                                ? ErrorCodes.PassphraseRequired
                                : ErrorCodes.PassphraseInvalid,
                            string.IsNullOrEmpty(request.Passphrase)
                                ? "This deployment requires the operator passphrase to decide an approval."
                                : "The operator passphrase is not valid."));
            }
        }

        var result = await _approvals.DecideAsync(principal, request.ApprovalId, approve, ct).ConfigureAwait(false);

        // How long the caller waited for a human. Measured from the record itself rather than a
        // timer, so a decision that spans a restart is still counted correctly.
        if (result is { Outcome: ApprovalDecideOutcome.Decided, Record: { DecidedAtUtc: { } decidedAt } record }
            && DateTimeOffset.TryParse(
                record.CreatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdUtc)
            && DateTimeOffset.TryParse(
                decidedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var decidedUtc))
        {
            _metrics.ConsentDecided(decidedUtc - createdUtc);
        }

        var outcome = result.Outcome switch
        {
            ApprovalDecideOutcome.Decided => approve ? "approval_granted" : "approval_rejected",
            ApprovalDecideOutcome.NotPending => "approval_not_pending",
            _ => "approval_not_found",
        };
        var auditRef = await _audit.AppendAsync(
            new AuditEntry(
                principal.ClientId, principal.OsUser,
                result.Record?.ActionId ?? "approval.decide",
                Hashing.Sha256Hex(request.ApprovalId), outcome,
                Via: "approval", Decision: approve ? "approved" : "rejected"),
            CancellationToken.None).ConfigureAwait(false);

        return result.Outcome switch
        {
            ApprovalDecideOutcome.Decided => new ApproveResponse(
                ApproveStatus.Decided, request.ApprovalId, result.Record!.Status, result.Record.ActionId, [auditRef]),
            ApprovalDecideOutcome.NotPending => new ApproveResponse(
                ApproveStatus.NotPending, request.ApprovalId, AuditRef: [auditRef],
                Error: new ExecuteError(ErrorCodes.ApprovalNotPending, "The approval is not pending (already decided or expired).")),
            _ => new ApproveResponse(
                ApproveStatus.NotFound, request.ApprovalId, AuditRef: [auditRef],
                Error: new ExecuteError(ErrorCodes.ApprovalNotFound, "No pending approval with this id belongs to the caller.")),
        };
    }

    /// <summary>
    /// Best-effort settlement to an indeterminate terminal state after post-dispatch cancellation,
    /// so a replay receives a deterministic disposition instead of an eternal in-progress. Never
    /// masks the original cancellation with a bookkeeping fault.
    /// </summary>
    private async Task SettleIndeterminateAsync(
        Principal principal, string actionId, string inputHash, ResolvedAction resolved, string? key,
        bool reserved, AuditFacts facts)
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

        _metrics.ExecutionUnknown();

        try
        {
            await _audit.AppendAsync(
                Entry(principal, actionId, inputHash, "unknown", facts), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort; never mask the original cancellation with a bookkeeping fault.
            _metrics.AuditFailure();
        }
    }

    private static AuditEntry Entry(
        Principal principal, string actionId, string inputHash, string outcome, AuditFacts facts) =>
        new(principal.ClientId, principal.OsUser, actionId, inputHash, outcome,
            facts.Risk, facts.Via, facts.Decision, facts.PolicyIds, facts.Reason);

    private static ExecuteResponse Invalid(string code, string message, IReadOnlyList<string>? details = null) =>
        new(ExecuteStatus.Invalid, Error: new ExecuteError(code, message, details));

    private static ExecuteResponse? DeserializeStored(string? json) =>
        string.IsNullOrEmpty(json) ? null : AuroraJson.Deserialize<ExecuteResponse>(json);
}
