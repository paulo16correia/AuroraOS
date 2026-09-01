namespace Aurora.Adapters.Plugins.Sandboxes.Windows;

/// <summary>
/// Whether a process that was created is allowed to run.
/// </summary>
/// <remarks>
/// This is the fail-closed decision, kept apart from the interop that gathers its inputs so that
/// every path through it can be tested on a machine that has no Windows on it. The interop asks
/// the kernel three questions; this decides what the answers mean.
/// <para>
/// The order matters. The process is created <b>suspended</b>, so at the moment this runs the
/// plugin has not executed one instruction. A verdict of anything but
/// <see cref="Confined"/> ends with the process terminated, and there is no state to unwind
/// because nothing ran. Verifying afterwards — letting it start and then checking — would mean
/// the interesting failure is the one where a plugin does its damage in the milliseconds before
/// Aurora catches up.
/// </para>
/// </remarks>
/// <param name="Confined">Whether the process may be resumed.</param>
/// <param name="Refused">
/// Why it may not, in words that name the missing property rather than the API that reported it.
/// Null when confined.
/// </param>
public sealed record AppContainerVerdict(bool Confined, string? Refused = null)
{
    private static readonly AppContainerVerdict Allowed = new(true);

    /// <summary>
    /// Judges a created process against the container it was supposed to be created in.
    /// </summary>
    /// <param name="tokenRead">
    /// Whether the child's token could be opened and queried at all. False is a refusal and not a
    /// benefit of the doubt: a confinement that cannot be checked has not been demonstrated, and
    /// "probably fine" is the sentence that precedes every unconfined plugin.
    /// </param>
    /// <param name="isAppContainer">What the kernel says about the token's own nature.</param>
    /// <param name="actualSid">The container SID the process is actually running under.</param>
    /// <param name="expectedSid">The container SID Aurora created and intended.</param>
    public static AppContainerVerdict Of(
        bool tokenRead, bool isAppContainer, string? actualSid, string expectedSid)
    {
        if (!tokenRead)
        {
            return new AppContainerVerdict(
                false,
                "the child's token could not be read, so its confinement could not be "
                + "demonstrated; refusing rather than assuming");
        }

        if (!isAppContainer)
        {
            // The failure this whole exercise exists to catch. A CreateProcess that quietly
            // ignored the security-capabilities attribute produces a working plugin with no
            // confinement at all, and every layer above would report it as confined.
            return new AppContainerVerdict(
                false,
                "the process was created outside an AppContainer, so nothing constrains what it "
                + "may read, write or reach");
        }

        if (string.IsNullOrEmpty(actualSid))
        {
            return new AppContainerVerdict(
                false, "the process is in an AppContainer that does not identify itself");
        }

        if (!string.Equals(actualSid, expectedSid, StringComparison.OrdinalIgnoreCase))
        {
            // A different container is a different set of grants. It may be a wider one, and it is
            // certainly not the one whose filesystem access Aurora just decided on.
            return new AppContainerVerdict(
                false,
                "the process is in a different AppContainer from the one Aurora created, so the "
                + "grants it is running under are not the grants that were decided");
        }

        return Allowed;
    }
}
