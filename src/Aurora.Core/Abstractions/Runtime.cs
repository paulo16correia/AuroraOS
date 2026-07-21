using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Abstracts the wall clock so time-dependent behaviour is deterministic under test.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Supplies the authenticated principal for the current request. Established by the transport.</summary>
public interface IPrincipalAccessor
{
    Principal Current { get; }
}
