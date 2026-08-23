using System.Text;
using Aurora.Core.Abstractions;
using Aurora.Core.Files;

namespace Aurora.Adapters.Files;

/// <summary>
/// Reads a UTF-8 text file confined to the sandbox root (docs/adr/0010).
/// </summary>
/// <remarks>
/// Applies the same two path defences as the writer: lexical validation, then a refusal to follow
/// any symlink between the root and the target. A read is not harmless — following a planted link
/// would exfiltrate a file from outside the sandbox just as effectively as a write would corrupt
/// one.
/// </remarks>
public sealed class SandboxFileReader : ISandboxFileReader
{
    /// <summary>Refuses anything larger, so a read cannot be used to pull an unbounded file into a response.</summary>
    private const long MaxBytes = 64 * 1024;

    private readonly string _root;

    public SandboxFileReader(string sandboxRoot) => _root = SandboxGuard.ResolveRoot(sandboxRoot);

    public async Task<SandboxReadResult> ReadAsync(string relativePath, CancellationToken ct)
    {
        SandboxPathResult validated = SandboxPathValidator.Validate(_root, relativePath);
        if (!validated.IsValid)
        {
            throw new SandboxViolationException(validated.Reason!);
        }

        var full = validated.FullPath!;
        SandboxGuard.EnsureNoLinkedComponents(_root, full);

        if (!File.Exists(full))
        {
            throw new SandboxViolationException("File not found in the sandbox.");
        }

        var length = new FileInfo(full).Length;
        if (length > MaxBytes)
        {
            throw new SandboxViolationException($"File exceeds {MaxBytes} bytes.");
        }

        var content = await File.ReadAllTextAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);

        return new SandboxReadResult(Path.GetRelativePath(_root, full), content, length);
    }
}
