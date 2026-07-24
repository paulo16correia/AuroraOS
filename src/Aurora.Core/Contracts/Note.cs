namespace Aurora.Core.Contracts;

/// <summary>A note persisted by the <c>memory.remember</c> capability, scoped per principal.</summary>
public sealed record RememberedNote(string NoteId, string PrincipalClientId, string Note, string CreatedAtUtc);
