using System.Collections.Concurrent;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Plugins;

/// <summary>What running this plugin's program needs, or why it cannot be run at all.</summary>
/// <param name="Interpreter">
/// The interpreter to run it with, or <see langword="null"/> when the platform runs the program
/// itself — which is the answer on macOS and Linux, always.
/// </param>
/// <param name="Refused">
/// Why no launch is possible, in words that name what to fix. Set only when
/// <paramref name="Interpreter"/> is null <i>and</i> one was needed.
/// </param>
public sealed record InterpreterResolution(PluginInterpreter? Interpreter, string? Refused = null)
{
    /// <summary>Whether a launch may proceed.</summary>
    public bool Ok => Refused is null;
}

/// <summary>
/// Finds the interpreter a script plugin needs, on the one platform that will not find it itself.
/// </summary>
/// <remarks>
/// <b>The bug.</b> A plugin whose program is <c>voice_service.py</c> starts on macOS and Linux
/// because the kernel reads <c>#!/usr/bin/env python3</c> and runs Python for it. Windows has no
/// such thing: <c>CreateProcess</c> wants an executable image, so every service plugin Aurora ships
/// failed with <c>SERVICE_UNAVAILABLE: could not start: Win32Exception</c> — Voice, Microsoft and
/// Discord alike, because it was never their bug. It was this one (docs/adr/0075).
/// <para>
/// <b>What this does not do.</b> It does not run a shell. There is no <c>cmd.exe</c>, no
/// <c>UseShellExecute</c>, no <c>ShellExecute</c> verb and no <c>.bat</c> path: those all amount to
/// handing Windows a string and letting it decide what to execute, which is the generic command
/// execution Aurora does not have. It resolves one name from a closed list
/// (<see cref="PluginRuntimes"/>) to one absolute path, and that path is what gets executed.
/// </para>
/// <para>
/// <b>Constraints on the search.</b> The owner may name the interpreter outright, which settles it.
/// Otherwise the search is over <c>PATH</c> — but only for the fixed file names that runtime goes
/// by, and only accepting a real file: a zero-length reparse point is rejected, because that is
/// what Windows puts in <c>WindowsApps</c> as a Microsoft Store shortcut and it is not a program.
/// It cannot execute in an AppContainer and, launched from Aurora, would open the Store rather than
/// start a plugin.
/// </para>
/// <para>
/// <b>Fail closed.</b> Anything unresolved is a refusal that names what was looked for. Nothing
/// falls back to another program, and a plugin that cannot be started is reported as not started.
/// </para>
/// </remarks>
public sealed class PluginInterpreters
{
    /// <summary>
    /// Extensions Windows can start on its own, so no interpreter is wanted.
    /// </summary>
    /// <remarks>
    /// Only real executable images. <c>.bat</c> and <c>.cmd</c> are deliberately absent: Windows
    /// runs those through the command processor, which would be a shell in the launch path.
    /// </remarks>
    private static readonly string[] WindowsExecutables = [".exe", ".com"];

    /// <summary>Paths the owner named, by runtime. Believed over any search.</summary>
    private readonly IReadOnlyDictionary<string, string> _configured;

    /// <summary>
    /// What the search found, by runtime.
    /// </summary>
    /// <remarks>
    /// Once per process, on purpose and for the same reason the sandbox is chosen once at startup:
    /// a plugin's launch should not depend on whether somebody installed something between two
    /// calls. Restarting Aurora is what picks up a newly installed interpreter.
    /// </remarks>
    private readonly ConcurrentDictionary<string, string?> _found = new(StringComparer.Ordinal);

    private readonly bool _windows;

    /// <param name="configured">
    /// Absolute interpreter paths the owner set, keyed by runtime name — from
    /// <c>Aurora:Plugins:Interpreters:python3</c>. A name here is authoritative: if the file is not
    /// there, that is a refusal rather than a reason to go looking for a different Python.
    /// </param>
    /// <param name="windows">
    /// Whether this machine needs interpreters resolved at all. A parameter so the resolution can
    /// be tested on any machine; it is the running platform everywhere else.
    /// </param>
    public PluginInterpreters(
        IReadOnlyDictionary<string, string>? configured = null, bool? windows = null)
    {
        _configured = configured ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _windows = windows ?? OperatingSystem.IsWindows();
    }

