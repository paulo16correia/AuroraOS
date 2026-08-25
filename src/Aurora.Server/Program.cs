using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Server;
using Aurora.Server.Api;
using Aurora.Server.Mcp;
using Aurora.Server.Ui;
using Aurora.Server.Security;

var builder = WebApplication.CreateBuilder(args);

var options = AuroraServerOptions.FromConfiguration(builder.Configuration);

// Passphrase enrolment happens on this console, never over HTTP: the bearer token belongs to
// the agent, so any endpoint it can reach is one the agent could use to enrol its own.
if (PassphraseConsole.TryHandle(args, options) || OperationsConsole.TryHandle(args, options))
{
    return;
}

// Loopback-only Kestrel binding for real runs (bypassed by TestServer under WebApplicationFactory).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Loopback on a desktop install; a container has to bind its whole namespace or the proxy
    // cannot reach it. Binding beyond loopback without declaring the allowed hosts is refused
    // when the options are read, so this cannot quietly become an open port.
    if (options.BindAddress is "127.0.0.1" or "localhost")
    {
        kestrel.ListenLocalhost(options.Port);
    }
    else
    {
        kestrel.Listen(System.Net.IPAddress.Parse(options.BindAddress), options.Port);
    }
    // Resource guard: reject oversized bodies at the transport before any parsing/canonicalization.
    kestrel.Limits.MaxRequestBodySize = 64 * 1024;
});

// One wire contract for the whole surface: minimal-API binding and responses use the same
// snake_case rules as the payloads Aurora stores and replays.
builder.Services.ConfigureHttpJsonOptions(json => Aurora.Core.AuroraJson.Apply(json.SerializerOptions));

builder.Services.AddAurora(options);
builder.Services
    .AddMcpServer()
    .WithHttpTransport(http => http.Stateless = true)
    .WithTools<AuroraTools>();

var app = builder.Build();

// Migrate, then fail closed if the existing audit chain fails its integrity check.
app.Services.GetRequiredService<SqliteDatabase>().Initialize();
var auditVerification = await app.Services.GetRequiredService<IAuditStore>().VerifyChainAsync(CancellationToken.None);

// RFC 12 limit case: an incorrect clock blocks anything that depends on expiry. Approvals,
// consent sessions and schedules are all promises about time, and a clock that has gone backwards
// turns them into something else. Checked before serving rather than discovered afterwards.
ClockVerdict clockVerdict = await app.Services.GetRequiredService<IClockGuard>()
    .CheckAsync(CancellationToken.None);

if (!clockVerdict.Trustworthy)
{
    throw new InvalidOperationException(
        $"This machine's clock cannot be trusted: {clockVerdict.Detail}. Refusing to start. "
        + "Synchronise the clock and start again; nothing here needs to be repaired.");
}

if (auditVerification is { Ok: true, AcknowledgedBreakAt: { } seam })
{
    Console.WriteLine(
        $"[Aurora] The audit chain verifies from record {seam} onwards. Everything before that "
        + "seam is permanently unverifiable and is recorded as such.");
}

if (!auditVerification.Ok)
{
    // Fail closed, and say what can be done about it. A refusal with no way forward is not
    // fail-closed, it is bricked — the usual cause is a signing key that was lost or replaced.
    throw new InvalidOperationException(
        $"Audit chain integrity verification failed at sequence {auditVerification.BrokenSequence}"
        + $"{(auditVerification.Reason is { } why ? $": {why}" : ".")} Refusing to start. "
        + "If the signing key was lost or replaced, run 'seal-audit-break <reason>' on this console: "
        + "it records the discontinuity permanently and starts a new chain, and repairs nothing.");
}

// A process that died mid-effect leaves reservations in EXECUTING, which is deliberately not
// retryable. Move the stale ones to UNKNOWN before serving, so those keys stop being wedged
// and an operator can see them (docs/adr/0007).
var reconciled = await app.Services.GetRequiredService<IIdempotencyStore>()
    .ReconcileStaleAsync(options.ExecutingStaleAfter, CancellationToken.None);
