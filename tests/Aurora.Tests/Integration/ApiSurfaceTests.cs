using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Server.Security;
using Aurora.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aurora.Tests.Integration;

/// <summary>
/// End-to-end tests of the RFC 10 operator surface, over the real security middleware.
/// </summary>
/// <remarks>
/// These carry the steps 10–12 gate: the person controls memory and approvals, and what Aurora did
/// is observable. Each test is named after the property it holds, not the endpoint it calls.
/// </remarks>
public sealed class ApiSurfaceTests : IClassFixture<AuroraAppFactory>
{
    private readonly AuroraAppFactory _factory;

    public ApiSurfaceTests(AuroraAppFactory factory) => _factory = factory;

    private static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private HttpClient Client()
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_factory.BearerToken}");
        return http;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, object? body, string? key)
    {
        var request = new HttpRequestMessage(method, url);
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        if (body is not null)
        {
            request.Content = new StringContent(AuroraJson.Serialize(body), Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string Key() => Guid.NewGuid().ToString("N");

    private IMemoryService MemoryStore() =>
        _factory.Services.GetRequiredService<IMemoryService>();

    private async Task<MemoryRecord> RememberAsync(
        string summary, string sensitivity = Sensitivity.Private, string policy = MemoryAccessPolicy.Owner)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var memories = scope.ServiceProvider.GetRequiredService<IMemoryService>();

        return await memories.RecordAsync(
            new MemoryCandidate(
                MemoryKind.Semantic, "person/owner", "prefers", """{"value":"tea"}""",
                summary, 0.9, sensitivity),
            new MemoryProvenance(
                ["conversation/1"], ["turn/2"], MemoryOrigin.User, policy,
                [new MemoryAnchor(MemoryAnchorKind.Conversation, "conversation/1", "the owner said so")]),
            Ct());
    }

    // ---- authentication ----

    [Fact]
    public async Task ApiRefusesACallerWithoutTheBearerToken()
    {
        var http = _factory.CreateClient();
        HttpResponseMessage response = await http.GetAsync("/v1/audit", Ct());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- rule 1: write commands are repeatable ----

    [Fact]
    public async Task WriteCommandWithoutAnIdempotencyKeyIsRefused()
    {
        using HttpClient http = Client();
        HttpResponseMessage response = await http.SendAsync(
            Request(HttpMethod.Post, "/v1/goals", new { title = "T", outcome = "O" }, key: null), Ct());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await BodyAsync(response);
        Assert.Equal(
            ApiErrorCode.IdempotencyKeyRequired,
            body.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task RepeatingAWriteWithTheSameKeyReturnsTheSameResultAndCreatesOneGoal()
    {
        using HttpClient http = Client();
        var key = Key();
        var body = new { title = "Book the flight", outcome = "A seat is reserved." };

        HttpResponseMessage first = await http.SendAsync(Request(HttpMethod.Post, "/v1/goals", body, key), Ct());
        HttpResponseMessage second = await http.SendAsync(Request(HttpMethod.Post, "/v1/goals", body, key), Ct());

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();

        var firstId = (await BodyAsync(first)).GetProperty("data").GetProperty("id").GetString();
        var secondId = (await BodyAsync(second)).GetProperty("data").GetProperty("id").GetString();

        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public async Task ReusingAKeyForADifferentRequestIsAConflictRatherThanAReplay()
    {
        using HttpClient http = Client();
        var key = Key();

        await http.SendAsync(
            Request(HttpMethod.Post, "/v1/goals", new { title = "One", outcome = "A" }, key), Ct());
        HttpResponseMessage second = await http.SendAsync(
            Request(HttpMethod.Post, "/v1/goals", new { title = "Two", outcome = "B" }, key), Ct());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        JsonElement body = await BodyAsync(second);
        Assert.Equal(
            ApiErrorCode.IdempotencyConflict,
            body.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    // ---- goals ----

    [Fact]
    public async Task APostedGoalArrivesAsADraftAndIsNotDecomposedOnArrival()
    {
        using HttpClient http = Client();
        HttpResponseMessage created = await http.SendAsync(
            Request(
                HttpMethod.Post, "/v1/goals",
                new { title = "Move house", outcome = "Everything is at the new address.", success_criteria = new[] { "No box left behind." } },
                Key()),
            Ct());

        created.EnsureSuccessStatusCode();
        var id = (await BodyAsync(created)).GetProperty("data").GetProperty("id").GetString();

        HttpResponseMessage read = await http.GetAsync($"/v1/goals/{id}", Ct());
        read.EnsureSuccessStatusCode();

        JsonElement data = (await BodyAsync(read)).GetProperty("data");
        Assert.Equal(GoalStatus.Draft, data.GetProperty("goal").GetProperty("status").GetString());
        Assert.Empty(data.GetProperty("tasks").EnumerateArray());
    }

    [Fact]
    public async Task AGoalThatDoesNotExistIsNotFound()
    {
        using HttpClient http = Client();
        HttpResponseMessage response = await http.GetAsync("/v1/goals/nope", Ct());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- rule 3: the server applies authorization ----

    [Fact]
    public async Task SearchReturnsOnlyWhatTheCallerMaySee()
    {
        MemoryRecord visible = await RememberAsync("The owner prefers tea.");
        MemoryRecord hidden = await RememberAsync("Someone else's note.", policy: "policy/other");

        using HttpClient http = Client();
        HttpResponseMessage response = await http.GetAsync("/v1/memories?q=owner", Ct());
        response.EnsureSuccessStatusCode();

        var ids = (await BodyAsync(response)).GetProperty("data").GetProperty("matches")
            .EnumerateArray()
            .Select(m => m.GetProperty("memory").GetProperty("id").GetString())
            .ToList();

        Assert.Contains(visible.Id, ids);
        Assert.DoesNotContain(hidden.Id, ids);
    }

    // ---- rule 4: an unauthorized caller does not learn a resource exists ----

    [Fact]
    public async Task AMemoryTheCallerMayNotSeeAnswersNotFoundRatherThanForbidden()
    {
        MemoryRecord hidden = await RememberAsync("Not yours.", policy: "policy/other");

        using HttpClient http = await _factory.CreateOperatorClientAsync();
        HttpResponseMessage response = await http.SendAsync(
            Request(HttpMethod.Patch, $"/v1/memories/{hidden.Id}", new { reason = "Wrong." }, Key()), Ct());

        // The distinction that matters: 403 would confirm the memory is there.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- the person controls their memory ----

    [Fact]
    public async Task TheOwnerCanCorrectAMemoryAndTheCorrectionIsRecorded()
    {
        MemoryRecord memory = await RememberAsync("The owner prefers coffee.");

        using HttpClient http = await _factory.CreateOperatorClientAsync();
        HttpResponseMessage response = await http.SendAsync(
            Request(
                HttpMethod.Patch, $"/v1/memories/{memory.Id}",
                new { reason = "It is tea, not coffee." }, Key()),
            Ct());

        response.EnsureSuccessStatusCode();
        JsonElement revision = (await BodyAsync(response)).GetProperty("data");

        Assert.Equal(RevisionOperation.Correct, revision.GetProperty("operation").GetString());
        Assert.Equal(MemoryOrigin.User, revision.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task ForgettingReportsWhatItActuallyRemoved()
    {
        MemoryRecord memory = await RememberAsync("Forget this.");

        using HttpClient http = await _factory.CreateOperatorClientAsync();
        HttpResponseMessage response = await http.SendAsync(
            Request(HttpMethod.Delete, $"/v1/memories/{memory.Id}", body: null, Key()), Ct());

        response.EnsureSuccessStatusCode();
        JsonElement tombstone = (await BodyAsync(response)).GetProperty("data");

        Assert.Equal(memory.Id, tombstone.GetProperty("memory_id").GetString());

        // And it is gone from search, not merely marked.
        HttpResponseMessage search = await http.GetAsync("/v1/memories?q=forget", Ct());
        var ids = (await BodyAsync(search)).GetProperty("data").GetProperty("matches")
            .EnumerateArray()
            .Select(m => m.GetProperty("memory").GetProperty("id").GetString())
            .ToList();

        Assert.DoesNotContain(memory.Id, ids);
    }

    // ---- events ----

    [Fact]
    public async Task TheIngressEndpointPublishesOneDeclaredEventAndNotWhateverTheCallerNames()
    {
        using HttpClient http = Client();
        HttpResponseMessage response = await http.SendAsync(
            Request(
                HttpMethod.Post, "/v1/events",
                new { observation = "the front door sensor reported motion", subject_ref = "sensor/front" },
                Key()),
            Ct());

        response.EnsureSuccessStatusCode();
        JsonElement published = (await BodyAsync(response)).GetProperty("data");

        // A surface outside Aurora choosing its own event type is a surface that can assert
        // anything about anything. It reports an observation; the type is Aurora's (LAW-007).
        Assert.Equal("ExternalObservationReported", published.GetProperty("type").GetString());
        Assert.Equal("api", published.GetProperty("producer").GetString());
        Assert.False(string.IsNullOrWhiteSpace(published.GetProperty("integrity_hash").GetString()));
    }

    [Fact]
    public async Task AnObservationThatSaysNothingIsRefused()
    {
        using HttpClient http = Client();
        HttpResponseMessage response = await http.SendAsync(
            Request(HttpMethod.Post, "/v1/events", new { observation = "" }, Key()), Ct());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- the stream ----

    [Fact]
    public async Task TheStreamResumesFromACursorRatherThanReplayingFromTheTop()
    {
        using HttpClient http = Client();

        await http.SendAsync(
            Request(
                HttpMethod.Post, "/v1/events",
                new { observation = "something worth streaming" }, Key()),
            Ct());

        var frames = await http.GetStringAsync("/v1/stream?after=0", Ct());
        Assert.Contains("event: ExternalObservationReported", frames, StringComparison.Ordinal);

        var after = frames
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("id: ", StringComparison.Ordinal))
            .Select(line => long.Parse(line[4..]))
            .Max();

        Assert.Equal(string.Empty, await http.GetStringAsync($"/v1/stream?after={after}", Ct()));
    }

    // ---- RFC 11: deciding is a person's act, and needs a person's credential ----

    [Fact]
    public async Task TheAgentSTokenCannotDecideAnApproval()
    {
        using HttpClient agent = Client();
        HttpResponseMessage response = await agent.SendAsync(
            Request(
                HttpMethod.Post, "/v1/approvals/anything/decide",
                new { decision = "approved" }, Key()),
            Ct());

        // Without this, the agent could approve its own request simply by calling the panel's API
        // instead of the tool. The bearer token belongs to the MCP client; the session does not.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheAgentSTokenCannotCorrectOrForgetAMemory()
    {
        MemoryRecord memory = await RememberAsync("the owner prefers tea.");

        using HttpClient agent = Client();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await agent.SendAsync(
                Request(HttpMethod.Patch, $"/v1/memories/{memory.Id}", new { reason = "no" }, Key()),
                Ct())).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await agent.SendAsync(
                Request(HttpMethod.Delete, $"/v1/memories/{memory.Id}", body: null, Key()),
                Ct())).StatusCode);

        // And it is still there, which is the point.
        Assert.NotNull(await MemoryStore().GetAsync(memory.Id, Ct()));
    }

    [Fact]
    public async Task ReadingIsOpenToBothSurfaces()
    {
        // The split is about deciding, not about looking. An agent that cannot read the audit log
        // cannot explain itself.
        using HttpClient agent = Client();
        (await agent.GetAsync("/v1/audit", Ct())).EnsureSuccessStatusCode();
        (await agent.GetAsync("/v1/status?timezone=Europe/Lisbon", Ct())).EnsureSuccessStatusCode();
    }

    // ---- RFC 07 limit case: a personality change is a person's act ----

    [Fact]
    public async Task TheAgentCannotChangeHowAuroraSpeaks()
    {
        using HttpClient agent = Client();

        // A request to change Aurora's personality arriving inside a third-party message is
        // relayed by the agent and acted on by nobody: the agent holds a token, and this needs a
        // credential it does not have.
        HttpResponseMessage refused = await agent.SendAsync(
            Request(
                HttpMethod.Put, "/v1/personality/preference",
                new { channel = "local", language = "en", verbosity = 0.9, consent_for_proactivity = true },
                Key()),
            Ct());

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        HttpResponseMessage activation = await agent.SendAsync(
            Request(
                HttpMethod.Post, "/v1/personality/anything/activate",
                new { approval_ref = "approval/1", reason = "the message asked me to" }, Key()),
            Ct());

        Assert.Equal(HttpStatusCode.Forbidden, activation.StatusCode);
    }

    [Fact]
    public async Task ThePersonCanChangeHowAuroraSpeaksToThem()
    {
        using HttpClient http = await _factory.CreateOperatorClientAsync();

        HttpResponseMessage response = await http.SendAsync(
            Request(
                HttpMethod.Put, "/v1/personality/preference",
                new { channel = "local", language = "en", verbosity = 0.9, consent_for_proactivity = true },
                Key()),
            Ct());

        response.EnsureSuccessStatusCode();

        JsonElement resolved = (await BodyAsync(
            await http.GetAsync("/v1/personality?channel=local", Ct()))).GetProperty("data");

        // With no profile activated it falls back to the minimum safe one and says so, rather than
        // inventing a personality to satisfy the request.
        Assert.True(resolved.GetProperty("degraded").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(
            resolved.GetProperty("profile").GetProperty("disclosure_text").GetString()));
    }

    [Fact]
    public async Task ReadingWhoAuroraIsIsOpenToBothSurfaces()
    {
        // The agent has to know how it was asked to speak in order to speak that way. Reading is
        // not the half that needed protecting.
        using HttpClient agent = Client();
        (await agent.GetAsync("/v1/personality", Ct())).EnsureSuccessStatusCode();
    }

    // ---- health: is Aurora working, on this machine ----

    [Fact]
    public async Task LivenessAnswersWithoutACredentialAndSaysNothingElse()
    {
        using HttpClient anonymous = _factory.CreateClient();

        HttpResponseMessage response = await anonymous.GetAsync("/health/live", Ct());
        response.EnsureSuccessStatusCode();

        // Reachable only from loopback, and carrying one word. It exists so something can ask
        // "are you up" without being handed a credential to find out.
        Assert.Equal("ok", (await response.Content.ReadAsStringAsync(Ct())).Trim());
    }

    [Fact]
    public async Task HealthCarriesDetailAndStaysBehindTheGuard()
    {
        using HttpClient anonymous = _factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/health", Ct())).StatusCode);

        using HttpClient http = Client();
        HttpResponseMessage response = await http.GetAsync("/health", Ct());

        JsonElement body = await BodyAsync(response);

        Assert.Equal(7, body.GetProperty("checks").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("status").GetString()));
        Assert.True(body.GetProperty("schema_version").GetInt32() > 0);

        Assert.True(
            body.GetProperty("status").GetString() == "FAIL"
                ? response.StatusCode == HttpStatusCode.ServiceUnavailable
                : response.IsSuccessStatusCode);
    }

    // ---- RFC 11: the panel itself ----

    [Fact]
    public async Task ThePanelIsNotServedToAnyoneWithoutACredential()
    {
        using HttpClient anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/ui/", Ct())).StatusCode);
    }

    [Fact]
    public async Task AnOperatorLinkWorksOnceAndOnlyOnce()
    {
        var grant = _factory.Services.GetRequiredService<OperatorSessions>().Mint();

        using HttpClient first = _factory.CreateDefaultClient(
            new Microsoft.AspNetCore.Mvc.Testing.Handlers.CookieContainerHandler());

        HttpResponseMessage redeemed = await first.GetAsync($"/ui/session?t={grant}", Ct());
        Assert.NotEqual(HttpStatusCode.Unauthorized, redeemed.StatusCode);

        // A link that keeps working is a link that keeps working for whoever finds it in a shell
        // history or a screenshot.
        using HttpClient second = _factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await second.GetAsync($"/ui/session?t={grant}", Ct())).StatusCode);
    }

    [Fact]
    public async Task ThePanelLoadsForAnOperatorAndCarriesNoRemoteOrigin()
    {
        using HttpClient http = await _factory.CreateOperatorClientAsync();

        HttpResponseMessage page = await http.GetAsync("/ui/", Ct());
        page.EnsureSuccessStatusCode();

        var html = await page.Content.ReadAsStringAsync(Ct());

        Assert.Contains("Aurora", html, StringComparison.Ordinal);
        Assert.Contains("/ui/app.js", html, StringComparison.Ordinal);

        // Nothing loads from anywhere else. The page where approvals are decided is the last page
        // in the system that should be able to.
        Assert.DoesNotContain("http://", html.Replace("http://127.0.0.1", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);

        var policy = page.Headers.TryGetValues("Content-Security-Policy", out var values)
            ? string.Join(' ', values)
            : string.Empty;

        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SigningOutEndsTheSessionImmediately()
    {
        using HttpClient http = await _factory.CreateOperatorClientAsync();
        (await http.PostAsync("/ui/session/end", content: null, Ct())).EnsureSuccessStatusCode();

        // The cookie is still in the jar; the server has simply stopped honouring it. That leaves
        // the client with no credential at all — 401, not 403, which is the stronger answer: it is
        // not that this caller may not decide, it is that there is no caller.
        HttpResponseMessage after = await http.SendAsync(
            Request(
                HttpMethod.Post, "/v1/approvals/anything/decide",
                new { decision = "approved" }, Key()),
            Ct());

        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    // ---- status and upkeep ----

    [Fact]
    public async Task StatusReportsWhatAuroraHasNoticesAndIsWaitingOn()
    {
        using HttpClient http = Client();
        HttpResponseMessage response = await http.GetAsync("/v1/status?timezone=Europe/Lisbon", Ct());
        response.EnsureSuccessStatusCode();

        JsonElement data = (await BodyAsync(response)).GetProperty("data");

        // The observability half of the gate: an automation nobody can look at is not limited by
        // anything, whatever its rules say.
        Assert.False(string.IsNullOrWhiteSpace(
            data.GetProperty("situation").GetProperty("risk_posture").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            data.GetProperty("resources").GetProperty("status").GetString()));
        Assert.True(data.TryGetProperty("needs", out _));
        Assert.True(data.TryGetProperty("schedules", out _));
    }

    [Fact]
    public async Task AnUnknownTimeZoneIsRefusedRatherThanQuietlyTreatedAsUtc()
    {
        using HttpClient http = Client();
        HttpResponseMessage response = await http.GetAsync("/v1/status?timezone=Mars/Olympus_Mons", Ct());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AMaintenancePassReportsWhatItDidAndWhatItDidNotLookAt()
    {
        using HttpClient http = Client();
        HttpResponseMessage response = await http.SendAsync(
            Request(HttpMethod.Post, "/v1/maintenance?timezone=Europe/Lisbon", body: null, Key()), Ct());

        response.EnsureSuccessStatusCode();
        JsonElement report = (await BodyAsync(response)).GetProperty("data");

        Assert.True(report.TryGetProperty("due_run_ids", out _));
        Assert.Contains(
            "overdue_goals",
            report.GetProperty("unmeasured").EnumerateArray().Select(u => u.GetString()));
    }

    [Fact]
    public async Task DecidingAnApprovalThatDoesNotExistSaysSoWithoutFailing()
    {
        using HttpClient http = await _factory.CreateOperatorClientAsync();
        HttpResponseMessage response = await http.SendAsync(
            Request(
                HttpMethod.Post, "/v1/approvals/does-not-exist/decide",
                new { decision = "approved" }, Key()),
            Ct());

        response.EnsureSuccessStatusCode();
        JsonElement data = (await BodyAsync(response)).GetProperty("data");

        Assert.Equal(ApproveStatus.NotFound, data.GetProperty("status").GetString());
    }

    // ---- plugin management belongs to the person, not the agent (docs/adr/0063) ----

    [Fact]
    public async Task TheAgentSTokenCannotSeeOrDecideAboutPlugins()
    {
        using HttpClient http = Client();

        // What is installed is what somebody holding the agent's token would most want to read
        // before deciding what to attack.
        Assert.Equal(
            HttpStatusCode.Forbidden, (await http.GetAsync("/v1/plugins", Ct())).StatusCode);

        HttpResponseMessage decided = await http.PostAsJsonAsync(
            "/v1/plugins/acme%2Fnotes/decide",
            new { decision = "disable", reason = "because" },
            Ct());

        Assert.Equal(HttpStatusCode.Forbidden, decided.StatusCode);
    }

    [Fact]
    public async Task AnOperatorSeesWhatIsInstalled()
    {
        using HttpClient http = await _factory.CreateOperatorClientAsync();

        HttpResponseMessage response = await http.GetAsync("/v1/plugins", Ct());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement body = await BodyAsync(response);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task DecidingAboutAPluginThatIsNotInstalledSaysSo()
    {
        using HttpClient http = await _factory.CreateOperatorClientAsync();

        HttpResponseMessage response = await http.PostAsJsonAsync(
            "/v1/plugins/nobody%2Fnothing/decide",
            new { decision = "disable", reason = "tidying up" },
            Ct());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- the two security events, through the real flow (docs/adr/0064) ----

    [Fact]
    public async Task RepeatedBadCredentialsRaiseAnIncidentThroughTheRealMiddleware()
    {
        var incidents = _factory.Services.GetRequiredService<IIncidentService>();
        var before = (await incidents.OpenIncidentsAsync(Ct())).Count;

        using HttpClient anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-the-token");

        // Five, which is where the line is. Every one of them is refused by the middleware; what
        // is being asserted is that the fifth also produces an incident.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/status", Ct())).StatusCode);
        }

        IReadOnlyList<Incident> open = await incidents.OpenIncidentsAsync(Ct());
        Assert.Equal(before + 1, open.Count);

        Incident raised = open[0];
        Assert.Equal(SecurityEventType.AuthenticationAbuse, raised.Event.Type);
        Assert.Equal(IncidentStatus.Contained, raised.Status);

        // Contained means something was actually revoked, not that a row was written.
        Assert.Contains(
            raised.ContainmentActions,
            a => a.Contains("consent session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAgentReachingForAPersonSDecisionRaisesAnEscalation()
    {
        var incidents = _factory.Services.GetRequiredService<IIncidentService>();
        var before = (await incidents.OpenIncidentsAsync(Ct())).Count;

        using HttpClient http = Client();

        // The agent holds a valid credential and used it on an endpoint that is not its to call.
        // That is not a mistake the way a missing token is: it had to choose this over the tool.
        Assert.Equal(
            HttpStatusCode.Forbidden, (await http.GetAsync("/v1/plugins", Ct())).StatusCode);

        // Fire and forget on the request path, so the recording may land just after the response.
        Incident? raised = null;

        for (var attempt = 0; attempt < 40 && raised is null; attempt++)
        {
            IReadOnlyList<Incident> open = await incidents.OpenIncidentsAsync(Ct());

            raised = open.FirstOrDefault(
                i => i.Event.Type == SecurityEventType.PrivilegeEscalation);

            if (raised is null)
            {
                await Task.Delay(25, Ct());
            }
        }

        Assert.NotNull(raised);
        Assert.Contains("/v1/plugins", raised!.Event.EvidenceRef, StringComparison.Ordinal);
        Assert.True((await incidents.OpenIncidentsAsync(Ct())).Count > before);
    }
}
