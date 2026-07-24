using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Persistence for notes saved via the <c>memory.remember</c> capability.</summary>
public interface INoteStore
{
    Task<RememberedNote> SaveAsync(Principal principal, string note, CancellationToken ct);

    Task<IReadOnlyList<RememberedNote>> ListAsync(Principal principal, CancellationToken ct);
}
