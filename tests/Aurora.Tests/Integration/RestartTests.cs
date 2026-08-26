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
/// Aurora comes back as itself.
/// </summary>
/// <remarks>
/// Two instances over the same database file, with nothing shared in the process between them —
/// the second is built after the first has been disposed. That is the only honest way to test a
/// restart: anything held in memory by the first would otherwise answer for the second.
/// <para>
/// What must survive is not "the data": it is the specific things that would let a restart become
/// a bypass. A consumed approval that came back would be an approval used twice. A consent session
/// that survived would be a standing permission outliving the process it was granted to.
/// </para>
/// </remarks>
public sealed class RestartTests
{
    private static CancellationToken Ct() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static async Task<McpClient> ConnectAsync(AuroraAppFactory factory) =>
        await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri("http://localhost/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {factory.BearerToken}",
                    },
                },
                factory.CreateClient()),
            cancellationToken: Ct());

    private static async Task<JsonElement> CallAsync(
        McpClient client, string tool, Dictionary<string, object?> args)
    {
        CallToolResult result = await client.CallToolAsync(tool, args, cancellationToken: Ct());
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}";

        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task WhatMustSurviveARestartDoesAndWhatMustNotDoesNot()
    {
        var dbPath = TestTemp.Path("restart") + ".db";
        string noteId;
        string headHash;

        await using (var first = new AuroraAppFactory { DbPath = dbPath })
        {
            await using McpClient client = await ConnectAsync(first);

            var remember = new Dictionary<string, object?>
            {
                ["action_id"] = "memory.remember",
                ["input"] = new Dictionary<string, object?> { ["note"] = "survive the restart" },
            };

            JsonElement denied = await CallAsync(client, "aurora_execute", remember);

            await CallAsync(
                client, "aurora_approve",
                new Dictionary<string, object?>
                {
                    ["approval_id"] = denied.GetProperty("consent").GetProperty("approval_id").GetString(),
                    ["decision"] = "approved",
                });

            JsonElement done = await CallAsync(client, "aurora_execute", remember);
            Assert.Equal("completed", done.GetProperty("status").GetString());
            noteId = done.GetProperty("result").GetProperty("note_id").GetString()!;

            // A consent session, which is a standing permission granted to this process.
            await first.Services.GetRequiredService<IConsentSessionStore>()
                .OpenAsync(new Principal("c1", "u1"), Ct());

            headHash = (await first.Services.GetRequiredService<IAuditStore>().HeadHashAsync(Ct()))!;
        }

        await using var second = new AuroraAppFactory { DbPath = dbPath };
        await using McpClient reopened = await ConnectAsync(second);

        // 1. What Aurora was told is still there.
        JsonElement recalled = await CallAsync(
            reopened, "aurora_execute",
            new Dictionary<string, object?> { ["action_id"] = "memory.recall" });

        Assert.Contains(
            "survive the restart",
            recalled.GetProperty("result").GetRawText(),
            StringComparison.Ordinal);

        // 2. The audit chain continues from where it was, and still verifies. A restart that began
        //    a new chain would make everything before it unverifiable.
        var audit = second.Services.GetRequiredService<IAuditStore>();
        AuditVerification verified = await audit.VerifyChainAsync(Ct());

        Assert.True(verified.Ok, verified.Reason ?? "the chain did not verify after restart");

        // 3. The consumed approval did not come back. One that survived would be an approval used
        //    twice, which is the whole point of one-time use.
        JsonElement again = await CallAsync(
            reopened, "aurora_execute",
            new Dictionary<string, object?>
            {
                ["action_id"] = "memory.remember",
                ["input"] = new Dictionary<string, object?> { ["note"] = "survive the restart" },
            });

        Assert.Equal("denied", again.GetProperty("status").GetString());

        // 4. The consent session did not. It was granted to a process that no longer exists, and
        //    the boot id it was tied to is regenerated on every start (docs/adr/0010).
        Assert.Equal(
            0,
            await second.Services.GetRequiredService<IConsentSessionStore>().CountActiveAsync(Ct()));

        Assert.NotEmpty(noteId);
        Assert.NotEmpty(headHash);
    }

    [Fact]
    public async Task AnInstanceThatRestartsMidFlightReconcilesRatherThanAssuming()
    {
        var dbPath = TestTemp.Path("restart-work") + ".db";
        string workItemId;

        await using (var first = new AuroraAppFactory { DbPath = dbPath })
        {
            // A unit of work left in flight, which is what a crash actually looks like from the
            // outside: nobody wrote FAILED, the process simply stopped.
            WorkItem started = await first.Services.GetRequiredService<IWorkItemService>()
                .HandleAsync("corr-1", "restart-key", null, null, null, Ct());

            workItemId = started.Id;
        }

        await using var second = new AuroraAppFactory { DbPath = dbPath };
        var work = second.Services.GetRequiredService<IWorkItemService>();

        // Still active after the restart, and still findable. Something left in flight that
        // vanished would be work nobody could reconcile, because nobody would know it existed.
        WorkItem? recovered = await work.GetAsync(workItemId, Ct());

        Assert.NotNull(recovered);
        Assert.True(WorkItemStatus.IsActive(recovered!.Status));
        Assert.Contains(await work.ActiveAsync(Ct()), w => w.Id == workItemId);

        // And the same key still joins it rather than starting a second one beside it.
        WorkItem joined = await work.HandleAsync("corr-2", "restart-key", null, null, null, Ct());
        Assert.Equal(workItemId, joined.Id);
    }
}
