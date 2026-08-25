using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aurora.Core;

/// <summary>
/// Central JSON options. Every Aurora wire contract is snake_case; enums serialize as
/// snake_case strings. Kept in one place so the server and the stores agree byte-for-byte.
/// </summary>
public static class AuroraJson
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Apply(o);
        return o;
    }

    /// <summary>
    /// Applies the contract to options owned by someone else — the ASP.NET Core host, for one.
    /// </summary>
    /// <remarks>
    /// The host cannot be handed <see cref="Options"/> directly, and a second, slightly different
    /// set of rules for the same wire format is how a field ends up named two ways depending on
    /// which code path answered.
    /// </remarks>
    public static void Apply(JsonSerializerOptions target)
    {
        target.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        target.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
        target.PropertyNameCaseInsensitive = false;
        target.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        target.WriteIndented = false;
        target.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Deserialized to null.");
}