    /// <summary>
    /// Whether this platform needs to be told what runs the program, rather than working it out.
    /// </summary>
    public bool Needed(string executable) =>
        _windows
        && !WindowsExecutables.Contains(
            Path.GetExtension(executable), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The interpreter for a plugin's program, or the reason it cannot be started.
    /// </summary>
    /// <remarks>
    /// Where nothing is needed the answer is an empty success, and the launch is exactly the one
    /// macOS and Linux have always performed.
    /// </remarks>
    public InterpreterResolution Resolve(PluginManifest manifest, string executable)
    {
        if (!Needed(executable))
        {
            return new InterpreterResolution(null);
        }

        // The manifest first: the author saying it outright beats anything inferred, and it is the
        // only answer available for a program that is a script without looking like one.
        var runtime = PluginRuntimes.Canonical(manifest.Interpreter)
            ?? PluginRuntimes.Infer(FirstLine(executable), executable);

        if (runtime is null)
        {
            return new InterpreterResolution(
                null,
                $"{Path.GetFileName(executable)} is not a program Windows can start, and nothing "
                + "says what should run it. Add \"interpreter\": \"python3\" to the plugin's "
                + "manifest, or begin the script with \"#!/usr/bin/env python3\".");
        }

        if (_configured.TryGetValue(runtime, out var named))
        {
            return File.Exists(named)
                ? new InterpreterResolution(new PluginInterpreter(runtime, Path.GetFullPath(named)))
                : new InterpreterResolution(
                    null,
                    $"Aurora:Plugins:Interpreters:{runtime} names '{named}', which is not there. "
                    + "Correct it or remove it; Aurora will not start a different interpreter than "
                    + "the one you named.");
        }

        if (_found.GetOrAdd(runtime, Search) is { } path)
        {
            return new InterpreterResolution(new PluginInterpreter(runtime, path));
        }

        return new InterpreterResolution(
            null,
            $"{Path.GetFileName(executable)} needs {runtime}, and no "
            + $"{string.Join(" or ", PluginRuntimes.FileNames(runtime, windows: true))} was found "
            + "on PATH. Install it, or set Aurora:Plugins:Interpreters:"
            + $"{runtime} to its full path.");
    }

    /// <summary>The first line of the program, for its shebang. Null if it has none to read.</summary>
    private static string? FirstLine(string executable)
    {
        try
        {
            using var reader = new StreamReader(executable);
            return reader.ReadLine();
        }
        catch (Exception unreadable)
            when (unreadable is IOException or UnauthorizedAccessException
                      or ArgumentException or NotSupportedException)
        {
            // A program Aurora cannot read is one it also cannot start, and the caller says so in
            // better words than an exception from here would.
            return null;
        }
    }

    /// <summary>The first usable interpreter of this runtime on PATH.</summary>
    private string? Search(string runtime)
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // File name outer, directory inner: python3 anywhere on the path beats python next door,
        // because a machine with both may still have Python 2 under the shorter name.
        foreach (var fileName in PluginRuntimes.FileNames(runtime, _windows))
        {
            foreach (var directory in directories)
            {
                string candidate;

                try
                {
                    candidate = Path.GetFullPath(Path.Combine(directory, fileName));
                }
                catch (ArgumentException)
                {
                    // A PATH entry with characters no path can hold. Somebody else's problem.
                    continue;
                }

                if (IsUsable(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Whether a candidate is a real program rather than a stand-in for one.</summary>
    private static bool IsUsable(string path)
    {
        try
        {
            var file = new FileInfo(path);

            // Length, because a Microsoft Store app-execution alias is a zero-length reparse point
            // that looks exactly like python.exe until it is started, at which point it opens the
            // Store instead. In an AppContainer it does not even do that.
            return file.Exists
                && file.Length > 0
                && !file.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception unreadable)
            when (unreadable is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
