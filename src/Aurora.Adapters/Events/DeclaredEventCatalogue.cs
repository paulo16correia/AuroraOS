using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Events;

/// <summary>
/// The compile-time declaration, and nothing else (LAW-007).
/// </summary>
/// <remarks>
/// The whole point of the catalogue is that it is closed: an event type nobody declared cannot be
/// published, by any producer, including through the ingress endpoint. This implementation adds no
/// way to widen it at runtime.
/// </remarks>
public sealed class DeclaredEventCatalogue : IEventCatalogue
{
    public IReadOnlyList<EventContract> Declared => EventCatalogue.Declared;

    public bool TryValidate(OutboxWrite write, out string? violation)
    {
        if (!EventCatalogue.TryGet(write.Type, write.SchemaVersion, out EventContract? contract))
        {
            violation = $"'{write.Type}' v{write.SchemaVersion} is not a declared event";
            return false;
        }

        // A producer emitting another's events is how a component starts speaking for a part of
        // the system it does not own.
        if (!string.Equals(contract!.Producer, write.Producer, StringComparison.Ordinal))
        {
            violation = $"'{write.Type}' is declared for producer '{contract.Producer}', not '{write.Producer}'";
            return false;
        }

        if (!string.Equals(contract.SensitivityClass, write.SensitivityClass, StringComparison.Ordinal))
        {
            violation = $"'{write.Type}' is declared {contract.SensitivityClass}, not {write.SensitivityClass}";
            return false;
        }

        violation = null;
        return true;
    }
}
