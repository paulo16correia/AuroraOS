using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Time;

/// <summary>Wall-clock <see cref="IClock"/> backed by the system UTC time.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
