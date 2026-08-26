using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aurora.Core.Contracts;

/// <summary>
/// Builds a capability's input schema without writing JSON by hand.
/// </summary>
/// <remarks>
/// Every capability declares a JSON Schema for its input, and until now every one of them was a
/// string literal — two hundred characters on one line, with no compiler to check it and no
/// editor to complete it. A mistake in one is invisible until a caller sends valid input and
/// Aurora rejects it.
/// <para>
/// Two things are decided here rather than left to whoever is typing. <c>additionalProperties</c>
/// is always <see langword="false"/>, so a capability never silently accepts a field it does not
/// understand; and <c>$schema</c> is always the 2020-12 draft, so two capabilities cannot end up
/// validated against different dialects.
/// </para>
/// <example>
/// <code>
/// public CapabilityDescriptor Descriptor { get; } = new(
///     ActionId: "notes.write",
///     InputSchema: CapabilityInput.Object()
///         .String("note", required: true, minLength: 1, maxLength: 500)
///         .Boolean("pinned")
///         .Build(),
///     ...);
/// </code>
/// </example>
/// </remarks>
public sealed class CapabilityInput
{
    private const string Draft = "https://json-schema.org/draft/2020-12/schema";

    private readonly JsonObject _properties = [];
    private readonly List<string> _required = [];

    private CapabilityInput()
    {
    }

    /// <summary>An object schema. The only shape a capability's input may take.</summary>
    /// <remarks>
    /// Not a limitation worth working around: an object is the only shape that can gain a field
    /// later without breaking a caller that already sends the old ones.
    /// </remarks>
    public static CapabilityInput Object() => new();

    /// <summary>A string field.</summary>
    /// <param name="name">The property name, as callers will send it.</param>
    /// <param name="required">Whether a call without it is invalid.</param>
    /// <param name="minLength">
    /// Shortest accepted value. Use <c>1</c> to reject the empty string, which is almost always
    /// what you mean for a path, an identifier or anything that names something.
    /// </param>
    /// <param name="maxLength">
    /// Longest accepted value. Required, and deliberately so: a string field with no ceiling is a
    /// way to put as much as you like into Aurora's database through one call.
    /// </param>
    /// <param name="pattern">An optional regular expression the value must match.</param>
    public CapabilityInput String(
        string name, int maxLength, bool required = false, int? minLength = null,
        string? pattern = null)
    {
        var schema = new JsonObject { ["type"] = "string", ["maxLength"] = maxLength };

        if (minLength is not null)
        {
            schema["minLength"] = minLength;
        }

        if (pattern is not null)
        {
            schema["pattern"] = pattern;
        }

        return Add(name, schema, required);
    }

    /// <summary>A boolean field.</summary>
    public CapabilityInput Boolean(string name, bool required = false) =>
        Add(name, new JsonObject { ["type"] = "boolean" }, required);

    /// <summary>An integer field, between <paramref name="minimum"/> and <paramref name="maximum"/>.</summary>
    public CapabilityInput Integer(
        string name, int minimum, int maximum, bool required = false) =>
        Add(
            name,
            new JsonObject { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum },
            required);

    /// <summary>An array of objects, each described by <paramref name="items"/>.</summary>
    /// <param name="maxItems">
    /// How many the capability will accept. Required for the same reason a string needs a ceiling:
    /// without one, a single call can ask Aurora to do unbounded work.
    /// </param>
    public CapabilityInput ArrayOf(
        string name, CapabilityInput items, int maxItems, bool required = false, int minItems = 0)
    {
        var schema = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = maxItems,
            ["items"] = items.Body(),
        };

        if (minItems > 0)
        {
            schema["minItems"] = minItems;
        }

        return Add(name, schema, required);
    }

    /// <summary>An array of strings.</summary>
    public CapabilityInput ArrayOfStrings(
        string name, int maxItems, int maxLength, bool required = false, int minItems = 0)
    {
        var schema = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = maxItems,
            ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = maxLength },
        };

        if (minItems > 0)
        {
            schema["minItems"] = minItems;
        }

        return Add(name, schema, required);
    }

    /// <summary>The finished schema, ready for a <see cref="CapabilityDescriptor"/>.</summary>
    public JsonElement Build()
    {
        JsonObject body = Body();
        body.Insert(0, "$schema", Draft);

        // Parsed and cloned so the result is detached from the builder: a descriptor holding a
        // live view of a mutable object would be a schema that could change after publication.
        using JsonDocument document = JsonDocument.Parse(body.ToJsonString());
        return document.RootElement.Clone();
    }

    private JsonObject Body()
    {
        var body = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
        };

        if (_required.Count > 0)
        {
            body["required"] = new JsonArray([.. _required.Select(r => (JsonNode)r!)]);
        }

        body["properties"] = (JsonNode)_properties.DeepClone();
        return body;
    }

    private CapabilityInput Add(string name, JsonObject schema, bool required)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A field needs a name.", nameof(name));
        }

        if (_properties.ContainsKey(name))
        {
            throw new ArgumentException($"'{name}' is already declared.", nameof(name));
        }

        _properties[name] = schema;

        if (required)
        {
            _required.Add(name);
        }

        return this;
    }
}
