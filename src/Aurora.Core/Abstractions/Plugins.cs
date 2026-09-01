using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Runs a plugin's code, somewhere that is not this process.
/// </summary>
/// <remarks>
/// A seam, and the honest boundary of what Aurora can promise. The contract above it is enforced
/// whatever the host does; the isolation is only as good as the host, and the host is where the
/// platform-specific work lives.
/// </remarks>
public interface IPluginHost
{
    Task<PluginResult> InvokeAsync(
        PluginManifest manifest, PluginInvocation invocation, CancellationToken ct);
}

/// <summary>
/// Third-party capabilities, on a security contract rather than as a privileged exception (RFC 060).
/// </summary>
/// <remarks>
/// The point of the whole RFC in one sentence from its own justification: a plugin for one thing
/// can be useful without gaining powers over email, SSH or the Mind. Everything here exists to keep
/// a declaration from becoming an authority — a manifest states limits, and Aurora enforces exactly
/// those limits and nothing softer.
/// </remarks>
public interface IPluginRegistry
{
    /// <summary>
    /// Checks a manifest against its signature, its hash and this platform. Nothing runs first.
    /// </summary>
    Task<PluginVerification> VerifyAsync(PluginManifest manifest, CancellationToken ct);

    /// <summary>
    /// Installs a verified plugin. Needs an approval, and grants exactly what was reviewed (rule 3).
    /// </summary>
    Task<PluginInstallation> InstallAsync(
        PluginManifest manifest, IReadOnlyList<string> grantedPermissions,
        string approvalRef, CancellationToken ct);

    /// <summary>
    /// Applies an update. New permissions or a new publisher send it to quarantine (rule 5).
    /// </summary>
    Task<PluginInstallation> UpdateAsync(PluginManifest manifest, CancellationToken ct);

    /// <summary>
    /// Invokes a capability, refusing anything the manifest did not declare.
    /// </summary>
    Task<PluginResult> InvokeAsync(PluginInvocation invocation, CancellationToken ct);

    Task<PluginInstallation> DisableAsync(string installationId, string actor, CancellationToken ct);

    /// <summary>
    /// Ends an installation for good, and takes back everything it was granted.
    /// </summary>
    /// <remarks>
    /// <see cref="InstallationStatus.Removed"/> was a declared state nothing could reach: disabling
    /// held a plugin and releasing let it run again, and there was no way to be finished with one.
    /// Terminal — a removed installation cannot be released, because letting it back would restore
    /// permissions the owner had taken away.
    /// </remarks>
    Task<PluginInstallation> RemoveAsync(string installationId, string actor, CancellationToken ct);

    /// <summary>
    /// Installs with the network hosts and the graphics processor the owner agreed to.
    /// </summary>
    /// <remarks>
    /// Both are separate questions from the permissions and from each other. Somebody can
    /// reasonably want a plugin's capabilities without wanting it to reach the internet, or want
    /// it to reach the internet without handing it a graphics driver.
    /// </remarks>
    Task<PluginInstallation> InstallAsync(
        PluginManifest manifest, IReadOnlyList<string> grantedPermissions,
        IReadOnlyList<string> grantedEndpoints, bool grantGpu, string approvalRef,
        CancellationToken ct);

    /// <summary>Releases a quarantine, which is a decision and needs an approval.</summary>
    Task<PluginInstallation> ReleaseAsync(
        string installationId, string approvalRef, string actor, CancellationToken ct);

    /// <summary>Which declared events this plugin may actually receive, given what was granted.</summary>
    Task<IReadOnlyList<string>> PermittedSubscriptionsAsync(string pluginId, CancellationToken ct);

    Task<PluginInstallation?> GetAsync(string pluginId, CancellationToken ct);

    Task<IReadOnlyList<PluginInstallation>> ListAsync(CancellationToken ct);
}

/// <summary>
/// Turns "run this plugin" into "run this plugin, confined by the operating system".
/// </summary>
/// <remarks>
/// RFC 060 rule 2 says a plugin runs "without access to the main process, database, vault or
/// general network". A separate process delivers the first three by construction. The last one —
/// and the filesystem — are not properties of a process at all: they are properties of what the
/// kernel will let that process do, and only the operating system can decide them.
/// <para>
/// So this seam produces a <see cref="SandboxPlan"/>: the command actually to launch, and a
/// truthful statement of what confining it achieved. A platform that cannot confine says so rather
/// than returning the command unchanged and letting the caller assume.
/// </para>
/// <para>
/// <b>Why it also starts the process (docs/adr/0072).</b> Describing a command was enough while
/// every sandbox Aurora had was a wrapper program — <c>sandbox-exec</c> and <c>bwrap</c> both take
/// the plugin's path as an argument, so confinement fitted into a file name and a list of
/// arguments. Windows does not work that way: an AppContainer is a property of the token a process
/// is created with, reached through <c>CreateProcess</c> with a security-capabilities attribute
/// and not through anything <see cref="System.Diagnostics.ProcessStartInfo"/> exposes. A seam that
/// can only rewrite a command line cannot express it, so the seam starts the process instead.
/// </para>
/// </remarks>
public interface IPluginSandbox
{
    /// <summary>
    /// Decides how to launch this plugin, and reports what the launch will and will not enforce.
    /// </summary>
    /// <remarks>
    /// Separate from starting it, because "what would this machine do" is a question asked without
    /// wanting it done — the health report and the install decision both ask it.
    /// </remarks>
    SandboxPlan Plan(SandboxRequest request);

