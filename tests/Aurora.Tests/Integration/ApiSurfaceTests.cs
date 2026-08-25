using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
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

        using HttpClient http = Client();
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

        using HttpClient http = Client();
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

        using HttpClient http = Client();
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
        using HttpClient http = Client();
        HttpResponseMessage response = await http.SendAsync(
            Request(
                HttpMethod.Post, "/v1/approvals/does-not-exist/decide",
                new { decision = "approved" }, Key()),
            Ct());

        response.EnsureSuccessStatusCode();
        JsonElement data = (await BodyAsync(response)).GetProperty("data");

        Assert.Equal(ApproveStatus.NotFound, data.GetProperty("status").GetString());
    }
}
