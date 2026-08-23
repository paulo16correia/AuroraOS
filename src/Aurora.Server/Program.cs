using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Server;
using Aurora.Server.Mcp;
using Aurora.Server.Security;

var builder = WebApplication.CreateBuilder(args);

var options = AuroraServerOptions.FromConfiguration(builder.Configuration);

// Passphrase enrolment happens on this console, never over HTTP: the bearer token belongs to
// the agent, so any endpoint it can reach is one the agent could use to enrol its own.
if (PassphraseConsole.TryHandle(args, options))
{
    return;
}

// Loopback-only Kestrel binding for real runs (bypassed by TestServer under WebApplicationFactory).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenLocalhost(options.Port);
    // Resource guard: reject oversized bodies at the transport before any parsing/canonicalization.
    kestrel.Limits.MaxRequestBodySize = 64 * 1024;
});

builder.Services.AddAurora(options);
builder.Services
    .AddMcpServer()
    .WithHttpTransport(http => http.Stateless = true)
    .WithTools<AuroraTools>();

var app = builder.Build();

// Migrate, then fail closed if the existing audit chain fails its integrity check.
app.Services.GetRequiredService<SqliteDatabase>().Initialize();
var auditVerification = await app.Services.GetRequiredService<IAuditStore>().VerifyChainAsync(CancellationToken.None);
if (!auditVerification.Ok)
{
    throw new InvalidOperationException(
        $"Audit chain integrity verification failed at sequence {auditVerification.BrokenSequence}; refusing to start.");
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

// Security pipeline: loopback/Origin guard, then bearer auth, before the MCP endpoints.
app.UseMiddleware<LoopbackGuardMiddleware>();
app.UseMiddleware<BearerAuthMiddleware>();

app.MapMcp("/mcp");

// Operational health, behind the same loopback + bearer guard as the MCP surface. Deliberately
// NOT an MCP tool: these numbers are for the operator, and exposing them to the agent would
// hand an untrusted reasoner a view of how often its requests are being refused.
app.MapGet("/metrics", async (
        IAuroraMetrics metrics, IApprovalStore approvals, IConsentSessionStore sessions, CancellationToken ct) =>
    Results.Json(metrics.Snapshot(
        await approvals.CountPendingAsync(ct), await sessions.CountActiveAsync(ct))));

// Kill switch (docs/adr/0010). Revokes every consent session at once, including any left by an
// earlier run, so an operator pressing it does not have to reason about restarts. Operator
// surface, not an MCP tool: the agent must not be able to reason about its own leash.
app.MapPost("/sessions/revoke", async (IConsentSessionStore sessions, CancellationToken ct) =>
    Results.Json(new { revoked = await sessions.RevokeAllAsync(ct) }));

app.Run();

/// <summary>Exposed so WebApplicationFactory can host the app in integration tests.</summary>
public partial class Program;
