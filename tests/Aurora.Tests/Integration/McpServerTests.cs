using System.Net;
using System.Text;
using System.Text.Json;
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

    // --- It.2: persistent approval end to end (design/0002) ---

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
    public async Task Execute_Objective_ResolvesViaKeywordFallback_WhenNoModelIsConfigured()
    {
        // No Azure deployment is configured for tests, so objective mode degrades to the keyword
        // fallback rather than disappearing.
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
}
