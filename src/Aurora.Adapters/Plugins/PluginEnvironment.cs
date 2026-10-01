namespace Aurora.Adapters.Plugins;

/// <summary>
/// The entire environment a plugin process is given.
/// </summary>
/// <remarks>
/// <b>Built, never inherited.</b> Aurora's own environment holds whatever the owner's shell held
/// when they started it, and a connection string or a key path sitting there is exactly the sort of
/// thing that travels into a child without anybody deciding to pass it. So a plugin gets this and
/// nothing else, and every secret it needs arrives over the pipe instead (docs/adr/0067).
/// <para>
/// <b>Why anything is here at all.</b> An empty environment is not neutral — it is a broken one. A
/// script beginning <c>#!/usr/bin/env python3</c> cannot find an interpreter without a
/// <c>PATH</c> (docs/adr/0062), and on Windows a process without <c>SystemRoot</c> cannot
/// initialise Winsock: every socket fails with <c>WinError 10106</c>, "could not load or
/// initialise the requested service provider", which reaches Aurora as a plugin that will not talk
/// to anything and looks nothing like a missing environment variable (docs/adr/0075).
/// </para>
/// <para>
/// Everything here is a constant naming where the operating system is installed. None of it varies
/// with who is running Aurora or what they had exported, which is the property that matters: this
/// carries nothing of Aurora's or the owner's into third-party code.
/// </para>
/// </remarks>
internal static class PluginEnvironment
{
    /// <summary>
    /// The child's whole environment: the platform floor, plus what Aurora names for this launch.
    /// </summary>
    /// <param name="workingDirectory">
    /// The one directory this plugin may write to, which on Windows is also where its container's
    /// own private storage is put. See <c>LOCALAPPDATA</c> below.
    /// </param>
    /// <param name="aurora">What Aurora names for this particular launch.</param>
    internal static Dictionary<string, string> For(
        string workingDirectory, params (string Name, string Value)[] aurora)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // System directories only. Still a fixed list rather than the owner's own PATH, which
            // can name a directory anybody can write to — and on Unix it also names where package
            // managers put things, so a plugin needing a local program (speech recognition, a
            // codec tool) finds it here or not at all. "Not at all" then reads as the feature
            // being unavailable rather than as a path being short.
            ["PATH"] = OperatingSystem.IsWindows()
                ? Path.Combine(Windows, "System32")
                : "/usr/bin:/bin:/usr/local/bin:/opt/homebrew/bin",
        };

        if (OperatingSystem.IsWindows())
        {
            // Winsock's service-provider catalogue is loaded through this. Without it a plugin
            // cannot open a socket at all — not to Discord, not to Microsoft, not to a stand-in
            // on loopback — and the failure surfaces as an OSError from inside the plugin.
            //
            // Read from the operating system rather than copied from Aurora's own environment, so
            // that a variable somebody set in the shell that started Aurora cannot redirect where
            // a plugin looks for Windows.
            environment["SystemRoot"] = Windows;

            // Required to create the process at all when it is confined. CreateProcess composes
            // an AppContainer's own private folder from this, and with the variable absent it
            // refuses the whole creation with ERROR_ENVVAR_NOT_FOUND — before the process exists,
            // so it reads as "the confined process could not be created" and nothing says why.
            // That is why AppContainer confinement had never once started a plugin (docs/adr/0078).
            //
            // The plugin's own working directory, not the owner's AppData. Windows only needs the
            // variable to be present — it does not check that it leads anywhere — so this is the
            // value that tells a plugin nothing about who is running Aurora and puts whatever the
            // container does store inside the one directory it was already allowed to write to.
            environment["LOCALAPPDATA"] = Path.GetFullPath(workingDirectory);

            // A writable temporary directory, inside the one place the plugin may write. A
            // confined plugin cannot write the owner's %TEMP% — it is not among its grants — so a
            // plugin that shells out to a helper which writes a temp file (a speech engine writing
            // a wav, say) fails on a path it never chose. Pointing TEMP and TMP at the working
            // directory removes the guess: tempfile lands where the sandbox already allows it.
            environment["TEMP"] = Path.GetFullPath(workingDirectory);
            environment["TMP"] = Path.GetFullPath(workingDirectory);
        }

        foreach ((var name, var value) in aurora)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>Where Windows is installed, from Windows.</summary>
    private static string Windows =>
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);
}
