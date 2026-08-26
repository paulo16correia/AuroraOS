using System.Net;
using Aurora.Server.Security;
using Microsoft.Extensions.DependencyInjection;
using Aurora.Core.Abstractions;
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

        // A deterministic host. Without this the integration tests read the machine's real disk,
        // so a developer with a full drive watches Aurora correctly refuse every effectful action
        // and incorrectly conclude the tests are broken.
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IResourceProbe>(new StubResourceProbe());

            // No windows during a test run. The real NativeDialog finds osascript on macOS and
            // would put a password prompt on a developer's screen mid-suite — and then block until
            // somebody dismissed it, which is how a test suite becomes something people stop
            // running. Availability is false here, so the kernel takes the supplied-passphrase path.
            services.AddSingleton<IOperatorPrompt>(new NoOperatorPrompt());
        });
    }

    /// <summary>
    /// A client carrying an operator session, as a browser would after following the printed link.
    /// </summary>
    /// <remarks>
    /// Deliberately not a shortcut past the exchange: it mints a grant and redeems it through the
    /// real endpoint, so the tests exercise the path an operator actually takes.
    /// </remarks>
    public async Task<HttpClient> CreateOperatorClientAsync()
    {
        HttpClient http = CreateDefaultClient(new Microsoft.AspNetCore.Mvc.Testing.Handlers.CookieContainerHandler());

        var grant = Services.GetRequiredService<OperatorSessions>().Mint();
        HttpResponseMessage redeemed = await http.GetAsync($"/ui/session?t={grant}");

        if (redeemed.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Redirect
            or HttpStatusCode.Found or HttpStatusCode.MovedPermanently))
        {
            throw new InvalidOperationException($"Could not open an operator session: {redeemed.StatusCode}.");
        }

        return http;
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

/// <summary>A host that is comfortably idle, so integration tests are about Aurora.</summary>
internal sealed class StubResourceProbe : IResourceProbe
{
    public ResourceReading Read() => new(
        CpuFraction: 0.1, MemoryFraction: 0.2, DiskFraction: 0.3,
        DiskFreeBytes: 64L * 1024 * 1024 * 1024);
}
