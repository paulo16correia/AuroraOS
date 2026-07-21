using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aurora.Tests.Support;

/// <summary>
/// Hosts the real Aurora server in-memory for integration tests, with a known bearer token and an
/// isolated temp SQLite database per factory instance.
/// </summary>
public sealed class AuroraAppFactory : WebApplicationFactory<Program>
{
    public string BearerToken { get; } = "test-bearer-token-" + Guid.NewGuid().ToString("N");

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"aurora-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Aurora:BearerToken", BearerToken);
        builder.UseSetting("Aurora:DbPath", _dbPath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            TryDelete(_dbPath + suffix);
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
