using System.Net;
using System.Text;
using System.Text.Json;
using Aurora.Adapters.Consent;
using Aurora.Adapters.Time;
using Aurora.Tests.Support;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Aurora.Tests.Integration;

/// <summary>
/// End-to-end tests over the real MCP HTTP transport and security middleware, driven by the
/// official MCP client against an in-memory host.
/// </summary>
public sealed class McpServerTests : IClassFixture<AuroraAppFactory>
{
    private readonly AuroraAppFactory _factory;

    public McpServerTests(AuroraAppFactory factory) => _factory = factory;

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private async Task<McpClient> ConnectAsync(string? bearer = null)
    {
        var http = _factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {bearer ?? _factory.BearerToken}",
                },
            },
            http);

        return await McpClient.CreateAsync(transport, cancellationToken: Timeout());
    }

    /// <summary>
    /// Clears any consent session before a session test. The factory is a class fixture, so a
    /// session opened by a sibling test would otherwise already cover the first read here.
    /// </summary>
    private async Task ResetSessionsAsync()
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_factory.BearerToken}");
        (await http.PostAsync("/sessions/revoke", content: null, Timeout())).EnsureSuccessStatusCode();
    }

    private static string ToJson(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
        {
            return structured.GetRawText();
        }

        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        return text?.Text ?? throw new InvalidOperationException("Tool result carried no JSON payload.");
    }

    [Fact]
    public async Task ListTools_ExposesTheFixedTools()
    {
        await using var client = await ConnectAsync();
        var names = (await client.ListToolsAsync(cancellationToken: Timeout())).Select(t => t.Name).ToList();

        Assert.Contains("aurora_catalog", names);
        Assert.Contains("aurora_execute", names);
        Assert.Contains("aurora_approve", names);
        Assert.Contains("aurora_converse", names);
        Assert.Contains("aurora_cycle", names);
        Assert.Contains("aurora_review", names);
        Assert.Contains("aurora_self", names);
    }

    [Fact]
    public async Task Self_DescribesAuroraFromItsOwnPersistedModel()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "aurora_self", new Dictionary<string, object?>(), cancellationToken: Timeout());

        using var doc = JsonDocument.Parse(ToJson(result));
        JsonElement description = doc.RootElement;

        // RFC 027 rule 3: what Aurora says about itself is a secure view. The type has nowhere to
        // put a secret, and the wire shape is asserted so a later field cannot quietly add one.
        Assert.Equal(
            ["operational_state", "can_do", "cannot_do", "health_summary", "health_observed_at_utc",
             "active_cycles", "observed_at_utc"],
            description.EnumerateObject().Select(p => p.Name));

        Assert.False(string.IsNullOrWhiteSpace(description.GetProperty("operational_state").GetString()));

        // It knows what it can do because the capability registry said so, not because it assumed.
        Assert.Contains(
            description.GetProperty("can_do").EnumerateArray().Select(e => e.GetString()!),
            said => said!.StartsWith("clock.now", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Review_ReportsWhatHappenedFromAuroraSOwnRecords()
    {
        await using var client = await ConnectAsync();

        // Something for it to find.
        await client.CallToolAsync(
            "aurora_execute",
            new Dictionary<string, object?>
            {
                ["action_id"] = "echo.say",
                ["input"] = new Dictionary<string, object?> { ["message"] = "something to review" },
            },
            cancellationToken: Timeout());

        var result = await client.CallToolAsync(
            "aurora_review",
            new Dictionary<string, object?> { ["timezone"] = "Europe/Lisbon" },
            cancellationToken: Timeout());

        using var doc = JsonDocument.Parse(ToJson(result));

        // Reads Aurora's own records, touches nothing outside, and still goes through the cycle:
        // a briefing is a claim about what happened, not a query result.
        Assert.True(
            doc.RootElement.GetProperty("findings").GetProperty("audit_entries").GetInt32() >= 1);
        Assert.Contains(
            "POLICY",
            doc.RootElement.GetProperty("stages_run").EnumerateArray().Select(s => s.GetString()));
        Assert.Contains(
            "MEMORY",
            doc.RootElement.GetProperty("stages_omitted").EnumerateArray().Select(s => s.GetString()));
    }

    // ---- step 10b: MCP runs through the cognitive cycle, not beside it ----

    [Fact]
    public async Task Execute_ReturnsTheCycleItWasReasonedThrough()
    {
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync(
            "aurora_execute",
            new Dictionary<string, object?>
            {
                ["action_id"] = "echo.say",
                ["input"] = new Dictionary<string, object?> { ["message"] = "through the cycle" },
            },
            cancellationToken: Timeout());

        using var doc = JsonDocument.Parse(ToJson(result));
        Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString());

        var cycleRef = doc.RootElement.GetProperty("cycle_ref").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cycleRef));

        // And the cycle is readable back through MCP, so a client is never asked to take the
        // outcome on trust.
        var recalled = await client.CallToolAsync(
            "aurora_cycle",
            new Dictionary<string, object?> { ["cycle_id"] = cycleRef },
            cancellationToken: Timeout());

        using var cycle = JsonDocument.Parse(ToJson(recalled));
        var run = cycle.RootElement.GetProperty("stages_run").EnumerateArray()
            .Select(s => s.GetString()).ToList();

        Assert.Contains("DECISION", run);
        Assert.Contains("POLICY", run);
        Assert.Contains("EXECUTOR", run);
        Assert.Contains("OBSERVATION", run);
        Assert.Contains("REFLECTION", run);
    }

    [Fact]
    public async Task Converse_AnswersWithWhatWasDecidedRatherThanWithAWrittenReply()
    {
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync(
            "aurora_converse",
            new Dictionary<string, object?>
            {
                ["conversation_ref"] = $"c-{Guid.NewGuid():N}",
                ["utterance"] = "what do I usually drink",
            },
            cancellationToken: Timeout());

        using var doc = JsonDocument.Parse(ToJson(result));

        // Aurora returns references, not prose: RFC 021 leaves the wording to the client and keeps
        // authority over what happened.
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("cycle_id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("decision_id").GetString()));
        Assert.NotEmpty(doc.RootElement.GetProperty("audit_refs").EnumerateArray());
        Assert.Contains(
            "PLANNER",
            doc.RootElement.GetProperty("stages_omitted").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task Catalog_ListsClockEchoAndMemory()
    {
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync(
            "aurora_catalog", new Dictionary<string, object?>(), cancellationToken: Timeout());

        using var doc = JsonDocument.Parse(ToJson(result));
        var ids = doc.RootElement.GetProperty("actions").EnumerateArray()
            .Select(a => a.GetProperty("action_id").GetString())
            .ToList();

        Assert.Contains("clock.now", ids);
        Assert.Contains("echo.say", ids);
        Assert.Contains("memory.remember", ids);
        Assert.Contains("memory.recall", ids);
    }

    [Fact]
    public async Task Execute_Echo_Completes_WithAuditRef()
    {
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync(
            "aurora_execute",
            new Dictionary<string, object?>
            {
                ["action_id"] = "echo.say",
                ["input"] = new Dictionary<string, object?> { ["message"] = "hi there" },
            },
            cancellationToken: Timeout());

        using var doc = JsonDocument.Parse(ToJson(result));
        Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("hi there", doc.RootElement.GetProperty("result").GetProperty("said").GetString());
        Assert.NotEmpty(doc.RootElement.GetProperty("audit_ref").EnumerateArray());
    }

    [Fact]
    public async Task Execute_ClockNow_NoInput_Completes()
    {
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync(
            "aurora_execute",
            new Dictionary<string, object?> { ["action_id"] = "clock.now" },
            cancellationToken: Timeout());

        using var doc = JsonDocument.Parse(ToJson(result));
        Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("result").TryGetProperty("utc", out _));
    }

    [Fact]
    public async Task Execute_UnknownAction_IsInvalid()
    {
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync(
            "aurora_execute",
            new Dictionary<string, object?> { ["action_id"] = "nope.nope" },
            cancellationToken: Timeout());

        using var doc = JsonDocument.Parse(ToJson(result));
        Assert.Equal("invalid", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Execute_Idempotent_ReplayReturnsSameResult()
    {
        await using var client = await ConnectAsync();
        var args = new Dictionary<string, object?>
        {
            ["action_id"] = "echo.say",
            ["input"] = new Dictionary<string, object?> { ["message"] = "once" },
            ["idempotency_key"] = "integration-key-1",
        };

        var first = ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout()));
        var second = ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout()));

        using var d1 = JsonDocument.Parse(first);
        using var d2 = JsonDocument.Parse(second);
        Assert.Equal("completed", d1.RootElement.GetProperty("status").GetString());
        Assert.Equal("completed", d2.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            d1.RootElement.GetProperty("result").GetProperty("said").GetString(),
            d2.RootElement.GetProperty("result").GetProperty("said").GetString());
    }

    [Fact]
    public async Task WrongBearer_IsRejected()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(bearer: "definitely-wrong-token"));
    }

    [Fact]
    public async Task RawRequest_WrongHost_Returns421()
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Host = "evil.example.com";

        var response = await http.PostAsync(
            "/mcp", new StringContent("{}", Encoding.UTF8, "application/json"), Timeout());

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
    }

    [Fact]
    public async Task RawRequest_NoBearer_Returns401_WithJsonOAuthError()
    {
        var http = _factory.CreateClient();

        var response = await http.PostAsync(
            "/mcp", new StringContent("{}", Encoding.UTF8, "application/json"), Timeout());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_token", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("unauthorized", doc.RootElement.GetProperty("error_description").GetString());
    }

    // --- It.2: persistent approval end to end (docs/adr/0002) ---

    [Fact]
    public async Task Remember_RequiresApproval_ThenApprove_ThenExecuteCompletes_ThenRecall()
    {
        await using var client = await ConnectAsync();
        var args = new Dictionary<string, object?>
        {
            ["action_id"] = "memory.remember",
            ["input"] = new Dictionary<string, object?> { ["note"] = "buy milk" },
        };

        var denied = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
        Assert.Equal("denied", denied.RootElement.GetProperty("status").GetString());
        Assert.Equal("approval_required", denied.RootElement.GetProperty("error").GetProperty("code").GetString());
        var approvalId = denied.RootElement.GetProperty("consent").GetProperty("approval_id").GetString();
        Assert.False(string.IsNullOrEmpty(approvalId));

        var approve = JsonDocument.Parse(ToJson(await client.CallToolAsync(
            "aurora_approve",
            new Dictionary<string, object?> { ["approval_id"] = approvalId, ["decision"] = "approved" },
            cancellationToken: Timeout())));
        Assert.Equal("decided", approve.RootElement.GetProperty("status").GetString());
        Assert.Equal("APPROVED", approve.RootElement.GetProperty("approval_state").GetString());

        var completed = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
        Assert.Equal("completed", completed.RootElement.GetProperty("status").GetString());
        Assert.Equal("buy milk", completed.RootElement.GetProperty("result").GetProperty("note").GetString());

        var recalled = JsonDocument.Parse(ToJson(await client.CallToolAsync(
            "aurora_execute",
            new Dictionary<string, object?> { ["action_id"] = "memory.recall" },
            cancellationToken: Timeout())));
        var notes = recalled.RootElement.GetProperty("result").GetProperty("notes").EnumerateArray()
            .Select(n => n.GetProperty("note").GetString())
            .ToList();
        Assert.Contains("buy milk", notes);
    }

    [Fact]
    public async Task Remember_Rejected_StaysDenied_ForTheSameInput()
    {
        await using var client = await ConnectAsync();
        var args = new Dictionary<string, object?>
        {
            ["action_id"] = "memory.remember",
            ["input"] = new Dictionary<string, object?> { ["note"] = "do not remember this" },
        };

        var denied = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
        var approvalId = denied.RootElement.GetProperty("consent").GetProperty("approval_id").GetString();

        await client.CallToolAsync(
            "aurora_approve",
            new Dictionary<string, object?> { ["approval_id"] = approvalId, ["decision"] = "rejected" },
            cancellationToken: Timeout());

        var retried = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
        Assert.Equal("denied", retried.RootElement.GetProperty("status").GetString());
        Assert.Equal("consent_required", retried.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Approve_UnknownApprovalId_ReturnsNotFound()
    {
        await using var client = await ConnectAsync();
        var result = JsonDocument.Parse(ToJson(await client.CallToolAsync(
            "aurora_approve",
            new Dictionary<string, object?> { ["approval_id"] = "does-not-exist", ["decision"] = "approved" },
            cancellationToken: Timeout())));

        Assert.Equal("not_found", result.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Execute_WriteSandbox_RequiresApproval_ThenWritesTheFile()
    {
        await using var client = await ConnectAsync();
        var relative = $"it2/{Guid.NewGuid():N}.txt";
        var args = new Dictionary<string, object?>
        {
            ["action_id"] = "files.write_sandbox",
            ["input"] = new Dictionary<string, object?> { ["path"] = relative, ["content"] = "written by aurora" },
        };

        var denied = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
        Assert.Equal("denied", denied.RootElement.GetProperty("status").GetString());
        Assert.Equal("approval_required", denied.RootElement.GetProperty("error").GetProperty("code").GetString());
        var approvalId = denied.RootElement.GetProperty("consent").GetProperty("approval_id").GetString();

        // Nothing may exist on disk until the approval is actually decided.
        Assert.False(File.Exists(Path.Combine(_factory.SandboxRoot, "it2", Path.GetFileName(relative))));

        var approve = JsonDocument.Parse(ToJson(await client.CallToolAsync(
            "aurora_approve",
            new Dictionary<string, object?> { ["approval_id"] = approvalId, ["decision"] = "approved" },
            cancellationToken: Timeout())));
        Assert.Equal("decided", approve.RootElement.GetProperty("status").GetString());

        var completed = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
        Assert.Equal("completed", completed.RootElement.GetProperty("status").GetString());
        Assert.False(completed.RootElement.GetProperty("result").GetProperty("overwritten").GetBoolean());

        var written = Path.Combine(_factory.SandboxRoot, "it2", Path.GetFileName(relative));
        Assert.True(File.Exists(written));
        Assert.Equal("written by aurora", await File.ReadAllTextAsync(written));
    }

    [Fact]
    public async Task Execute_WriteSandbox_TraversalFails_EvenAfterApproval()
    {
        await using var client = await ConnectAsync();
        var escapeName = $"aurora-escaped-{Guid.NewGuid():N}.txt";
        var args = new Dictionary<string, object?>
        {
            ["action_id"] = "files.write_sandbox",
            ["input"] = new Dictionary<string, object?>
            {
                ["path"] = $"../{escapeName}",
                ["content"] = "should never land",
            },
        };

        var denied = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
        var approvalId = denied.RootElement.GetProperty("consent").GetProperty("approval_id").GetString();

        await client.CallToolAsync(
            "aurora_approve",
            new Dictionary<string, object?> { ["approval_id"] = approvalId, ["decision"] = "approved" },
            cancellationToken: Timeout());

        // Approval authorises the action, never the escape: the sandbox is a separate boundary.
        var failed = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
        Assert.Equal("failed", failed.RootElement.GetProperty("status").GetString());
        Assert.Equal("execution_failed", failed.RootElement.GetProperty("error").GetProperty("code").GetString());

        var parent = Directory.GetParent(_factory.SandboxRoot)!.FullName;
        Assert.False(File.Exists(Path.Combine(parent, escapeName)));
    }

    [Fact]
    public async Task Execute_Objective_ResolvesViaTheKeywordProposer()
    {
        // The keyword proposer is the only one Aurora has (docs/adr/0051), so objective mode
        // resolves against the catalogue and never leaves the machine.
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync(
            "aurora_execute",
            new Dictionary<string, object?> { ["objective"] = "say hello from aurora" },
            cancellationToken: Timeout());

        using var doc = JsonDocument.Parse(ToJson(result));
        Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("echo.say", doc.RootElement.GetProperty("resolved").GetProperty("action_id").GetString());
        Assert.Equal("keyword", doc.RootElement.GetProperty("resolved").GetProperty("via").GetString());
        Assert.Equal("hello from aurora", doc.RootElement.GetProperty("result").GetProperty("said").GetString());
    }

    [Fact]
    public async Task Execute_Objective_TargetingAWriteAction_IsRefused()
    {
        // "remember ..." names a MEDIUM capability; the keyword fallback must not reach it, and
        // no approval prompt should be created either.
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync(
            "aurora_execute",
            new Dictionary<string, object?> { ["objective"] = "remember that milk is expensive" },
            cancellationToken: Timeout());

        using var doc = JsonDocument.Parse(ToJson(result));
        Assert.Equal("invalid", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "objective_mode_unavailable",
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- It.3 metrics surface (docs/adr/0008) ----

    [Fact]
    public async Task Metrics_RequireTheBearerToken()
    {
        var http = _factory.CreateClient();

        var response = await http.GetAsync("/metrics", Timeout());

        // The operational surface is behind the same guard as the MCP one; an unauthenticated
        // reader could otherwise learn how often requests are being refused.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_ReportExecutionsAndPendingApprovals()
    {
        await using var client = await ConnectAsync();

        await client.CallToolAsync(
            "aurora_execute",
            new Dictionary<string, object?>
            {
                ["action_id"] = "echo.say",
                ["input"] = new Dictionary<string, object?> { ["message"] = "for metrics" },
            },
            cancellationToken: Timeout());

        // Leave one approval outstanding so the gauge has something to report.
        await client.CallToolAsync(
            "aurora_execute",
            new Dictionary<string, object?>
            {
                ["action_id"] = "memory.remember",
                ["input"] = new Dictionary<string, object?> { ["note"] = $"pending {Guid.NewGuid():N}" },
            },
            cancellationToken: Timeout());

        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_factory.BearerToken}");

        var response = await http.GetAsync("/metrics", Timeout());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Timeout()));
        // snake_case, like every other Aurora wire contract: /metrics used to answer in camelCase
        // because it fell through to the host default rather than because anything chose that.
        Assert.True(doc.RootElement.GetProperty("executions_by_outcome").GetProperty("completed").GetInt64() >= 1);
        Assert.True(doc.RootElement.GetProperty("pending_approvals").GetInt32() >= 1);
    }

    // ---- It.2 consent sessions, read-only reuse (docs/adr/0010) ----

    [Fact]
    public async Task Session_OpenedByApprovingARead_CoversFurtherReads()
    {
        await ResetSessionsAsync();
        await using var client = await ConnectAsync();

        Directory.CreateDirectory(_factory.SandboxRoot);
        var first = $"s-{Guid.NewGuid():N}.txt";
        var second = $"s-{Guid.NewGuid():N}.txt";
        await File.WriteAllTextAsync(Path.Combine(_factory.SandboxRoot, first), "one");
        await File.WriteAllTextAsync(Path.Combine(_factory.SandboxRoot, second), "two");

        static Dictionary<string, object?> Read(string path) => new()
        {
            ["action_id"] = "files.read_sandbox",
            ["input"] = new Dictionary<string, object?> { ["path"] = path },
        };

        var denied = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", Read(first), cancellationToken: Timeout())));
        Assert.Equal("approval_required", denied.RootElement.GetProperty("error").GetProperty("code").GetString());
        var approvalId = denied.RootElement.GetProperty("consent").GetProperty("approval_id").GetString();

        await client.CallToolAsync(
            "aurora_approve",
            new Dictionary<string, object?> { ["approval_id"] = approvalId, ["decision"] = "approved" },
            cancellationToken: Timeout());

        var granted = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", Read(first), cancellationToken: Timeout())));
        Assert.Equal("completed", granted.RootElement.GetProperty("status").GetString());
        Assert.Equal("one", granted.RootElement.GetProperty("result").GetProperty("content").GetString());

        // A different file, never approved: the session covers it because reading changes nothing.
        var reused = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", Read(second), cancellationToken: Timeout())));
        Assert.Equal("completed", reused.RootElement.GetProperty("status").GetString());
        Assert.Equal("session", reused.RootElement.GetProperty("consent").GetProperty("via").GetString());
        Assert.Equal("two", reused.RootElement.GetProperty("result").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Session_DoesNotCoverAWrite()
    {
        await ResetSessionsAsync();
        await using var client = await ConnectAsync();

        Directory.CreateDirectory(_factory.SandboxRoot);
        var readable = $"s-{Guid.NewGuid():N}.txt";
        await File.WriteAllTextAsync(Path.Combine(_factory.SandboxRoot, readable), "content");

        var readArgs = new Dictionary<string, object?>
        {
            ["action_id"] = "files.read_sandbox",
            ["input"] = new Dictionary<string, object?> { ["path"] = readable },
        };

        var denied = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", readArgs, cancellationToken: Timeout())));
        await client.CallToolAsync(
            "aurora_approve",
            new Dictionary<string, object?>
            {
                ["approval_id"] = denied.RootElement.GetProperty("consent").GetProperty("approval_id").GetString(),
                ["decision"] = "approved",
            },
            cancellationToken: Timeout());
        await client.CallToolAsync("aurora_execute", readArgs, cancellationToken: Timeout());

        // A session is live. A write must still be decided on its own.
        var write = JsonDocument.Parse(ToJson(await client.CallToolAsync(
            "aurora_execute",
            new Dictionary<string, object?>
            {
                ["action_id"] = "files.write_sandbox",
                ["input"] = new Dictionary<string, object?>
                {
                    ["path"] = $"s-{Guid.NewGuid():N}.txt",
                    ["content"] = "should need its own approval",
                },
            },
            cancellationToken: Timeout())));

        Assert.Equal("denied", write.RootElement.GetProperty("status").GetString());
        Assert.Equal("approval_required", write.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task KillSwitch_RevokesSessionsAndRequiresTheBearerToken()
    {
        var unauthenticated = _factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await unauthenticated.PostAsync("/sessions/revoke", content: null, Timeout())).StatusCode);

        await ResetSessionsAsync();
        await using var client = await ConnectAsync();
        Directory.CreateDirectory(_factory.SandboxRoot);
        var file = $"s-{Guid.NewGuid():N}.txt";
        await File.WriteAllTextAsync(Path.Combine(_factory.SandboxRoot, file), "content");

        var args = new Dictionary<string, object?>
        {
            ["action_id"] = "files.read_sandbox",
            ["input"] = new Dictionary<string, object?> { ["path"] = file },
        };

        var denied = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
        await client.CallToolAsync(
            "aurora_approve",
            new Dictionary<string, object?>
            {
                ["approval_id"] = denied.RootElement.GetProperty("consent").GetProperty("approval_id").GetString(),
                ["decision"] = "approved",
            },
            cancellationToken: Timeout());
        await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout());

        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_factory.BearerToken}");
        var revoke = await http.PostAsync("/sessions/revoke", content: null, Timeout());
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // After the kill switch the same read needs a human again.
        var after = JsonDocument.Parse(
            ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
        Assert.Equal("denied", after.RootElement.GetProperty("status").GetString());
    }

    // ---- operator passphrase over real MCP (docs/adr/0011) ----

    [Fact]
    public async Task AnEnrolledPassphrase_StopsTheAgentApprovingItsOwnRequest()
    {
        // Enrol against the running server's own verifier file. The authenticator reads state from
        // disk on every call, so the guard engages without a restart.
        var authenticator = new Pbkdf2PassphraseAuthenticator(
            _factory.PassphrasePath, new SystemClock(), new PassphraseOptions(Iterations: 1_000));
        authenticator.Enroll("operator-only-secret");

        try
        {
            await using var client = await ConnectAsync();
            var args = new Dictionary<string, object?>
            {
                ["action_id"] = "memory.remember",
                ["input"] = new Dictionary<string, object?> { ["note"] = $"guarded {Guid.NewGuid():N}" },
            };

            var denied = JsonDocument.Parse(
                ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
            var approvalId = denied.RootElement.GetProperty("consent").GetProperty("approval_id").GetString();

            // What the agent can do on its own: call the tool. What it cannot do: supply the secret.
            var withoutSecret = JsonDocument.Parse(ToJson(await client.CallToolAsync(
                "aurora_approve",
                new Dictionary<string, object?> { ["approval_id"] = approvalId, ["decision"] = "approved" },
                cancellationToken: Timeout())));

            Assert.Equal("invalid", withoutSecret.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "passphrase_required",
                withoutSecret.RootElement.GetProperty("error").GetProperty("code").GetString());

            // And the request really did not go through.
            var stillDenied = JsonDocument.Parse(
                ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
            Assert.Equal("denied", stillDenied.RootElement.GetProperty("status").GetString());

            // With the operator's secret, the same call decides.
            var withSecret = JsonDocument.Parse(ToJson(await client.CallToolAsync(
                "aurora_approve",
                new Dictionary<string, object?>
                {
                    ["approval_id"] = approvalId,
                    ["decision"] = "approved",
                    ["passphrase"] = "operator-only-secret",
                },
                cancellationToken: Timeout())));

            Assert.Equal("decided", withSecret.RootElement.GetProperty("status").GetString());

            var completed = JsonDocument.Parse(
                ToJson(await client.CallToolAsync("aurora_execute", args, cancellationToken: Timeout())));
            Assert.Equal("completed", completed.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            // Shared class fixture: leave the guard off for the other tests.
            authenticator.Revoke();
        }
    }

    // --- the reference capability, all the way through the kernel (docs/adr/0060) ---

    [Fact]
    public async Task Organise_PlansWithoutApproval_ThenNeedsOneToMoveAnything()
    {
        await using var client = await ConnectAsync();

        await File.WriteAllTextAsync(Path.Combine(_factory.SandboxRoot, "notes.md"), "x", Timeout());
        await File.WriteAllTextAsync(Path.Combine(_factory.SandboxRoot, "photo.png"), "x", Timeout());

        Dictionary<string, object?> Args(bool dryRun) => new()
        {
            ["action_id"] = "files.organise_sandbox",
            ["input"] = new Dictionary<string, object?>
            {
                ["rules"] = new[]
                {
                    new Dictionary<string, object?> { ["match"] = "*.md", ["into"] = "documents" },
                },
                ["dry_run"] = dryRun,
            },
        };

        // HIGH is permitted by policy because it is approval-gated and declares itself reversible.
        // Approval is still a person saying yes, and a dry run is not exempt: the plan discloses
        // what the sandbox contains, which is the same reading the real run does.
        var denied = JsonDocument.Parse(ToJson(
            await client.CallToolAsync("aurora_execute", Args(dryRun: true), cancellationToken: Timeout())));

        Assert.Equal("denied", denied.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "approval_required",
            denied.RootElement.GetProperty("error").GetProperty("code").GetString());

        await client.CallToolAsync(
            "aurora_approve",
            new Dictionary<string, object?>
            {
                ["approval_id"] = denied.RootElement.GetProperty("consent")
                    .GetProperty("approval_id").GetString(),
                ["decision"] = "approved",
            },
            cancellationToken: Timeout());

        var planned = JsonDocument.Parse(ToJson(
            await client.CallToolAsync("aurora_execute", Args(dryRun: true), cancellationToken: Timeout())));

        Assert.Equal("completed", planned.RootElement.GetProperty("status").GetString());
        JsonElement result = planned.RootElement.GetProperty("result");
        Assert.Equal(1, result.GetProperty("planned").GetInt32());
        Assert.Equal(0, result.GetProperty("moved").GetInt32());

        // Nothing moved, which is the point of asking first.
        Assert.True(File.Exists(Path.Combine(_factory.SandboxRoot, "notes.md")));

        // The real run is a different input, so it is a different approval. Scoped to this exact
        // input is the whole reason a dry run is safe to approve.
        var second = JsonDocument.Parse(ToJson(
            await client.CallToolAsync("aurora_execute", Args(dryRun: false), cancellationToken: Timeout())));

        Assert.Equal("denied", second.RootElement.GetProperty("status").GetString());

        await client.CallToolAsync(
            "aurora_approve",
            new Dictionary<string, object?>
            {
                ["approval_id"] = second.RootElement.GetProperty("consent")
                    .GetProperty("approval_id").GetString(),
                ["decision"] = "approved",
            },
            cancellationToken: Timeout());

        var done = JsonDocument.Parse(ToJson(
            await client.CallToolAsync("aurora_execute", Args(dryRun: false), cancellationToken: Timeout())));

        Assert.Equal("completed", done.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, done.RootElement.GetProperty("result").GetProperty("moved").GetInt32());

        Assert.True(File.Exists(Path.Combine(_factory.SandboxRoot, "documents", "notes.md")));
        Assert.True(File.Exists(Path.Combine(_factory.SandboxRoot, "photo.png")));

        // And it left the caller the means to put it back.
        JsonElement undo = done.RootElement.GetProperty("result").GetProperty("undo");
        Assert.Equal("documents/notes.md", undo[0].GetProperty("from").GetString());
        Assert.Equal("notes.md", undo[0].GetProperty("to").GetString());
    }
}
