using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The voice plugin's own rules (docs/adr/0073).
/// </summary>
/// <remarks>
/// The plugin is Python — it holds the one connection Aurora's own process may not, the request
/// that turns a sentence into audio, which is why it is a plugin rather than part of Aurora — so
/// its tests are Python, run from here so the suite is one place to look.
/// <para>
/// <b>Status.</b> IMPLEMENTED and TESTED against fakes. <b>Not VERIFIED</b>: no conversation has
/// been held with the real ElevenLabs or a real whisper model on this machine. See
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

    private static void RunPython(string module, int expected) =>
        _ = PythonSuite.Run(PluginSource(), module, expected);

    [Fact]
    public void TheLocalStackHoldsItsRules()
    {
        // Turn detection, the loop, and what none of it may do, with the engines faked. Whether
        // the path exists at all is LocalVoiceTests, through the real host and Kernel.
        //
        // Fewer than before, and the drop is the point: the OpenAI Realtime transport and the
        // telephone provider were removed, and with them fifty-four tests that covered neither
        // hearing nor speaking. What is left is one recogniser and one speaker.
        RunPython("test_local", 54);
    }

    [Fact]
    public void TheCapabilityLayerHandsOverWhatAuroraAsksForAndNothingElse()
    {
        // The six functions Aurora calls, with the conversation faked — the mirror image of
        // test_local, which fakes the engines and never goes through a capability.
        //
        // These were written after two defects got through the gap between the two. Removing the
        // telephone left `session.transport` in voice.poll and `interaction.FAILED` in
        // voice.tool_result; the first raised on every poll, after the event queue had been
        // drained, so a working conversation looked exactly like silence.
        RunPython("test_service", 25);
    }

    [Fact]
    public void TheVoicePluginOpensTheConnectionsSoAuroraDoesNot()
    {
        var source = File.ReadAllText(Path.Combine(PluginSource(), "speech.py"));

        // The reason voice is a plugin at all. Aurora's own process opens no sockets — LocalOnly
        // fails the build over it — so whatever holds a connection to a speech service has to be
        // somewhere else. It used to be provider.py, holding a websocket to OpenAI and an HTTPS
        // client to a telephone company; both are gone, and the one remaining connection is the
        // request that turns a sentence into audio.
        Assert.Contains("urllib.request", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Aurora.Core", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeakingIsTheOnlyThingThatLeavesTheMachine()
    {
        var source = File.ReadAllText(Path.Combine(PluginSource(), "speech.py"));

        // Recognition is local and must stay local: a recording is somebody's voice, and sending
        // it would take a private conversation off the owner's machine without anybody deciding
        // to. Synthesis is not local, deliberately, and the file says so rather than leaving a
        // reader to infer it from an import.
        Assert.Contains("leaves the machine", source, StringComparison.Ordinal);

        // One host, named once. A second would mean a second thing to approve and a second place
        // for audio to go.
        Assert.Contains("api.elevenlabs.io", source, StringComparison.Ordinal);
        Assert.DoesNotContain("api.openai.com", source, StringComparison.Ordinal);
        Assert.DoesNotContain("api.twilio.com", source, StringComparison.Ordinal);
    }
}
