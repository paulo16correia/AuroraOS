using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Files;

/// <summary>
/// Filesystem-level sandbox checks shared by the reader and the writer (docs/adr/0003, 0010).
/// </summary>
internal static class SandboxGuard
{
    /// <summary>Resolves the sandbox root through its own links, creating it when absent.</summary>
    internal static string ResolveRoot(string sandboxRoot)
    {
        Directory.CreateDirectory(sandboxRoot);
        var resolved = Directory.ResolveLinkTarget(sandboxRoot, returnFinalTarget: true);
        return Path.GetFullPath(resolved?.FullName ?? sandboxRoot);
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
