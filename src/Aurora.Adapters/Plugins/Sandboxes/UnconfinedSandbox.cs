using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Plugins.Sandboxes;

/// <summary>
/// The absence of a sandbox, said out loud.
/// </summary>
/// <remarks>
/// This type exists so that "we could not confine it" is a value the host receives and can refuse
/// on, rather than a silence. A sandbox seam whose fallback quietly returns the command unchanged
/// is worse than no seam at all: it converts a missing security property into an invisible one.
/// </remarks>
public sealed class UnconfinedSandbox : WrapperSandbox
{
    private readonly string _because;

    public UnconfinedSandbox(string because)
    {
        _because = because;
    }

    public override SandboxPlan Plan(SandboxRequest request) => new(
        request.Executable,
        [],
        SandboxLevel.Process,
        _because,
        [
            "the plugin can open network connections",
            "the plugin can read every file the owner can read, including Aurora's database and key files",
            "the plugin can write anywhere the owner can write",
        ]);
}
