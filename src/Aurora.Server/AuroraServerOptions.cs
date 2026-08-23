using System.Security.Cryptography;

using Aurora.Adapters.Reasoning;

namespace Aurora.Server;

/// <summary>
/// Runtime options resolved from configuration/environment. The bearer token is required; if none
/// is supplied one is generated for the run (and printed once) so the server is never unprotected.
/// </summary>
public sealed class AuroraServerOptions
{
    public required string BearerToken { get; init; }

    public int Port { get; init; } = 5099;

    public required string DbPath { get; init; }

    /// <summary>Root of the writable sandbox for <c>files.write_sandbox</c> (design/0003).</summary>
    public required string SandboxRoot { get; init; }

    /// <summary>
    /// How long a reservation may sit in EXECUTING before startup reconciliation calls it
    /// indeterminate. Long enough that a slow-but-live execution is never stolen from itself.
    /// </summary>
    public TimeSpan ExecutingStaleAfter { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>File holding the HMAC key that signs the audit chain (design/0005).</summary>
    public required string AuditKeyPath { get; init; }

    /// <summary>File mirroring the audit head, so a truncated tail is detectable.</summary>
    public required string AuditAnchorPath { get; init; }

    /// <summary>Azure OpenAI settings, or null when objective mode falls back to keywords only.</summary>
    public AzureOpenAiOptions? AzureOpenAi { get; init; }

    public static AuroraServerOptions FromConfiguration(IConfiguration config)
    {
        var token = config["Aurora:BearerToken"]
            ?? Environment.GetEnvironmentVariable("AURORA_BEARER_TOKEN");
        var generated = false;
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            generated = true;
        }

        var port = config.GetValue<int?>("Aurora:Port") ?? 5099;

        var dbPath = config["Aurora:DbPath"];
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aurora");
            Directory.CreateDirectory(dir);
            dbPath = Path.Combine(dir, "aurora.db");
        }

        var sandboxRoot = config["Aurora:SandboxRoot"];
        if (string.IsNullOrWhiteSpace(sandboxRoot))
        {
            sandboxRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aurora", "sandbox");
        }

        Directory.CreateDirectory(sandboxRoot);

        // Default the audit key and anchor beside the database, but keep them configurable so an
        // operator can put the key somewhere the database's own backups do not reach.
        var auditKeyPath = config["Aurora:AuditKeyPath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "aurora.audit.key");
        // Derived from the database file, not the directory: two databases side by side must not
        // share one anchor, or each would read the other's head as evidence of truncation.
        var auditAnchorPath = config["Aurora:AuditAnchorPath"]
            ?? Path.GetFullPath(dbPath) + ".anchor";

        // Objective mode only reaches the model when all three are present; otherwise the
        // keyword fallback stands in, restricted to LOW read-only actions.
        var azureEndpoint = config["Aurora:AzureOpenAI:Endpoint"];
        var azureDeployment = config["Aurora:AzureOpenAI:Deployment"];
        var azureKey = config["Aurora:AzureOpenAI:ApiKey"]
            ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

        AzureOpenAiOptions? azure = null;
        if (!string.IsNullOrWhiteSpace(azureEndpoint)
            && !string.IsNullOrWhiteSpace(azureDeployment)
            && !string.IsNullOrWhiteSpace(azureKey))
        {
            azure = new AzureOpenAiOptions(
                azureEndpoint,
                azureDeployment,
                azureKey,
                config["Aurora:AzureOpenAI:ApiVersion"] ?? "2024-10-21");
        }

        var options = new AuroraServerOptions
        {
            BearerToken = token,
            Port = port,
            DbPath = dbPath,
            SandboxRoot = sandboxRoot,
            AuditKeyPath = auditKeyPath,
            AuditAnchorPath = auditAnchorPath,
            AzureOpenAi = azure,
        };
        if (generated)
        {
            Console.WriteLine($"[Aurora] No bearer token configured; generated one for this run: {token}");
        }

        return options;
    }
}
