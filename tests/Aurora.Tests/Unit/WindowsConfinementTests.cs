using Aurora.Adapters.Plugins;
using Aurora.Adapters.Plugins.Sandboxes.Windows;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Guards for the three Windows-confinement defects that only a real kernel could surface
/// (docs/adr/0078), locked in as cheap unit checks beside the live verification.
/// </summary>
/// <remarks>
/// Each of these was a single wrong value that let the whole confinement path build, pass every
/// test that ran off-Windows, and then fail — or silently misbehave — the first time a plugin was
/// started inside a real AppContainer. None is caught by asserting the shape of a profile; they are
/// caught by pinning the exact values a Windows kernel rejected.
/// </remarks>
public sealed class WindowsConfinementTests
{
    // ---- the environment CreateProcess needs to make a confined process at all ----

    [Fact]
    public void ThePluginEnvironmentCarriesWhatAConfinedCreateProcessRequires()
    {
        var working = Path.Combine(Path.GetTempPath(), "aurora-env-probe");
        Dictionary<string, string> environment = PluginEnvironment.For(working, ("AURORA_MODE", "service"));

        // Always: a PATH, and the Aurora variables the caller named.
        Assert.True(environment.ContainsKey("PATH"));
        Assert.Equal("service", environment["AURORA_MODE"]);

        if (OperatingSystem.IsWindows())
        {
            // SystemRoot, without which Winsock cannot initialise and no plugin opens a socket.
            Assert.True(environment.ContainsKey("SystemRoot"));

            // LOCALAPPDATA, without which CreateProcess into an AppContainer fails outright with
            // ERROR_ENVVAR_NOT_FOUND before the process exists. Set to the plugin's own working
            // directory rather than the owner's AppData, so it discloses nothing and points the
            // container's private storage at a directory it already owns.
            Assert.True(
                environment.ContainsKey("LOCALAPPDATA"),
                "LOCALAPPDATA is required or a confined CreateProcess fails (docs/adr/0078)");
            Assert.Equal(Path.GetFullPath(working), environment["LOCALAPPDATA"]);

            // A writable temp inside the plugin's own directory. A confined plugin cannot write
            // the owner's %TEMP%, so a helper it runs that writes a temp file would fail on a path
            // it never chose.
            Assert.Equal(Path.GetFullPath(working), environment["TEMP"]);
            Assert.Equal(Path.GetFullPath(working), environment["TMP"]);
        }
    }

    [Fact]
    public void ThePluginEnvironmentNeverCarriesTheOwnersOwnSecretsByAccident()
    {
        // Built from constants and the caller's own values only. A variable the owner happened to
        // export must not travel — the environment is the classic quiet leak.
        Environment.SetEnvironmentVariable("AURORA_TEST_LEAK_CANARY", "must-not-travel");

        try
        {
            Dictionary<string, string> environment = PluginEnvironment.For(
                Path.GetTempPath(), ("AURORA_PLUGIN_ID", "plugin/probe"));

            Assert.DoesNotContain("AURORA_TEST_LEAK_CANARY", environment.Keys);
            Assert.DoesNotContain(
                environment.Values, v => v.Contains("must-not-travel", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AURORA_TEST_LEAK_CANARY", null);
        }
    }

    // ---- the two interop constants a Windows kernel judged ----

    [Fact]
    public void TheInternetClientCapabilityIsTheDocumentedSid()
    {
        // Once built from WELL_KNOWN_SID_TYPE 116, which on a real Windows is a mandatory-label
        // SID, not internetClient — so CreateProcess rejected the capability and no networked
        // plugin could start confined. The capability SIDs are fixed; internetClient is S-1-15-3-1.
        if (!OperatingSystem.IsWindows())
        {
            return;   // Win32 is [SupportedOSPlatform("windows")]; the constant lives only there.
        }

        Assert.Equal("S-1-15-3-1", Win32.InternetClientCapabilitySid);
    }

    [Fact]
    public void TheAlreadyExistsCodeIsTheOneCreateAppContainerProfileReturns()
    {
        // Once 0x800700B5 (ERROR_ALIAS_EXISTS), returned by nothing here, so the branch that
        // reuses an existing container never ran and every start after a plugin's first failed.
        // The value a re-created profile actually returns is HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS).
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(unchecked((int)0x800700B7), Win32.ErrorAlreadyExists);
    }
}
