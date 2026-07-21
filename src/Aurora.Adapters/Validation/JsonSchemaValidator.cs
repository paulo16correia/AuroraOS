using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Core.Abstractions;
using Json.Schema;

namespace Aurora.Adapters.Validation;

/// <summary>
/// <see cref="ISchemaValidator"/> backed by JsonSchema.Net (Json.Schema). Validation verdict is
/// authoritative; error-message extraction is best-effort so a minor library API difference only
/// degrades the messages, never the <see cref="SchemaValidationResult.IsValid"/> outcome.
/// </summary>
public sealed class JsonSchemaValidator : ISchemaValidator
{
    public SchemaValidationResult Validate(JsonElement schema, JsonElement input)
    {
        try
        {
            JsonSchema jsonSchema = JsonSchema.FromText(schema.GetRawText());
            JsonNode? node = JsonSerializer.SerializeToNode(input);
            EvaluationResults results = jsonSchema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (results.IsValid)
            {
                return SchemaValidationResult.Valid;
            }

            var errors = new List<string>();
            CollectErrors(results, errors);
            if (errors.Count == 0)
            {
                errors.Add("Input failed schema validation.");
            }

            return new SchemaValidationResult(false, errors);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // Fail closed: an input the schema layer cannot even evaluate (e.g. duplicate keys that
            // JsonNode rejects) or a malformed schema is treated as invalid, never as valid.
            return new SchemaValidationResult(false, ["Input could not be validated against the schema."]);
        }
    }

    private static void CollectErrors(EvaluationResults r, List<string> acc)
    {
        if (r.IsValid)
        {
            return;
        }

        if (r.Errors is not null)
        {
            foreach (KeyValuePair<string, string> kvp in r.Errors)
            {
                acc.Add($"{r.InstanceLocation}: {kvp.Value}");
            }
        }

        if (r.Details is not null)
        {
            foreach (EvaluationResults detail in r.Details)
            {
                CollectErrors(detail, acc);
            }
        }
    }
}
