namespace Aurora.Core.Abstractions;

/// <summary>
/// Raised when a write is refused by the sandbox boundary. The kernel reports this to the caller
/// as a generic execution failure: the reason is deliberately not echoed back, so a caller cannot
/// probe the sandbox layout one rejected path at a time.
/// </summary>
public sealed class SandboxViolationException : Exception
{
    public SandboxViolationException(string message) : base(message)
    {
    }
}

/// <summary>Result of a successful sandboxed write.</summary>
public sealed record SandboxWriteResult(string Path, long Bytes, bool Overwritten);

/// <summary>Content of a file read from inside the sandbox.</summary>
public sealed record SandboxReadResult(string Path, string Content, long Bytes);

/// <summary>Reads a file from inside the sandbox root, refusing to follow links out of it.</summary>
public interface ISandboxFileReader
{
    Task<SandboxReadResult> ReadAsync(string relativePath, CancellationToken ct);
}

/// <summary>Writes a file inside a fixed sandbox root, atomically and without following links out.</summary>
public interface ISandboxFileWriter
{
    Task<SandboxWriteResult> WriteAsync(string relativePath, string content, CancellationToken ct);
}

/// <summary>One file the sandbox holds, as the index sees it.</summary>
public sealed record SandboxEntry(string Path, long Bytes, string ModifiedAtUtc);

/// <summary>
/// Lists what is in the sandbox, without following anything out of it.
/// </summary>
/// <remarks>
/// A listing is disclosure. A symlinked directory inside the root would otherwise enumerate
/// somewhere else entirely, and the caller would have no way to tell the difference — so the index
/// refuses to descend into one rather than reporting its contents as if they were Aurora's.
/// </remarks>
public interface ISandboxFileIndex
{
    Task<IReadOnlyList<SandboxEntry>> ListAsync(CancellationToken ct);
}

/// <summary>Where a file went.</summary>
public sealed record SandboxMoveResult(string From, string To);

/// <summary>
/// Moves a file within the sandbox root, refusing to overwrite and refusing to leave.
/// </summary>
/// <remarks>
/// Both ends are validated, not just the destination. A move is a read and a write, and a source
/// path that escapes the root would pull a file in from outside just as surely as a destination
/// that escapes would push one out.
/// </remarks>
public interface ISandboxFileMover
{
    Task<SandboxMoveResult> MoveAsync(string from, string to, CancellationToken ct);
}
