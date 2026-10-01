namespace Aurora.Adapters.Files;

/// <summary>
/// Thrown when Aurora cannot establish owner-only protection on security-critical local material.
/// </summary>
/// <remarks>
/// The fail-closed half of <see cref="OwnerOnly"/> (F-3, docs/adr/0079). Aurora keeps its audit,
/// vault, snapshot, deliberation, plugin and genome keys — and the operator passphrase verifier —
/// in files whose only protection is that the operating system reaches them for the owner alone.
/// If that restriction cannot be applied, the guarantee those keys rest on does not hold, so the
/// operation refuses rather than continuing while claiming a protection it does not have. There is
/// deliberately no permissive fallback: the answer is to place the data where Aurora can restrict
/// it (a per-user directory), not to lower the bar.
/// </remarks>
public sealed class OwnerOnlyProtectionException : Exception
{
    public OwnerOnlyProtectionException(string path)
        : base(
            $"owner-only protection could not be established for '{path}'. Aurora will not keep "
            + "security-critical key material behind a permission it cannot narrow to the owner. "
            + "Place Aurora's data directory on a volume where the running account can restrict "
            + "its own files (a per-user location such as %LocalAppData%), then start again.")
    {
        Path = path;
    }

    /// <summary>The file whose protection could not be established.</summary>
    public string Path { get; }
}
