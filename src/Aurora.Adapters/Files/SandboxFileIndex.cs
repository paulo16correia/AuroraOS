using System.Globalization;
using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Files;

/// <summary>
/// Lists the files under the sandbox root (docs/adr/0060).
/// </summary>
/// <remarks>
/// Walks the tree itself rather than calling <c>EnumerateFiles</c> with
/// <c>SearchOption.AllDirectories</c>, because that follows directory symlinks and there is no
/// option to stop it. A planted link inside the root would otherwise enumerate the owner's whole
/// home and report the results as the sandbox's contents.
/// </remarks>
public sealed class SandboxFileIndex : ISandboxFileIndex
{
    /// <summary>
    /// How many files a listing may report.
    /// </summary>
    /// <remarks>
    /// A bound rather than a page: every caller of this wants the whole sandbox, and a sandbox
    /// with more than this in it is a situation to look at rather than to paginate through.
    /// </remarks>
    private const int MaxEntries = 2_000;

    /// <summary>How deep the walk goes before it stops trusting the shape of the tree.</summary>
    private const int MaxDepth = 16;

    private readonly string _root;

    public SandboxFileIndex(string sandboxRoot) => _root = SandboxGuard.ResolveRoot(sandboxRoot);

    public Task<IReadOnlyList<SandboxEntry>> ListAsync(CancellationToken ct)
    {
        var entries = new List<SandboxEntry>();
        Walk(_root, depth: 0, entries, ct);

        return Task.FromResult<IReadOnlyList<SandboxEntry>>(
            entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList());
    }

    private void Walk(string directory, int depth, List<SandboxEntry> entries, CancellationToken ct)
    {
        if (depth > MaxDepth || entries.Count >= MaxEntries)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (entries.Count >= MaxEntries)
            {
                return;
            }

            // A link that points outside is not this sandbox's file, whatever its name suggests.
            if (File.ResolveLinkTarget(file, returnFinalTarget: false) is not null)
            {
                continue;
            }

            var info = new FileInfo(file);

            entries.Add(new SandboxEntry(
                Relative(file),
                info.Length,
                info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)));
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            // Same reason, one level up: descending into a linked directory would enumerate
            // somewhere else and report it as ours.
            if (Directory.ResolveLinkTarget(child, returnFinalTarget: false) is null)
            {
                Walk(child, depth + 1, entries, ct);
            }
        }
    }

    /// <summary>Always forward slashes, so a rule written once matches on every platform.</summary>
    private string Relative(string full) =>
        Path.GetRelativePath(_root, full).Replace(Path.DirectorySeparatorChar, '/');
}
