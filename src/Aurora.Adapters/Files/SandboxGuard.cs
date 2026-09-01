using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Files;

/// <summary>
/// Filesystem-level sandbox checks shared by the reader and the writer (docs/adr/0003, 0010).
/// </summary>
public static class SandboxGuard
{
    /// <summary>Resolves the sandbox root through its own links, creating it when absent.</summary>
    public static string ResolveRoot(string sandboxRoot)
    {
        Directory.CreateDirectory(sandboxRoot);
        RestrictToOwner(sandboxRoot);

        var resolved = Directory.ResolveLinkTarget(sandboxRoot, returnFinalTarget: true);
        return Path.GetFullPath(resolved?.FullName ?? sandboxRoot);
    }

    /// <summary>
    /// Makes a sandbox directory readable and writable by its owner alone.
    /// </summary>
    /// <remarks>
    /// ADR 0003 named the residual TOCTOU risk and said the mitigation was operational: the root
    /// "should be writable only by the Aurora process's own user". Should is not a control. This
    /// applies it, so the assumption the hardening rests on is something Aurora does rather than
    /// something it hopes an operator did.
    /// <para>
    /// This was once a no-op on Windows, on the argument that a per-user application data directory
    /// already carries the right inherited ACL. That is true of the default root and says nothing
    /// about a configured one, which is the same "should" the ADR refused to rely on —
    /// <see cref="OwnerOnly"/> now applies it there too.
    /// </para>
    /// </remarks>
    public static void RestrictToOwner(string directory)
    {
        // A root Aurora cannot re-permission is one somebody else controls. Better to keep working
        // with the lexical and link checks than to refuse to start over a mode bit, so the answer
        // is reported rather than thrown — and `aurora doctor` is where it is read out loud.
        OwnerOnly.Directory(directory);
    }

    /// <summary>
    /// Confirms a path that now exists really resolves inside the root.
    /// </summary>
    /// <remarks>
    /// The last line of defence, and the point of it is honesty about the race. .NET has no
    /// portable <c>openat</c>/<c>O_NOFOLLOW</c>, so a directory component swapped between the check
    /// and the write cannot be prevented outright — but it can be <i>detected</i>, and a write that
    /// landed outside the sandbox can be undone and reported instead of quietly standing.
    /// </remarks>
    internal static void EnsureResolvesInsideRoot(string root, string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null)
        {
            throw new SandboxViolationException("Path has no parent directory.");
        }

        FileSystemInfo? real = Directory.ResolveLinkTarget(directory, returnFinalTarget: true);
        var resolved = Path.GetFullPath(real?.FullName ?? directory);

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!string.Equals(resolved, root, StringComparison.Ordinal)
            && !resolved.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new SandboxViolationException("The path resolved outside the sandbox.");
        }
    }

    /// <summary>
    /// Walks root → target and refuses if any existing component is a symlink or reparse point.
    /// The target itself is included: following a link there would step outside the sandbox, for a
    /// read just as much as for a write.
    /// </summary>
    internal static void EnsureNoLinkedComponents(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);

            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;

            if (info?.LinkTarget is not null)
            {
                throw new SandboxViolationException($"'{segment}' is a link; refusing to follow it.");
            }
        }
    }
}
