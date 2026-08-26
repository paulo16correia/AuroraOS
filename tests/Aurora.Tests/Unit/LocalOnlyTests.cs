using System.Text.RegularExpressions;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Aurora reaches nothing outside this machine, asserted over the source rather than remembered.
/// </summary>
/// <remarks>
/// A reflection test cannot see <c>new HttpClient()</c>, and a decision this load-bearing should
/// not rest on somebody having grepped for it once (docs/adr/0045, 0051). So this reads the source
/// tree and fails on anything that could open an outbound connection, with the single loopback
/// call named explicitly.
/// <para>
/// Unusual, and worth it: local-only is the property every other guarantee in Aurora is built on
/// top of, and the way it would be lost is one line in one pull request that nobody thought about.
/// </para>
/// <para>
/// <b>What local-only means since docs/adr/0067.</b> Aurora's own process opens no connection —
/// that is what these tests check, and it did not weaken when plugins were granted the network. A
/// plugin runs in its own sandboxed subprocess, and an integration that must reach a service does
/// it from there, with hosts the owner named and agreed to. The control plane stays here: the
/// kernel, policy, approvals, audit, memory and every credential never leave the machine. A plugin
/// reaching Discord is an external effect Aurora governs, not Aurora becoming networked.
/// </para>
public sealed class LocalOnlyTests
{
    /// <summary>Constructs that can reach another machine.</summary>
    private static readonly (string Pattern, string What)[] Outbound =
    [
        (@"new\s+HttpClient\b", "an HTTP client"),
        (@"new\s+HttpMessageInvoker\b", "an HTTP invoker"),
        (@"IHttpClientFactory", "an HTTP client factory"),
        (@"AddHttpClient\b", "an HTTP client registration"),
        (@"new\s+TcpClient\b", "a TCP client"),
        (@"new\s+UdpClient\b", "a UDP client"),
        (@"new\s+Socket\(", "a raw socket"),
        (@"new\s+ClientWebSocket\b", "a websocket client"),
        (@"new\s+SmtpClient\b", "a mail client"),
        (@"\bDns\.(Get|Resolve)", "a DNS lookup"),
        (@"new\s+WebClient\b", "a web client"),
    ];

    /// <summary>
    /// The one place Aurora opens a connection, and why it is allowed.
    /// </summary>
    /// <remarks>
    /// The console's health verb asks Aurora's own liveness endpoint on 127.0.0.1, because the
    /// runtime image carries no curl and giving a health probe a bearer token would be handing out
    /// a credential to save a word. It cannot reach another machine: the address is a literal.
    /// </remarks>
    private const string AllowedFile = "OperationsConsole.cs";

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src");
    }

    [Fact]
    public void NothingInAuroraCanOpenAnOutboundConnection()
    {
        var findings = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach ((var pattern, var what) in Outbound)
            {
                if (!Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(2)))
                {
                    continue;
                }

                if (name == AllowedFile && what == "an HTTP client")
                {
                    // Allowed, and only because it is pinned to 127.0.0.1 below.
                    continue;
                }

                findings.Add($"{name} constructs {what}");
            }
        }

        // If this fails, somebody added a way for Aurora to reach another machine. That may be the
        // right thing to do — and it is a change to the architecture (docs/adr/0045), not a change
        // to a file.
        Assert.Empty(findings);
    }

    [Fact]
    public void TheOneConnectionAuroraOpensIsToItself()
    {
        var console = Path.Combine(SourceRoot(), "Aurora.Server", AllowedFile);
        var text = File.ReadAllText(console);

        // A literal, so no configuration and no environment variable can point it elsewhere.
        Assert.Contains("http://127.0.0.1:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("http://\" +", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AuroraStillOpensNothingItselfNowThatPluginsMay()
    {
        var source = File.ReadAllText(
            Path.Combine(SourceRoot(), "Aurora.Adapters", "Plugins", "ServiceProcess.cs"));

        // The service host starts a process and talks to it over pipes. If it ever grows a socket
        // of its own, the boundary has moved from "the plugin reaches out" to "Aurora does", and
        // the sandbox stops being what stands between the two.
        foreach ((var pattern, _) in Outbound)
        {
            Assert.False(
                Regex.IsMatch(source, pattern, RegexOptions.None, TimeSpan.FromSeconds(2)),
                $"the service host matched {pattern}; the plugin is what reaches out, not Aurora");
        }
    }

    [Fact]
    public void TheServerListensOnLoopbackOnly()
    {
        var program = Path.Combine(SourceRoot(), "Aurora.Server", "Program.cs");
        var text = File.ReadAllText(program);

        // Kestrel binds to loopback and the guard refuses a Host header naming anything else, so a
        // proxy in front of Aurora does not turn it into a networked service by accident.
        Assert.Contains("ListenLocalhost", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ListenAnyIP", text, StringComparison.Ordinal);
        Assert.Contains("LoopbackGuardMiddleware", text, StringComparison.Ordinal);
    }
}
