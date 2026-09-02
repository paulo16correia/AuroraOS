using System.Diagnostics;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The voice plugin's own rules (docs/adr/0073).
/// </summary>
/// <remarks>
/// The plugin is Python — it holds the connections to the telephone provider and to the interaction
/// layer, which is why it is a plugin rather than part of Aurora — so its tests are Python, run
/// from here so the suite is one place to look.
/// <para>
/// <b>Status.</b> IMPLEMENTED and TESTED against fakes. <b>Not VERIFIED</b>: no call has been
/// placed or answered, no OpenAI Realtime session has been opened, and no +351 number exists. See
/// <c>docs/reference/platform-support.md</c>.
/// </para>
/// </remarks>
public sealed class VoicePluginTests
{
    private static string PluginSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "plugins")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "plugins", "voice");
    }

    private static void RunPython(string module, int expected)
    {
        using var python = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "python3",
                WorkingDirectory = PluginSource(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        python.StartInfo.ArgumentList.Add("-m");
        python.StartInfo.ArgumentList.Add("unittest");
        python.StartInfo.ArgumentList.Add(module);
        python.StartInfo.ArgumentList.Add("-v");

        python.Start();
        var output = python.StandardOutput.ReadToEnd() + python.StandardError.ReadToEnd();
        python.WaitForExit(180_000);

        Assert.True(python.ExitCode == 0, output);
        Assert.Contains($"Ran {expected} test", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealRealtimeTransportHoldsItsRules()
    {
        // Driven against a Realtime service on loopback, so the class that ships performs a real
        // RFC 6455 handshake and carries real masked frames. The client stand-in used by the
        // runtime tests replaces the transport, which leaves everything inside it untested.
        RunPython("test_realtime", 20);
    }

    [Fact]
    public void TheProviderBoundaryAndTheInteractionLayerHoldTheirRules()
    {
        RunPython("test_voice_plugin", 34);
    }

    [Fact]
    public void TheVoicePluginOpensTheConnectionsSoAuroraDoesNot()
    {
        var source = File.ReadAllText(Path.Combine(PluginSource(), "provider.py"));

        // The reason voice is a plugin at all. Aurora's own process opens no sockets — LocalOnly
        // fails the build over it — so the thing holding a websocket to a speech provider and an
        // HTTPS client to a telephone company has to be somewhere else.
        Assert.Contains("hmac", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Aurora.Core", source, StringComparison.Ordinal);
    }
}
