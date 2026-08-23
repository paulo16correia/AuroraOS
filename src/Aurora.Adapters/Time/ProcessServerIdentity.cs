using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Time;

/// <summary>
/// Identifies this process by a value generated at construction (docs/adr/0010).
/// </summary>
/// <remarks>
/// Registered as a singleton, so the id lasts exactly as long as the run. That is the whole
/// mechanism behind "grants do not survive a restart": sessions store the boot id they were opened
/// under, and after a restart none of them match any more. No sweeper, nothing to forget to run.
/// </remarks>
public sealed class ProcessServerIdentity : IServerIdentity
{
    public string BootId { get; } = Guid.NewGuid().ToString("N");
}