if (reconciled > 0)
{
    Console.WriteLine(
        $"[Aurora] Reconciled {reconciled} reservation(s) stuck in EXECUTING into UNKNOWN.");
}

// Security pipeline: loopback/Origin guard, then auth, before anything else.
app.UseMiddleware<LoopbackGuardMiddleware>();

// Redeeming a printed link happens before auth, because the point of the link is that the browser
// does not hold a credential yet. Still behind the loopback guard: the link is only usable from
// this machine.
app.MapGet("/ui/session", (string? t, HttpContext context, OperatorSessions sessions) =>
    UiSessionExchange.Redeem(t, context, sessions));

app.UseMiddleware<BearerAuthMiddleware>();

app.MapMcp("/mcp");

// The operator and UI surface (RFC 10), behind the same loopback + bearer guard. Separate from
// MCP on purpose: these are the endpoints a person uses to decide, correct and inspect what
// Aurora did, and they must not be reachable as agent tools.
app.MapAuroraApi();

// The control panel (RFC 11): the command post for memory, approvals, auditing and health.
app.MapAuroraUi();

// Operational health, behind the same loopback + bearer guard as the MCP surface. Deliberately
// NOT an MCP tool: these numbers are for the operator, and exposing them to the agent would
// hand an untrusted reasoner a view of how often its requests are being refused.
// Liveness, and nothing else: the process is up and answering. Unauthenticated on purpose so a
// container runtime can poll it, and therefore carrying nothing worth reading — a health endpoint
// is the most-scraped surface a system has.
app.MapGet("/health/live", () => Results.Text("ok", "text/plain"));

// Readiness, with detail, behind the same guard as everything else. RFC 12 rule 2: a release
// passes its checks before receiving traffic, and this is what a deploy script asks.
app.MapGet("/health", async (IHealthService health, CancellationToken ct) =>
{
    IReadOnlyList<HealthCheck> checks = await health.ReadAsync(ct);
    var overall = HealthStatus.Worst(checks.Select(c => c.Status));

    return Results.Json(
        new { status = overall, schema_version = SqliteDatabase.TargetSchemaVersion, checks },

        // A failing system answers 503 so a proxy or an orchestrator can act on it without
        // parsing anything. WARN still serves: it means look, not stop.
        statusCode: overall == HealthStatus.Fail
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK);
});

app.MapGet("/metrics", async (
        IAuroraMetrics metrics, IApprovalStore approvals, IConsentSessionStore sessions, CancellationToken ct) =>
    Results.Json(metrics.Snapshot(
        await approvals.CountPendingAsync(ct), await sessions.CountActiveAsync(ct))));

// Kill switch (docs/adr/0010). Revokes every consent session at once, including any left by an
// earlier run, so an operator pressing it does not have to reason about restarts. Operator
// surface, not an MCP tool: the agent must not be able to reason about its own leash.
app.MapPost("/sessions/revoke", async (IConsentSessionStore sessions, CancellationToken ct) =>
    Results.Json(new { revoked = await sessions.RevokeAllAsync(ct) }));

// The panel's link is printed on this console and nowhere else. It is the only way a person gets
// a credential the agent does not have, and printing it here is what keeps it that way.
if (args.Contains("ui", StringComparer.Ordinal))
{
    var grant = app.Services.GetRequiredService<OperatorSessions>().Mint();
    Console.WriteLine();
    Console.WriteLine("[Aurora] Control panel — open this once, within ten minutes:");
    Console.WriteLine($"         http://127.0.0.1:{options.Port}/ui/session?t={grant}");
    Console.WriteLine("[Aurora] The link is single-use. Anyone holding it gets an operator session.");
    Console.WriteLine();
}

app.Run();

/// <summary>Exposed so WebApplicationFactory can host the app in integration tests.</summary>
public partial class Program;
