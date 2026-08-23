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

/// <summary>Writes a file inside a fixed sandbox root, atomically and without following links out.</summary>
public interface ISandboxFileWriter
{
    Task<SandboxWriteResult> WriteAsync(string relativePath, string content, CancellationToken ct);
}
