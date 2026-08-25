using System.Security.Cryptography;

using Aurora.Adapters.Reasoning;

using Aurora.Adapters.Files;

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
    /// The address Kestrel binds. Loopback by default, which is the desktop install.
    /// </summary>
    /// <remarks>
    /// A container has to bind <c>0.0.0.0</c> or nothing outside its own namespace can reach it —
    /// including the reverse proxy. Doing that without also naming <see cref="AllowedHosts"/> is
    /// refused at startup: the loopback binding and the Host guard are one control, and quietly
    /// keeping half of it would be worse than having neither.
    /// </remarks>
    public string BindAddress { get; init; } = "127.0.0.1";

    /// <summary>
    /// Host names this instance answers to, beyond loopback.
    /// </summary>
    /// <remarks>
    /// Empty on a desktop install, where loopback is the whole story. Behind a proxy the forwarded
    /// Host is the public name, so it has to be named — an allowlist the operator writes, rather
    /// than a guard that switches itself off when it becomes inconvenient.
    /// </remarks>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>
    /// Whether the sandbox file capabilities are offered in the catalog.
    /// </summary>
    /// <remarks>
    /// Frozen off by the re-baseline (docs/adr/0012) because they were built at step 8 before
    /// steps 3–7 existed; unfrozen by the owner's decision in docs/adr/0037, now that those steps
    /// do exist and the review's conditions are closed. Default true.
    /// <para>
    /// Still a switch, because turning them off is a legitimate thing to want: an instance that
    /// has no business touching files should not offer to. Nothing about the switch is what makes
    /// them safe — the approval gate is, and that gate applies on every single call.
    /// </para>
    /// </remarks>
    public bool SandboxFilesEnabled { get; init; } = true;

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

        var sandboxFilesEnabled = config.GetValue<bool?>("Aurora:SandboxFilesEnabled") ?? true;

        var bindAddress = config["Aurora:BindAddress"];
        bindAddress = string.IsNullOrWhiteSpace(bindAddress) ? "127.0.0.1" : bindAddress.Trim();

        var allowedHosts = (config["Aurora:AllowedHosts"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var isLoopback = bindAddress is "127.0.0.1" or "::1" or "localhost";

        // Fail closed on the combination that looks like it works and does not: reachable from the
        // network, and still judging every request against a guard that only knows loopback.
        if (!isLoopback && allowedHosts.Count == 0)
        {
            throw new InvalidOperationException(
                $"Aurora:BindAddress is '{bindAddress}', so this instance is reachable beyond "
                + "loopback. Name the host(s) it answers to in Aurora:AllowedHosts — the binding "
                + "and the Host guard are one control and cannot be half-applied.");
        }

        var sandboxRoot = config["Aurora:SandboxRoot"];
        if (string.IsNullOrWhiteSpace(sandboxRoot))
        {
            sandboxRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aurora", "sandbox");
        }

        Directory.CreateDirectory(sandboxRoot);

        // At creation, not at first use. The writer restricts the root when it is constructed, but
        // it is constructed lazily — so on an instance that never touches a file, the sandbox would
        // sit world-readable indefinitely, which is exactly the precondition the path hardening
        // rests on (docs/adr/0036).
        SandboxGuard.RestrictToOwner(sandboxRoot);

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
            BindAddress = bindAddress,
            AllowedHosts = allowedHosts,
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
