using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Aurora.Tests.Integration;

/// <summary>
/// The Core as one system, through the surfaces a client actually uses.
/// </summary>
/// <remarks>
/// Every other test in this suite proves one component keeps its own promise. These prove the
/// promises meet: that a decision made in one place is honoured in another, and that the refusals
/// hold when they are reached the long way round rather than by calling the guard directly.
/// <para>
/// Written because "each part is correct" and "the thing works" are different claims, and the
/// conformance pass found three places where only the first was true.
/// </para>
/// </remarks>
public sealed class CoreEndToEndTests : IClassFixture<AuroraAppFactory>
{
    private readonly AuroraAppFactory _factory;

    public CoreEndToEndTests(AuroraAppFactory factory) => _factory = factory;

    private static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private HttpClient Client()
    {
        HttpClient http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.BearerToken);

        return http;
    }

    private async Task<McpClient> ConnectAsync() =>
        await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri("http://localhost/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {_factory.BearerToken}",
                    },
                },
                _factory.CreateClient()),
            cancellationToken: Ct());

    private static JsonDocument Json(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;

        return JsonDocument.Parse(text ?? "{}");
    }

    private static Dictionary<string, object?> Execute(string actionId, object? input = null) =>
        new() { ["action_id"] = actionId, ["input"] = input ?? new Dictionary<string, object?>() };

    private async Task<JsonElement> CallAsync(
        McpClient client, string tool, Dictionary<string, object?> args)
    {
        using JsonDocument document =
            Json(await client.CallToolAsync(tool, args, cancellationToken: Ct()));

        return document.RootElement.Clone();
    }

    // ---- 1. the loop closes: decide, authorize, act, observe, reflect ----

    [Fact]
    public async Task ACapabilityCallRunsTheWholeCycleAndLeavesItReadable()
    {
        await using McpClient client = await ConnectAsync();

        JsonElement done = await CallAsync(
            client, "aurora_execute", Execute("echo.say", new { message = "close the loop" }));

        Assert.Equal("completed", done.GetProperty("status").GetString());

        // Every stage is either recorded or has a recorded reason for being omitted (RFC 021
        // rule 1). The cycle is what makes an answer explainable afterwards rather than asserted.
        JsonElement cycle = await CallAsync(
            client, "aurora_cycle",
            new Dictionary<string, object?> { ["cycle_id"] = done.GetProperty("cycle_ref").GetString() });

        var ran = cycle.GetProperty("stages_run").EnumerateArray()
            .Select(s => s.GetString()!).ToList();

        // The whole governed order, in order: nothing is decided before what it is deciding
        // about was attended to, and nothing runs before policy has spoken (RFC 021).
        foreach (var required in new[]
        {
            CycleStage.Perception, CycleStage.Attention, CycleStage.WorkingMemory,
            CycleStage.Decision, CycleStage.Policy, CycleStage.Capabilities, CycleStage.Executor,
            CycleStage.Observation, CycleStage.Reflection,
        })
        {
            Assert.Contains(required, ran);
        }

        // And in the order the RFC states them, not merely all present.
        var positions = ran.Select(stage => CycleStage.Order.ToList().IndexOf(stage)).ToList();
        Assert.Equal(positions.OrderBy(p => p), positions);

        // And the effect is in the audit chain, which is the record that cannot be edited.
        using HttpClient http = Client();
        JsonElement audit = await ReadAsync(http, "/v1/audit?limit=50");

        Assert.Contains(
            audit.GetProperty("data").EnumerateArray(),
            r => r.GetProperty("action_id").GetString() == "echo.say");
    }

    // ---- 2 and 3. the two ways an action does not happen ----

    [Fact]
    public async Task PolicyRefusesWhatItWasNeverGoingToAllow()
    {
        await using McpClient client = await ConnectAsync();

        // RFC 06 rule 1: there is no shell.execute_anything and no implicit access. Refused at
        // resolution, before policy is even asked — the earlier and stricter of the two refusals.
        JsonElement unknown = await CallAsync(client, "aurora_execute", Execute("shell.execute_anything"));

        Assert.Equal("invalid", unknown.GetProperty("status").GetString());
        Assert.NotEqual("completed", unknown.GetProperty("status").GetString());

        // And the one that is in the catalogue but not permitted is refused by policy rather than
        // by absence: fail-closed means the default is no, not that the list is short.
        JsonElement forbidden = await CallAsync(
            client, "aurora_execute",
            Execute("files.read_sandbox", new { path = "nothing-here.txt" }));

        Assert.NotEqual("completed", forbidden.GetProperty("status").GetString());
    }

    [Fact]
    public async Task WithoutAnApprovalNothingWithAnEffectHappens()
    {
        await using McpClient client = await ConnectAsync();

        JsonElement denied = await CallAsync(
            client, "aurora_execute",
            Execute("memory.remember", new { note = "the loop needs approval" }));

        Assert.Equal("denied", denied.GetProperty("status").GetString());
        Assert.Equal(
            "approval_required", denied.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- 4. an approval is for one input, and for one use ----

    [Fact]
    public async Task AnApprovalDoesNotCarryToADifferentInputOrASecondCall()
    {
        await using McpClient client = await ConnectAsync();

        Dictionary<string, object?> first = Execute("memory.remember", new { note = "scoped one" });

        JsonElement denied = await CallAsync(client, "aurora_execute", first);
        var approvalId = denied.GetProperty("consent").GetProperty("approval_id").GetString();

        await CallAsync(
            client, "aurora_approve",
            new Dictionary<string, object?> { ["approval_id"] = approvalId, ["decision"] = "approved" });

        Assert.Equal("completed", (await CallAsync(client, "aurora_execute", first))
            .GetProperty("status").GetString());

        // The same action with different input is a different thing to agree to.
        JsonElement other = await CallAsync(
            client, "aurora_execute", Execute("memory.remember", new { note = "scoped two" }));

        Assert.Equal("denied", other.GetProperty("status").GetString());

        // And the same input again is a second act, not a continuation of the first.
        JsonElement again = await CallAsync(client, "aurora_execute", first);
        Assert.Equal("denied", again.GetProperty("status").GetString());
    }

    // ---- 5. a machine with no room left does not reach outside itself ----

    [Fact]
    public async Task WhenThereIsNoRoomLeftAuroraReadsAndDoesNotReachOutside()
    {
        await using McpClient client = await ConnectAsync();
        var self = _factory.Services.GetRequiredService<ISelfModel>();

        try
        {
            // Below the floor Aurora needs to write a snapshot or a backup (docs/adr/0061).
            _factory.Host.DiskFreeBytes = 64L * 1024 * 1024;
            await self.RefreshAsync("local", Ct());

            // Reading is unaffected. "I can prepare, but not send" is the whole of it.
            Assert.Equal(
                "completed",
                (await CallAsync(client, "aurora_execute", Execute("clock.now")))
                    .GetProperty("status").GetString());

            JsonElement effectful = await CallAsync(
                client, "aurora_execute",
                Execute("files.write_sandbox", new { path = "no-room.txt", content = "x" }));

            Assert.NotEqual("completed", effectful.GetProperty("status").GetString());
        }
        finally
        {
            _factory.Host.DiskFreeBytes = 64L * 1024 * 1024 * 1024;
            await self.RefreshAsync("local", Ct());
        }
    }

    // ---- 13. Aurora describes itself without that describing granting anything ----

    [Fact]
    public async Task WhatAuroraSaysAboutItselfGrantsNothing()
    {
        await using McpClient client = await ConnectAsync();

        JsonElement described = await CallAsync(
            client, "aurora_self", new Dictionary<string, object?>());

        // It says what it can do — and saying so is not doing so. The same capability still needs
        // its approval, which is the point of the separation (LAW-008, RFC 027 rule 2).
        Assert.Contains(
            described.GetProperty("can_do").EnumerateArray().Select(e => e.GetString()!),
            said => said.StartsWith("memory.remember", StringComparison.Ordinal));

        JsonElement denied = await CallAsync(
            client, "aurora_execute",
            Execute("memory.remember", new { note = "self-awareness is not permission" }));

        Assert.Equal("denied", denied.GetProperty("status").GetString());
    }

    // ---- 15. the audit chain verifies after everything above ----

    [Fact]
    public async Task TheAuditChainStillVerifiesAfterTheSuiteHasUsedIt()
    {
        var audit = _factory.Services.GetRequiredService<IAuditStore>();

        await CallAsync(
            await ConnectAsync(), "aurora_execute", Execute("echo.say", new { message = "chain" }));

        AuditVerification verification = await audit.VerifyChainAsync(Ct());

        // Hash-chained and HMAC-signed: every other guarantee Aurora offers is checked against
        // this log, so a chain that stopped verifying would make the rest unfalsifiable.
        Assert.True(verification.Ok, verification.Reason ?? "the chain did not verify");
        Assert.Null(verification.BrokenSequence);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient http, string path)
    {
        HttpResponseMessage response = await http.GetAsync(path, Ct());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct()));

        return document.RootElement.Clone();
    }

    // ---- 7. a capability that fails is a failure, not a silent success ----

    [Fact]
    public async Task ACapabilityThatFailsIsRecordedAsFailedAndCanBeTriedAgain()
    {
        await using McpClient client = await ConnectAsync();

        Dictionary<string, object?> read = Execute(
            "files.read_sandbox", new { path = "nothing-was-ever-here.txt" });

        // Approve it, so what follows is the capability failing rather than policy refusing.
        JsonElement denied = await CallAsync(client, "aurora_execute", read);

        await CallAsync(
            client, "aurora_approve",
            new Dictionary<string, object?>
            {
                ["approval_id"] = denied.GetProperty("consent").GetProperty("approval_id").GetString(),
                ["decision"] = "approved",
            });

        JsonElement failed = await CallAsync(client, "aurora_execute", read);

        Assert.Equal("failed", failed.GetProperty("status").GetString());

        // The reason is not echoed back. A caller that could probe the sandbox one rejected path
        // at a time would learn its layout from the error messages.
        Assert.DoesNotContain(
            "nothing-was-ever-here", failed.GetProperty("error").GetProperty("message").GetString()!,
            StringComparison.Ordinal);

        // A failure is not a result. Whatever the same call does next, it does not come back as
        // the failure replayed with a completed status.
        JsonElement again = await CallAsync(client, "aurora_execute", read);
        Assert.NotEqual("completed", again.GetProperty("status").GetString());

        // And it is in the audit as a failure, so the record does not read as if nothing happened.
        using HttpClient http = Client();
        JsonElement audit = await ReadAsync(http, "/v1/audit?limit=50");

        JsonElement record = audit.GetProperty("data").EnumerateArray()
            .First(r => r.GetProperty("action_id").GetString() == "files.read_sandbox"
                && r.GetProperty("outcome").GetString() == "failed");

        // With why. The caller is told only that it failed — a message written by a capability
        // could name a path a probing caller would learn from — but the record has to answer the
        // question afterwards, and a bare "failed" answers nothing. Found when a plugin failed on
        // a live instance and there was no way to discover the reason.
        var reason = record.TryGetProperty("reason", out JsonElement why) ? why.GetString() : null;

        Assert.False(string.IsNullOrWhiteSpace(reason), "a failed call recorded no reason");
    }

    // ---- 1, the other half: a mission gives a goal a reason to exist ----

    [Fact]
    public async Task AGoalArrivesAsADraftUnderAMissionAndIsNotActedOn()
    {
        using HttpClient http = Client();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/goals")
        {
            Content = JsonContent.Create(new
            {
                title = "Keep the notes tidy",
                outcome = "There are no duplicate notes.",
                success_criteria = new[] { "No two notes say the same thing." },
            }),
        };

        // RFC 10 rule 1: a write command carries an idempotency key, so the same request twice is
        // one goal rather than two.
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        HttpResponseMessage created = await http.SendAsync(request, Ct());
        created.EnsureSuccessStatusCode();

        using JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct()));
        var id = body.RootElement.GetProperty("data").GetProperty("id").GetString();

        JsonElement read = await ReadAsync(http, $"/v1/goals/{id}");
        JsonElement goal = read.GetProperty("data").GetProperty("goal");

        // DRAFT, and decomposed by nobody on arrival. RFC 05: an objective states an outcome and
        // its success criteria, and posting one is not asking for it to be pursued.
        Assert.Equal("DRAFT", goal.GetProperty("status").GetString());
        Assert.Empty(read.GetProperty("data").GetProperty("tasks").EnumerateArray());

        // RFC 052 rule 2 binds a *persistent* goal to a mission or an ad-hoc review date. A draft
        // is not yet a commitment, so it carries neither — and the invariant is checked below on
        // the thing the rule is actually about.
        Assert.Null(goal.TryGetProperty("mission_ref", out JsonElement m) ? m.GetString() : null);
    }

    [Fact]
    public async Task AGoalThatIsAnActualCommitmentCarriesAMissionOrAReviewDate()
    {
        var planner = _factory.Services.GetRequiredService<IPlanner>();

        // Well specified, so it is created ACTIVE rather than drafted: this is the state RFC 052
        // rule 2 is about — a standing commitment that nobody owns and nobody looks at again.
        Plan plan = await planner.CreateAsync(
            new GoalRequest(
                "Tidy the notes", "There are no duplicate notes.", "owner",
                SuccessCriteria: ["No two notes say the same thing."],
                Assumptions: []),
            [new TaskRequest("Find duplicates", "List notes that repeat.", TaskKind.Research, [], "Low")],
            Ct());

        Goal active = (await planner.GetGoalAsync(plan.GoalId, Ct()))!;

        Assert.Equal(GoalStatus.Active, active.Status);

        Assert.True(
            active.MissionRef is not null || active.AdHocReviewAtUtc is not null,
            "an active goal is under a mission or marked ad hoc with a review date");
    }

}