    /// <summary>
    /// Starts the plugin under this confinement, or explains why it did not.
    /// </summary>
    /// <remarks>
    /// A sandbox that cannot prove the confinement it promised must return a refusal here rather
    /// than a running process. It is the last moment at which the difference between "confined"
    /// and "started" can still be noticed, and after it every caller is entitled to assume they
    /// were the same thing.
    /// </remarks>
    Task<SandboxStart> StartAsync(SandboxLaunch launch, CancellationToken ct);
}

/// <summary>Everything needed to start one plugin, once the plan has been accepted.</summary>
/// <param name="Request">What was asked for, carried through so a sandbox can re-read the grants.</param>
/// <param name="Plan">The plan this launch is executing.</param>
/// <param name="Executable">The plugin's own program, which the plan may be wrapping.</param>
/// <param name="WorkingDirectory">The one directory it may write to.</param>
/// <param name="Environment">
/// The child's entire environment. Cleared rather than inherited: nothing about the owner's shell
/// travels into a plugin, and no secret travels this way either.
/// </param>
public sealed record SandboxLaunch(
    SandboxRequest Request,
    SandboxPlan Plan,
    string Executable,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>A started process, or the reason there is not one.</summary>
/// <remarks>
/// Both are values. A refusal to start is an ordinary outcome of asking a sandbox to do something
/// it cannot do safely, and an exception would make it indistinguishable from the plugin itself
/// being broken.
/// </remarks>
public sealed record SandboxStart(ISandboxedProcess? Process, string? Refused = null)
{
    public bool Started => Process is not null;
}

/// <summary>
/// A running plugin, seen through the three pipes and the two questions a host actually asks.
/// </summary>
/// <remarks>
/// Narrower than <see cref="System.Diagnostics.Process"/> on purpose. A host that could reach the
/// underlying process could also start one, and then the sandbox would be advice rather than a
/// boundary.
/// </remarks>
public interface ISandboxedProcess : IDisposable
{
    StreamWriter StandardInput { get; }

    StreamReader StandardOutput { get; }

    StreamReader StandardError { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken ct);

    /// <summary>Ends it, and everything it started.</summary>
    void Kill();
}

/// <summary>What the host wants confined.</summary>
/// <param name="PluginId">Used only to name the plugin in a refusal.</param>
/// <param name="Executable">The absolute path of the program to run.</param>
/// <param name="WorkingDirectory">
/// The one directory the plugin may write to. Everything else is read-only at best.
/// </param>
public sealed record SandboxRequest(
    string PluginId,
    string Executable,
    string WorkingDirectory,
    /// <summary>
    /// Whether the owner granted this plugin the network.
    /// </summary>
    /// <remarks>
    /// A boolean and not a host list, because that is the truth of what the sandbox can do. Neither
    /// <c>sandbox-exec</c> nor bubblewrap filters outbound traffic by hostname: the choice they
    /// offer is network or no network. The declared hosts are what the owner agreed to and what the
    /// plugin is audited against — they are not a boundary the kernel enforces, and pretending
    /// otherwise in this type would be the lie spreading into the code.
    /// </remarks>
    bool NetworkGranted = false,
    /// <summary>
    /// Whether the owner granted this plugin the graphics processor.
    /// </summary>
    /// <remarks>
    /// Separate from the network because it is a separate decision. Local speech recognition is
    /// unusable without it — a large model runs about twenty times slower on the processor alone —
    /// and a graphics driver is a wide surface to open to third-party code, so neither answer is
    /// obviously right and the owner gives it.
    /// </remarks>
    bool GpuGranted = false);

/// <summary>How confined a plugin actually is once launched.</summary>
public enum SandboxLevel
{
    /// <summary>
    /// A separate process and nothing more. Isolated from Aurora; not from the machine.
    /// </summary>
    Process,

    /// <summary>
    /// A separate process that the kernel also holds to a policy: no network, no writes outside
    /// its working directory, and no reading of the owner's files.
    /// </summary>
    Confined,
}

/// <summary>
/// The command to launch and the truth about it.
/// </summary>
/// <param name="FileName">The program to start — the sandbox wrapper, or the plugin itself.</param>
/// <param name="Arguments">
/// Everything before the plugin's own path, already in the wrapper's order.
/// </param>
/// <param name="Level">What launching this way actually enforces.</param>
/// <param name="Mechanism">
/// The name of the thing doing the enforcing, for the audit record and the refusal message —
/// <c>sandbox-exec</c>, <c>bubblewrap</c>, or why there is nothing.
/// </param>
/// <param name="Unenforced">
/// Each part of RFC 060 rule 2 this plan does not deliver, in words an owner can act on. Empty at
/// <see cref="SandboxLevel.Confined"/>.
/// </param>
public sealed record SandboxPlan(
    string FileName,
    IReadOnlyList<string> Arguments,
    SandboxLevel Level,
    string Mechanism,
    IReadOnlyList<string> Unenforced);
