namespace Aurora.Core.Contracts;

/// <summary>
/// The interpreters Aurora is willing to start a plugin with, by name.
/// </summary>
/// <remarks>
/// <b>Why a closed set.</b> A plugin whose program is a script needs something to run it, and on
/// Unix the kernel supplies that from the shebang. Windows has no equivalent: <c>CreateProcess</c>
/// wants a PE image, so a <c>.py</c> file fails with a <c>Win32Exception</c> and the plugin never
/// starts (docs/adr/0075). Aurora therefore has to name the interpreter itself — and the moment it
/// does, "which program does Aurora start" becomes a security question rather than a convenience.
/// <para>
/// The answer here is that a manifest, and a shebang, may only ever select a <i>name from this
/// list</i>. Neither can supply a path. <c>#!/bin/sh</c> is not a runtime Aurora knows, so it is
/// refused rather than turned into a shell — which is the same rule as everywhere else in Aurora:
/// there is no generic command execution, and this is not the place one gets in through.
/// </para>
/// <para>
/// The list is short because Aurora's own plugins are all Python. Adding to it is a decision
/// somebody makes deliberately, in a review, rather than a string appearing in a manifest.
/// </para>
/// </remarks>
public static class PluginRuntimes
{
    /// <summary>CPython 3. The only interpreter Aurora's own plugins use.</summary>
    public const string Python = "python3";

    /// <summary>Every runtime name a manifest may declare.</summary>
    public static IReadOnlyList<string> All { get; } = [Python];

    /// <summary>
    /// The canonical name for a spelling of a runtime, or <see langword="null"/> if it is not one.
    /// </summary>
    /// <remarks>
    /// <c>python</c>, <c>python3</c> and <c>python3.12</c> are the same runtime under three names,
    /// and a plugin author who wrote any of them meant the same thing. A minor version is accepted
    /// and then ignored: Aurora starts whichever Python this machine has, and a plugin that needs a
    /// specific one has to say so in its own first lines, where it can say why.
    /// </remarks>
    public static string? Canonical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();

        // A ".exe" from a manifest written on Windows names the same runtime.
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        if (trimmed.Equals("python", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("python3", StringComparison.OrdinalIgnoreCase)
            || IsPythonWithVersion(trimmed))
        {
            return Python;
        }

        return null;
    }

    /// <summary>Whether a manifest may declare this name.</summary>
    public static bool IsKnown(string? name) => Canonical(name) is not null;

    /// <summary>
    /// The runtime a plugin's own program says it needs, from its shebang or its extension.
    /// </summary>
    /// <remarks>
    /// Aurora is reading the declaration the author already wrote — the line the Unix kernel reads
    /// on the platforms where this problem does not exist. The shebang is preferred over the
    /// extension because it is the more specific statement, and because a file can be a script
    /// without being named like one.
    /// <para>
    /// Only the interpreter's <i>name</i> is taken from it. <c>#!/usr/bin/env python3</c>,
    /// <c>#!/usr/local/bin/python3.12</c> and <c>#!/opt/whatever/python</c> all resolve to the same
    /// runtime, resolved by <see cref="Canonical"/>, and the directories they name are discarded —
    /// so a manifest cannot smuggle a path to an arbitrary program through this.
    /// </para>
    /// </remarks>
    /// <param name="firstLine">The program's first line, or <see langword="null"/> if unreadable.</param>
    /// <param name="fileName">The program's file name, used when there is no usable shebang.</param>
    public static string? Infer(string? firstLine, string fileName)
    {
        if (FromShebang(firstLine) is { } declared)
        {
            return declared;
        }

        return Path.GetExtension(fileName).Equals(".py", StringComparison.OrdinalIgnoreCase)
            ? Python
            : null;
    }

    /// <summary>The runtime named by a <c>#!</c> line, if it names one Aurora knows.</summary>
    public static string? FromShebang(string? firstLine)
    {
        if (firstLine is null || !firstLine.StartsWith("#!", StringComparison.Ordinal))
        {
            return null;
        }

        // "#!/usr/bin/env python3 -u" — env is a way of spelling "whichever one is on the path",
        // so the name that matters is the word after it rather than "env" itself.
        var words = firstLine[2..]
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var word in words)
        {
            if (word.StartsWith('-'))
            {
                // A switch to the interpreter, not the interpreter.
                continue;
            }

            var name = word[(word.LastIndexOfAny(['/', '\\']) + 1)..];

            if (Canonical(name) is { } runtime)
            {
                return runtime;
            }

            if (!name.Equals("env", StringComparison.OrdinalIgnoreCase))
            {
                // The first real program named is not one Aurora knows, and looking further along
                // the line would be looking for an excuse to run something.
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The file names this runtime's interpreter goes by on this platform, most specific first.
    /// </summary>
    public static IReadOnlyList<string> FileNames(string runtime, bool windows)
    {
        if (!string.Equals(Canonical(runtime), Python, StringComparison.Ordinal))
        {
            return [];
        }

        // "python3" first everywhere: on a machine with both, "python" may still be Python 2.
        return windows ? ["python3.exe", "python.exe"] : ["python3", "python"];
    }

    private static bool IsPythonWithVersion(string name)
    {
        const string prefix = "python3.";

        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> minor = name.AsSpan(prefix.Length);

        return minor.Length > 0 && !minor.ContainsAnyExcept("0123456789");
    }
}
