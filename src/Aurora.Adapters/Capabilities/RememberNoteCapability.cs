using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Capabilities;

/// <summary>
/// MEDIUM, approval-gated capability that persists a short note (design/0002). The first
/// capability with a real, stateful effect; exercises the It.2 approval path end to end.
/// </summary>
public sealed class RememberNoteCapability : ICapability
{
    private const string InputSchemaJson =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"required":["note"],"properties":{"note":{"type":"string","minLength":1,"maxLength":500}}}""";

    private static readonly JsonElement SchemaElement =
        JsonDocument.Parse(InputSchemaJson).RootElement.Clone();

    private readonly INoteStore _notes;
    private readonly IPrincipalAccessor _principals;

    public RememberNoteCapability(INoteStore notes, IPrincipalAccessor principals)
    {
        _notes = notes;
        _principals = principals;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        ActionId: "memory.remember",
        Title: "Remember a note",
        Description: "Persists a short note for later recall. Requires approval.",
        InputSchema: SchemaElement,
        Effects: ["memory.write"],
        Risk: RiskLevel.Medium,
        ApprovalRequired: true);

    public async ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var note = input.GetProperty("note").GetString() ?? string.Empty;
        var saved = await _notes.SaveAsync(_principals.Current, note, ct).ConfigureAwait(false);

        var obj = new JsonObject { ["note_id"] = saved.NoteId, ["note"] = saved.Note };
        return JsonSerializer.SerializeToElement(obj);
    }
}
