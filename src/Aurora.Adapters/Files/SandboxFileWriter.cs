using System.Text;
using Aurora.Core.Abstractions;
using Aurora.Core.Files;

namespace Aurora.Adapters.Files;

/// <summary>
/// Atomic, link-aware file writer confined to a sandbox root (docs/adr/0003).
/// </summary>
/// <remarks>
/// Three defences, in order: <see cref="SandboxPathValidator"/> rejects the path lexically; every
/// component between the root and the target is checked for being a link, so a symlink planted
/// mid-path cannot redirect the write outside; and the content lands via a temp file plus rename,
/// so a reader never observes a partially written file.
/// <para>
/// Residual race, stated plainly: .NET has no portable <c>openat</c>/<c>O_NOFOLLOW</c>, so the
/// link check and the write are separate syscalls. An attacker who can create files inside the
/// sandbox root between those two steps can still win a TOCTOU race. Closing it properly needs
/// platform interop and is deferred; the mitigation today is that the sandbox root is expected to
/// be writable only by the Aurora process's own user.
/// </para>
/// </remarks>
public sealed class SandboxFileWriter : ISandboxFileWriter
{
    private readonly string _root;

    public SandboxFileWriter(string sandboxRoot)
    {
        Directory.CreateDirectory(sandboxRoot);

        // Resolve the root through any links once, so containment is judged against the real path.
        var resolved = Directory.ResolveLinkTarget(sandboxRoot, returnFinalTarget: true);
        _root = Path.GetFullPath(resolved?.FullName ?? sandboxRoot);
    }

    public async Task<SandboxWriteResult> WriteAsync(string relativePath, string content, CancellationToken ct)
    {
        SandboxPathResult validated = SandboxPathValidator.Validate(_root, relativePath);
        if (!validated.IsValid)
        {
            throw new SandboxViolationException(validated.Reason!);
        }

        var full = validated.FullPath!;
        var directory = Path.GetDirectoryName(full)
            ?? throw new SandboxViolationException("Path has no parent directory.");

        // Check what already exists before creating anything, so we never mkdir through a link...
        EnsureNoLinkedComponents(full);
        Directory.CreateDirectory(directory);
        // ...and again afterwards, now that every component exists.
        EnsureNoLinkedComponents(full);

        var overwritten = File.Exists(full);
        var bytes = Encoding.UTF8.GetBytes(content);
        var temp = Path.Combine(directory, $".aurora-{Guid.NewGuid():n}.tmp");

        try
        {
            var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return new SandboxWriteResult(Path.GetRelativePath(_root, full), bytes.LongLength, overwritten);
    }

    /// <summary>
    /// Walks root → target and refuses if any existing component is a symlink or reparse point.
    /// The target itself is included: overwriting a link would write through it.
    /// </summary>
    private void EnsureNoLinkedComponents(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath);
        var current = _root;

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);

            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;

            if (info?.LinkTarget is not null)
            {
                throw new SandboxViolationException($"'{segment}' is a link; refusing to write through it.");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is preferable to masking the original failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
