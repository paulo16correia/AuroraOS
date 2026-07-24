using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Capabilities;

/// <summary>Read-only LOW capability that lists notes previously saved via <c>memory.remember</c>.</summary>
public sealed class RecallNotesCapability : ICapability
{
    private const string InputSchemaJson =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"properties":{}}""";

    private static readonly JsonElement SchemaElement =
        JsonDocument.Parse(InputSchemaJson).RootElement.Clone();

    private readonly INoteStore _notes;
    private readonly IPrincipalAccessor _principals;

    public RecallNotesCapability(INoteStore notes, IPrincipalAccessor principals)
    {
        _notes = notes;
        _principals = principals;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        ActionId: "memory.recall",
        Title: "Recall notes",
        Description: "Lists notes previously remembered.",
        InputSchema: SchemaElement,
        Effects: Array.Empty<string>(),
        Risk: RiskLevel.Low,
        ApprovalRequired: false);

    public async ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var notes = await _notes.ListAsync(_principals.Current, ct).ConfigureAwait(false);
        var array = new JsonArray();
        foreach (var note in notes)
        {
            array.Add(new JsonObject
            {
                ["note_id"] = note.NoteId,
                ["note"] = note.Note,
                ["created_at"] = note.CreatedAtUtc,
            });
        }

        return JsonSerializer.SerializeToElement(new JsonObject { ["notes"] = array });
    }
}
