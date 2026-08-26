using Aurora.Core.Abstractions;
using Aurora.Core.Files;

namespace Aurora.Adapters.Files;

/// <summary>
/// Moves a file within the sandbox root (docs/adr/0060).
/// </summary>
/// <remarks>
/// The same three defences as the writer, applied to both ends: lexical validation, no linked
/// component between the root and either path, and containment re-verified after the move. A move
/// is a read and a write, so a source that escapes the root is exactly as bad as a destination
/// that does.
/// <para>
/// Never overwrites. A destination that already exists is refused rather than replaced, because
/// the caller asked to move a file and not to lose one.
/// </para>
/// </remarks>
public sealed class SandboxFileMover : ISandboxFileMover
{
    private readonly string _root;

    public SandboxFileMover(string sandboxRoot) => _root = SandboxGuard.ResolveRoot(sandboxRoot);

    public Task<SandboxMoveResult> MoveAsync(string from, string to, CancellationToken ct)
    {
        var source = Resolve(from);
        var destination = Resolve(to);

        if (!File.Exists(source))
        {
            throw new SandboxViolationException("File not found in the sandbox.");
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new SandboxViolationException("Something is already at the destination.");
        }

        var parent = Path.GetDirectoryName(destination)
            ?? throw new SandboxViolationException("The destination has no directory.");

        Directory.CreateDirectory(parent);

        // Re-checked after creating the directory: the component that did not exist a moment ago
        // is the one an attacker would have wanted to create as a link.
        SandboxGuard.EnsureNoLinkedComponents(_root, destination);
        SandboxGuard.EnsureResolvesInsideRoot(_root, destination);

        File.Move(source, destination, overwrite: false);

        try
        {
            SandboxGuard.EnsureResolvesInsideRoot(_root, destination);
        }
        catch (SandboxViolationException)
        {
            // It landed outside. Put it back rather than leave it there, and say so.
            File.Move(destination, source, overwrite: false);
            throw;
        }

        return Task.FromResult(new SandboxMoveResult(
            Path.GetRelativePath(_root, source).Replace(Path.DirectorySeparatorChar, '/'),
            Path.GetRelativePath(_root, destination).Replace(Path.DirectorySeparatorChar, '/')));
    }

    private string Resolve(string relativePath)
    {
        SandboxPathResult validated = SandboxPathValidator.Validate(_root, relativePath);

        if (!validated.IsValid)
        {
            throw new SandboxViolationException(validated.Reason!);
        }

        SandboxGuard.EnsureNoLinkedComponents(_root, validated.FullPath!);
        return validated.FullPath!;
    }
}
