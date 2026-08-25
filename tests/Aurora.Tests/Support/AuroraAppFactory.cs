using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aurora.Tests.Support;

/// <summary>
/// Hosts the real Aurora server in-memory for integration tests, with a known bearer token and an
/// isolated temp SQLite database and sandbox root per factory instance.
/// </summary>
public sealed class AuroraAppFactory : WebApplicationFactory<Program>
{
    public string BearerToken { get; } = "test-bearer-token-" + Guid.NewGuid().ToString("N");

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"aurora-test-{Guid.NewGuid():N}.db");

    /// <summary>Passphrase verifier file for this instance, so enrolling never touches the real one.</summary>
    public string PassphrasePath { get; } =
        Path.Combine(Path.GetTempPath(), $"aurora-pass-{Guid.NewGuid():N}.json");

    /// <summary>Sandbox root for this instance, so a test write never escapes into the real one.</summary>
    public string SandboxRoot { get; } =
        Path.Combine(Path.GetTempPath(), $"aurora-sandbox-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Aurora:BearerToken", BearerToken);
        builder.UseSetting("Aurora:DbPath", _dbPath);
        builder.UseSetting("Aurora:SandboxRoot", SandboxRoot);
        builder.UseSetting("Aurora:PassphrasePath", PassphrasePath);

        // Stated rather than relied on. It matches the production default since docs/adr/0037, and
        // a test that silently depended on that default would stop covering the capabilities the
        // day somebody changed it.
        builder.UseSetting("Aurora:SandboxFilesEnabled", "true");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", ".anchor" })
        {
            TryDelete(_dbPath + suffix);
        }

        TryDelete(PassphrasePath);

        try
        {
            if (Directory.Exists(SandboxRoot))
            {
                Directory.Delete(SandboxRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked WAL file will be reclaimed by the OS later.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }
}
