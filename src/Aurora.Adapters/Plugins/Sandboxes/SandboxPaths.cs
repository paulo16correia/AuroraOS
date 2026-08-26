namespace Aurora.Adapters.Plugins.Sandboxes;

/// <summary>
/// Resolves a path the way the kernel does, following every symbolic link on the way down.
/// </summary>
/// <remarks>
/// A sandbox policy is matched against the path the kernel arrives at, not the one the caller
/// wrote. On macOS the system temporary directory is reached through <c>/var</c>, which is a link
/// to <c>/private/var</c> — so a rule naming <c>/var/folders/…</c> matches nothing, and a plugin
/// that should have been allowed to write to its own directory is denied instead.
/// <para>
/// This was found by a test: the write test failed while the identical commands passed by hand,
/// and the only difference was which of the two names for the same directory was used.
/// </para>
/// </remarks>
internal static class SandboxPaths
{
    /// <summary>How many links to follow before deciding a path points at itself.</summary>
    private const int MaxHops = 40;

    /// <summary>
    /// The real path, or the fully-qualified one if any part of it cannot be resolved. Never
    /// throws: a path that cannot be resolved still has to produce a policy.
    /// </summary>
    internal static string Real(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);

        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        var current = root;

        foreach (var part in full[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);

            for (var hop = 0; hop < MaxHops; hop++)
            {
                string? target = Resolve(current);

                if (target is null || target == current)
                {
                    break;
                }

                current = target;
            }
        }

        return current;
    }

    private static string? Resolve(string path)
    {
        try
        {
            FileSystemInfo? target = Directory.Exists(path)
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: false)
                : File.ResolveLinkTarget(path, returnFinalTarget: false);

            return target?.FullName;
        }
        catch (Exception unresolvable)
            when (unresolvable is IOException or UnauthorizedAccessException)
        {
            // The path does not exist yet, or is not ours to look at. Either way the caller's
            // own name for it is the best answer available.
            return null;
        }
    }
}
