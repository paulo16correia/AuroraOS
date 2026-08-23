namespace Aurora.Core.Files;

/// <summary>Outcome of validating a caller-supplied relative path against a sandbox root.</summary>
public sealed record SandboxPathResult(bool IsValid, string? FullPath, string? Reason)
{
    public static SandboxPathResult Rejected(string reason) => new(false, null, reason);

    public static SandboxPathResult Accepted(string fullPath) => new(true, fullPath, null);
}

/// <summary>
/// Pure, I/O-free validation of a caller-supplied relative path against a sandbox root.
/// Rejects by construction rather than by sanitising: a path that is not obviously safe is
/// refused, never rewritten into something "close enough".
/// </summary>
/// <remarks>
/// Windows-specific hazards (UNC, device namespaces, reserved device names, alternate data
/// streams, trailing dot/space) are rejected on every platform on purpose. A sandbox written on
/// macOS may later be opened on Windows, and a name that is inert here can resolve to a device
/// there; keeping one rule set also means the tests cover the same behaviour everywhere.
/// This type performs no I/O, so it cannot see symlinks — that check belongs to the writer.
/// </remarks>
public static class SandboxPathValidator
{
    /// <summary>Maximum length of the caller-supplied relative path.</summary>
    public const int MaxRelativePathChars = 512;

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    public static SandboxPathResult Validate(string sandboxRootFullPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return SandboxPathResult.Rejected("Path must not be empty.");
        }

        if (relativePath.Length > MaxRelativePathChars)
        {
            return SandboxPathResult.Rejected($"Path exceeds {MaxRelativePathChars} characters.");
        }

        foreach (char c in relativePath)
        {
            if (char.IsControl(c))
            {
                return SandboxPathResult.Rejected("Path must not contain control characters.");
            }
        }

        // Alternate data streams and Windows drive-relative paths both hinge on ':'.
        if (relativePath.Contains(':', StringComparison.Ordinal))
        {
            return SandboxPathResult.Rejected("Path must not contain ':'.");
        }

        // Absolute, UNC and device paths never reach the combine step.
        if (Path.IsPathRooted(relativePath))
        {
            return SandboxPathResult.Rejected("Path must be relative.");
        }

        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            return SandboxPathResult.Rejected("Path must not start with a separator.");
        }

        if (relativePath.StartsWith("//", StringComparison.Ordinal)
            || relativePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return SandboxPathResult.Rejected("UNC and device paths are not permitted.");
        }

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return SandboxPathResult.Rejected("Path must not contain empty segments.");
            }

            if (segment == ".")
            {
                return SandboxPathResult.Rejected("Path must not contain '.' segments.");
            }

            if (segment == "..")
            {
                return SandboxPathResult.Rejected("Path must not traverse upwards.");
            }

            // Windows silently strips these, so "a " and "a" would collide.
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                return SandboxPathResult.Rejected("Path segments must not end with a space or dot.");
            }

            var stem = segment.Split('.', 2)[0];
            foreach (var reserved in ReservedDeviceNames)
            {
                if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
                {
                    return SandboxPathResult.Rejected($"'{segment}' is a reserved device name.");
                }
            }
        }

        var root = Path.GetFullPath(sandboxRootFullPath);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var full = Path.GetFullPath(Path.Combine(root, relativePath));

        // Ordinal on purpose: `full` is built from `root`, so a genuine child always shares the
        // exact prefix. A case-insensitive compare would widen what counts as "inside".
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            return SandboxPathResult.Rejected("Path escapes the sandbox root.");
        }

        return SandboxPathResult.Accepted(full);
    }
}
