using System.Security.Cryptography;

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

        var options = new AuroraServerOptions
        {
            BearerToken = token, Port = port, DbPath = dbPath, SandboxRoot = sandboxRoot,
        };
        if (generated)
        {
            Console.WriteLine($"[Aurora] No bearer token configured; generated one for this run: {token}");
        }

        return options;
    }
}
