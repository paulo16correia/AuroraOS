using System.Text.Json;
using Aurora.Adapters.Consent;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Kernel;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class KernelTests
{
    private const string EchoSchema =
        """{"type":"object","additionalProperties":false,"required":["message"],"properties":{"message":{"type":"string"}}}""";

    private static readonly Principal Caller = new("client-1", "user-1");

    private static FakeCapability EchoCapability()
    {
        var descriptor = FakeCapability.LowReadOnly("echo.say", EchoSchema);
        return new FakeCapability(descriptor, input =>
        {
            var message = input.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            return JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["said"] = message });
        });
    }

    private static AuroraKernel Build(
        FakeCapability capability,
        bool valid = true,
        bool allow = true,
        bool grant = true,
        ReasonerProposal? proposal = null,
        IIdempotencyStore? idempotency = null,
        RecordingAuditStore? audit = null,
        IConsentGate? consent = null,
        IApprovalStore? approvals = null) =>
        new(
            new FakeReasoner(proposal),
            new FakeRegistry(capability),
            new FakeValidator(valid),
            new FakePolicy(allow),
            consent ?? new FakeConsent(grant),
            approvals ?? new FakeApprovalStore(),
            new DirectExecutor(),
            audit ?? new RecordingAuditStore(),
            idempotency ?? new InMemoryIdempotencyStore());

    private static JsonElement Message(string text) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["message"] = text });

    [Fact]
    public async Task BothModes_IsInvalid()
    {
        var kernel = Build(EchoCapability());
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(Objective: "hi", ActionId: "echo.say"), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.BothModes, response.Error?.Code);
    }

    [Fact]
    public async Task NoMode_IsInvalid()
    {
        var kernel = Build(EchoCapability());
        var response = await kernel.ExecuteAsync(new ExecuteRequest(), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.NoMode, response.Error?.Code);
    }

    [Fact]
    public async Task UnknownAction_IsInvalid()
    {
        var kernel = Build(EchoCapability());
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "does.not.exist", Input: Message("x")), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.UnknownAction, response.Error?.Code);
    }

    [Fact]
    public async Task Objective_WithNoReasoner_IsUnavailable()
    {
        var kernel = Build(EchoCapability(), proposal: null);
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(Objective: "please echo"), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.ObjectiveUnavailable, response.Error?.Code);
    }

    [Fact]
    public async Task SchemaInvalid_IsRejected()
    {
        var kernel = Build(EchoCapability(), valid: false);
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("x")), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.SchemaInvalid, response.Error?.Code);
    }

    [Fact]
    public async Task PolicyDenied_IsDeniedAndAudited()
    {
        var audit = new RecordingAuditStore();
        var kernel = Build(EchoCapability(), allow: false, audit: audit);
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("x")), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Denied, response.Status);
        Assert.Equal(ErrorCodes.PolicyDenied, response.Error?.Code);
        Assert.Equal(["policy_denied"], audit.Outcomes);
    }

    [Fact]
    public async Task ConsentRefused_IsDenied()
    {
        var kernel = Build(EchoCapability(), grant: false);
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("x")), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Denied, response.Status);
        Assert.Equal(ErrorCodes.ConsentRequired, response.Error?.Code);
    }

    [Fact]
    public async Task Happy_Completes_WithResultAndAudit()
    {
        var audit = new RecordingAuditStore();
        var capability = EchoCapability();
        var kernel = Build(capability, audit: audit);

        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("hello")), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Completed, response.Status);
        Assert.Equal("explicit", response.Resolved?.Via);
        Assert.NotNull(response.Result);
        Assert.Equal("hello", response.Result!.Value.GetProperty("said").GetString());
        Assert.Equal(["completed"], audit.Outcomes);
        Assert.Equal(["audit-1"], response.AuditRef);
        Assert.Equal(1, capability.ExecuteCount);
    }

    [Fact]
    public async Task Idempotent_Replay_ReturnsStored_WithoutReExecuting()
    {
        var store = new InMemoryIdempotencyStore();
        var capability = EchoCapability();
        var kernel = Build(capability, idempotency: store);
        var request = new ExecuteRequest(ActionId: "echo.say", Input: Message("hi"), IdempotencyKey: "k1");

        var first = await kernel.ExecuteAsync(request, Caller, CancellationToken.None);
        var second = await kernel.ExecuteAsync(request, Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Completed, first.Status);
        Assert.Equal(ExecuteStatus.Completed, second.Status);
        Assert.Equal("hi", second.Result!.Value.GetProperty("said").GetString());
        Assert.Equal(1, capability.ExecuteCount); // second call served from the idempotency store
    }

    [Fact]
    public async Task Idempotent_Conflict_OnDifferentInput()
    {
        var store = new InMemoryIdempotencyStore();
        var kernel = Build(EchoCapability(), idempotency: store);

        await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("one"), IdempotencyKey: "k1"),
            Caller, CancellationToken.None);
        var conflict = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("two"), IdempotencyKey: "k1"),
            Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Conflict, conflict.Status);
        Assert.Equal(ErrorCodes.IdempotencyConflict, conflict.Error?.Code);
    }

    [Fact]
    public async Task InputTooLarge_IsInvalid()
    {
        var kernel = Build(EchoCapability());
        var big = new string('a', AuroraLimits.MaxInputBytes + 10);
        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message(big)), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.InputTooLarge, response.Error?.Code);
    }

    [Fact]
    public async Task Cancellation_DuringExecute_SettlesIndeterminate_AndReplayConflicts()
    {
        var store = new InMemoryIdempotencyStore();
        var cancelling = new FakeCapability(
            FakeCapability.LowReadOnly("boom.op", EchoSchema),
            _ => throw new OperationCanceledException());
        var kernel = Build(cancelling, idempotency: store);
        var request = new ExecuteRequest(ActionId: "boom.op", Input: Message("x"), IdempotencyKey: "kc");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => kernel.ExecuteAsync(request, Caller, CancellationToken.None));

        // The reservation is settled to an indeterminate state, so a replay is a deterministic
        // conflict rather than an eternal in-progress.
        var replay = await kernel.ExecuteAsync(request, Caller, CancellationToken.None);
        Assert.Equal(ExecuteStatus.Conflict, replay.Status);
        Assert.Equal(ErrorCodes.UnknownState, replay.Error?.Code);
    }

    // --- It.2: persistent approval (design/0002) ---

    [Fact]
    public async Task RequiresApproval_IsDenied_WithApprovalId_ButNotTerminal()
    {
        var consent = new FakeConsent(false, ConsentDecision.RequiresApproval, "appr-1");
        var kernel = Build(EchoCapability(), consent: consent);

        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("x")), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Denied, response.Status);
        Assert.Equal(ErrorCodes.ApprovalRequired, response.Error?.Code);
        Assert.Equal("appr-1", response.Consent?.ApprovalId);
    }

    [Fact]
    public async Task RequiresApproval_WithIdempotencyKey_AbandonsReservation_SoRetryStartsFresh()
    {
        var store = new InMemoryIdempotencyStore();
        var consent = new FakeConsent(false, ConsentDecision.RequiresApproval, "appr-1");
        var kernel = Build(EchoCapability(), consent: consent, idempotency: store);
        var request = new ExecuteRequest(ActionId: "echo.say", Input: Message("x"), IdempotencyKey: "k1");

        var first = await kernel.ExecuteAsync(request, Caller, CancellationToken.None);
        Assert.Equal(ExecuteStatus.Denied, first.Status);

        // The reservation was abandoned, not settled as failed, so a retry with the SAME key sees a
        // fresh Begin rather than an eternal ReplayFailed of the pre-approval denial.
        var begin = await store.BeginAsync(Caller, "k1", "irrelevant-hash-for-this-check", CancellationToken.None);
        Assert.Equal(IdempotencyDisposition.Begin, begin.Disposition);
    }

    [Fact]
    public async Task ApprovalRejected_IsDenied_Terminal_ConsentRequiredCode()
    {
        var consent = new FakeConsent(false, ConsentDecision.Denied, "appr-1");
        var audit = new RecordingAuditStore();
        var kernel = Build(EchoCapability(), consent: consent, audit: audit);

        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: Message("x")), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Denied, response.Status);
        Assert.Equal(ErrorCodes.ConsentRequired, response.Error?.Code);
        Assert.Equal(["consent_denied"], audit.Outcomes);
    }

    [Fact]
    public async Task Approve_Decided_ReturnsApprovalState_AndConsumesOnNextExecute()
    {
        var descriptor = new CapabilityDescriptor(
            "vault.write", "vault.write", "test capability",
            JsonDocument.Parse("""{"type":"object","additionalProperties":false,"properties":{}}""").RootElement.Clone(),
            ["writes"], RiskLevel.Medium, ApprovalRequired: true);
        var capability = new FakeCapability(descriptor, _ =>
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["ok"] = "true" }));

        var approvals = new FakeApprovalStore();
        var kernel = Build(capability, approvals: approvals, consent: new PersistentApprovalConsentGate(approvals));
        var request = new ExecuteRequest(ActionId: "vault.write", Input: JsonDocument.Parse("{}").RootElement);

        var denied = await kernel.ExecuteAsync(request, Caller, CancellationToken.None);
        Assert.Equal(ExecuteStatus.Denied, denied.Status);
        var approvalId = denied.Consent!.ApprovalId!;

        var decide = await kernel.ApproveAsync(
            new ApproveRequest(approvalId, ApprovalDecision.Approved), Caller, CancellationToken.None);
        Assert.Equal(ApproveStatus.Decided, decide.Status);
        Assert.Equal(ApprovalStatus.Approved, decide.ApprovalState);

        var retried = await kernel.ExecuteAsync(request, Caller, CancellationToken.None);
        Assert.Equal(ExecuteStatus.Completed, retried.Status);
    }

    [Fact]
    public async Task Approve_UnknownId_ReturnsNotFound()
    {
        var kernel = Build(EchoCapability());
        var response = await kernel.ApproveAsync(
            new ApproveRequest("nope", ApprovalDecision.Approved), Caller, CancellationToken.None);

        Assert.Equal(ApproveStatus.NotFound, response.Status);
        Assert.Equal(ErrorCodes.ApprovalNotFound, response.Error?.Code);
    }

    [Fact]
    public async Task Approve_MissingApprovalId_IsInvalid()
    {
        var kernel = Build(EchoCapability());
        var response = await kernel.ApproveAsync(
            new ApproveRequest(null, ApprovalDecision.Approved), Caller, CancellationToken.None);

        Assert.Equal(ApproveStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.ApprovalIdRequired, response.Error?.Code);
    }

    [Fact]
    public async Task Approve_InvalidDecisionWord_IsInvalid()
    {
        var kernel = Build(EchoCapability());
        var response = await kernel.ApproveAsync(
            new ApproveRequest("appr-1", "maybe"), Caller, CancellationToken.None);

        Assert.Equal(ApproveStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.InvalidDecision, response.Error?.Code);
    }

    [Fact]
    public async Task Objective_KeywordProposal_MayNotReachAMediumCapability()
    {
        // The adapter already refuses this, but the kernel must not rely on the adapter's manners:
        // a proposer that widened its own reach has to be stopped here.
        var descriptor = new CapabilityDescriptor(
            "vault.write", "vault.write", "test capability",
            JsonDocument.Parse("""{"type":"object","additionalProperties":false,"properties":{}}""").RootElement.Clone(),
            ["writes"], RiskLevel.Medium, ApprovalRequired: true);
        var capability = new FakeCapability(descriptor, _ => JsonSerializer.SerializeToElement(new { }));

        var proposal = new ReasonerProposal(
            "vault.write", JsonDocument.Parse("{}").RootElement, 0.4, ResolutionVia.Keyword);
        var kernel = Build(capability, proposal: proposal);

        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(Objective: "write to the vault"), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Invalid, response.Status);
        Assert.Equal(ErrorCodes.KeywordRestricted, response.Error!.Code);
        Assert.Equal(0, capability.ExecuteCount);
    }

    [Fact]
    public async Task Objective_ModelProposal_MayReachAMediumCapability_StillGatedByConsent()
    {
        // Restricting the *keyword* fallback must not restrict the model-backed path, which stays
        // subject to the normal policy and consent gates rather than to a resolution-mode rule.
        var descriptor = new CapabilityDescriptor(
            "vault.write", "vault.write", "test capability",
            JsonDocument.Parse("""{"type":"object","additionalProperties":false,"properties":{}}""").RootElement.Clone(),
            ["writes"], RiskLevel.Medium, ApprovalRequired: true);
        var capability = new FakeCapability(descriptor, _ => JsonSerializer.SerializeToElement(new { }));

        var approvals = new FakeApprovalStore();
        var proposal = new ReasonerProposal(
            "vault.write", JsonDocument.Parse("{}").RootElement, 0.9, ResolutionVia.Reasoner);
        var kernel = Build(
            capability, proposal: proposal, approvals: approvals,
            consent: new PersistentApprovalConsentGate(approvals));

        var response = await kernel.ExecuteAsync(
            new ExecuteRequest(Objective: "write to the vault"), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Denied, response.Status);
        Assert.Equal(ErrorCodes.ApprovalRequired, response.Error!.Code);
        Assert.Equal(0, capability.ExecuteCount);
    }

    [Fact]
    public async Task Objective_KeywordProposal_IsAcceptedForLowReadOnly()
    {
        var capability = EchoCapability();
        var proposal = new ReasonerProposal(
            "echo.say",
            JsonDocument.Parse("""{"message":"hi"}""").RootElement,
            0.4,
            ResolutionVia.Keyword);

        var response = await Build(capability, proposal: proposal)
            .ExecuteAsync(new ExecuteRequest(Objective: "say hi"), Caller, CancellationToken.None);

        Assert.Equal(ExecuteStatus.Completed, response.Status);
        Assert.Equal(ResolutionVia.Keyword, response.Resolved!.Via);
    }

    [Fact]
    public async Task Audit_RecordsWhyNotJustWhat()
    {
        var audit = new RecordingAuditStore();
        var capability = EchoCapability();
        var kernel = Build(capability, audit: audit);

        await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: JsonDocument.Parse("""{"message":"hi"}""").RootElement),
            Caller,
            CancellationToken.None);

        AuditEntry entry = Assert.Single(audit.Entries);
        Assert.Equal("completed", entry.Outcome);
        Assert.Equal("Low", entry.Risk);
        Assert.Equal(ResolutionVia.Explicit, entry.Via);
        Assert.False(string.IsNullOrEmpty(entry.Decision));
    }

    [Fact]
    public async Task Audit_PolicyDenial_CarriesThePolicyIdAndReason()
    {
        var audit = new RecordingAuditStore();
        var kernel = Build(EchoCapability(), allow: false, audit: audit);

        await kernel.ExecuteAsync(
            new ExecuteRequest(ActionId: "echo.say", Input: JsonDocument.Parse("""{"message":"hi"}""").RootElement),
            Caller,
            CancellationToken.None);

        AuditEntry entry = Assert.Single(audit.Entries);
        Assert.Equal("policy_denied", entry.Outcome);
        Assert.False(string.IsNullOrEmpty(entry.Reason));
    }

    [Fact]
    public async Task Audit_ReasonerResolution_IsDistinguishableFromExplicit()
    {
        // The point of recording `via`: after the fact you must be able to tell whether a human
        // named the action or an untrusted model picked it.
        var audit = new RecordingAuditStore();
        var capability = EchoCapability();
        var proposal = new ReasonerProposal(
            "echo.say", JsonDocument.Parse("""{"message":"hi"}""").RootElement, 0.9, ResolutionVia.Reasoner);

        await Build(capability, proposal: proposal, audit: audit)
            .ExecuteAsync(new ExecuteRequest(Objective: "greet"), Caller, CancellationToken.None);

        Assert.Equal(ResolutionVia.Reasoner, Assert.Single(audit.Entries).Via);
    }
}
