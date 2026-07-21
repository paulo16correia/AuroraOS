using System.Text;
using System.Text.Json;

namespace Aurora.Core.Serialization;

/// <summary>
/// Deterministic JSON canonicalization: object keys sorted by ordinal, arrays kept in order,
/// scalars compact. Used to derive stable hashes (input_hash, request_hash, record_hash) that
/// are independent of incidental key ordering or whitespace.
/// </summary>
public static class CanonicalJson
{
    public static string Canonicalize(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(element, sb);
        return sb.ToString();
    }

    private static void Write(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var prop in el.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    sb.Append(JsonSerializer.Serialize(prop.Name));
                    sb.Append(':');
                    Write(prop.Value, sb);
                }

                sb.Append('}');
                break;

            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        sb.Append(',');
                    }

                    firstItem = false;
                    Write(item, sb);
                }

                sb.Append(']');
                break;

            case JsonValueKind.String:
                sb.Append(JsonSerializer.Serialize(el.GetString()));
                break;

            case JsonValueKind.Number:
                // Preserve the literal token; two textually different numbers hash differently.
                sb.Append(el.GetRawText());
                break;

            case JsonValueKind.True:
                sb.Append("true");
                break;

            case JsonValueKind.False:
                sb.Append("false");
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                sb.Append("null");
                break;
        }
    }
}
