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

    /// <summary>Root of the writable sandbox for <c>files.write_sandbox</c> (docs/adr/0003).</summary>
    public required string SandboxRoot { get; init; }

    /// <summary>
    /// Whether the sandbox file capabilities are registered in the catalog. Default false since
    /// the re-baseline (docs/adr/0012): they are step 8 of the frozen implementation order and
    /// their prerequisites (steps 3-7) do not exist yet. The code and tests stay; the capability
    /// is simply not offered.
    /// </summary>
    public bool SandboxFilesEnabled { get; init; }

    /// <summary>
    /// How long a reservation may sit in EXECUTING before startup reconciliation calls it
    /// indeterminate. Long enough that a slow-but-live execution is never stolen from itself.
    /// </summary>
    public TimeSpan ExecutingStaleAfter { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>File holding the key that encrypts Mind State snapshots (docs/adr/0018).</summary>
    public required string SnapshotKeyPath { get; init; }

    /// <summary>File holding the ECDSA key that signs genome manifests (docs/adr/0017).</summary>
    public required string GenomeKeyPath { get; init; }

    /// <summary>File holding the key that encrypts vault secrets at rest (docs/adr/0014).</summary>
    public required string VaultKeyPath { get; init; }

    /// <summary>File holding the operator passphrase verifier (docs/adr/0011).</summary>
    public required string PassphrasePath { get; init; }

    /// <summary>File holding the HMAC key that signs the audit chain (docs/adr/0005).</summary>
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

        var sandboxFilesEnabled = config.GetValue<bool?>("Aurora:SandboxFilesEnabled") ?? false;

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
        var snapshotKeyPath = config["Aurora:SnapshotKeyPath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "aurora.snapshot.key");

        var genomeKeyPath = config["Aurora:GenomeKeyPath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "aurora.genome.key");

        var vaultKeyPath = config["Aurora:VaultKeyPath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "aurora.vault.key");

        var passphrasePath = config["Aurora:PassphrasePath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "aurora.passphrase.json");

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
            SandboxFilesEnabled = sandboxFilesEnabled,
            SnapshotKeyPath = snapshotKeyPath,
            GenomeKeyPath = genomeKeyPath,
            VaultKeyPath = vaultKeyPath,
            PassphrasePath = passphrasePath,
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
