using System.Text;
using System.Text.Json;

namespace PAXCookbook.App;

// Mutable CLR value tree that mirrors PowerShell's
// `ConvertFrom-Json -AsHashtable` shape so the recipe validator and the PAX
// adapter projection can use the same membership / type checks the oracle uses:
//   object  -> Dictionary<string, object?> (case-insensitive keys, like a
//              PowerShell hashtable)
//   array   -> List<object?>
//   string  -> string
//   integer -> long
//   number  -> double
//   boolean -> bool
//   null    -> null
//
// The tree is mutable so the draft-preview path can fill server-managed fields
// (recipeId / recipeSchemaVersion / paxAdapterVersion / createdBy) exactly as
// the oracle does, without persisting anything.
internal static class JsonModel
{
    // Parses a JSON document string into the CLR tree. Returns null for
    // empty / whitespace input or malformed JSON (oracle: Read-RequestJson
    // returns $null which the route translates to 400 invalid_json).
    public static object? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return FromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Converts an already-parsed JsonElement (e.g. a recipe file loaded by
    // RecipeReadModel) into the CLR tree.
    public static object? FromElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                // Case-insensitive keys mirror a PowerShell hashtable's default
                // ContainsKey / indexing behavior used throughout the oracle.
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty p in el.EnumerateObject())
                {
                    dict[p.Name] = FromElement(p.Value);
                }
                return dict;

            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (JsonElement item in el.EnumerateArray())
                {
                    list.Add(FromElement(item));
                }
                return list;

            case JsonValueKind.String:
                return el.GetString();

            case JsonValueKind.Number:
                string rawNum = el.GetRawText();
                bool looksIntegral =
                    rawNum.IndexOf('.') < 0 &&
                    rawNum.IndexOf('e') < 0 &&
                    rawNum.IndexOf('E') < 0;
                if (looksIntegral && el.TryGetInt64(out long l))
                {
                    return l;
                }
                return el.GetDouble();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            default:
                return null;
        }
    }

    // Reads the request body and parses it into the CLR tree.
    public static async Task<object?> ReadBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        string raw = await reader.ReadToEndAsync();
        return Parse(raw);
    }

    // Serializes the CLR tree to UTF-8 bytes with no BOM, mirroring the oracle's
    // Write-RecipeFile ($obj | ConvertTo-Json -Depth 12 written with a no-BOM
    // UTF-8 encoder). Byte-for-byte parity with PowerShell's ConvertTo-Json is
    // neither required nor attempted; the contract is that the persisted bytes
    // are valid JSON that the read model parses back to the same value tree.
    // Each leaf is written by its CLR runtime type so integers stay integers
    // (recipeSchemaVersion = 1, not 1.0).
    public static byte[] SerializeToUtf8Bytes(object? node) => SerializeToUtf8Bytes(node, indented: false);

    public static byte[] SerializeToUtf8Bytes(object? node, bool indented)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            WriteNode(writer, node);
        }
        return stream.ToArray();
    }

    private static void WriteNode(Utf8JsonWriter w, object? node)
    {
        switch (node)
        {
            case null:
                w.WriteNullValue();
                break;
            case Dictionary<string, object?> dict:
                w.WriteStartObject();
                foreach (KeyValuePair<string, object?> kv in dict)
                {
                    w.WritePropertyName(kv.Key);
                    WriteNode(w, kv.Value);
                }
                w.WriteEndObject();
                break;
            case List<object?> list:
                w.WriteStartArray();
                foreach (object? item in list)
                {
                    WriteNode(w, item);
                }
                w.WriteEndArray();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case double d:
                w.WriteNumberValue(d);
                break;
            default:
                w.WriteStringValue(Convert.ToString(node, System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }

    // ---- Tree accessors mirroring PowerShell hashtable idioms ----

    public static Dictionary<string, object?>? AsDict(object? node) =>
        node as Dictionary<string, object?>;

    public static List<object?>? AsList(object? node) =>
        node as List<object?>;

    // [string]$x — a string leaf becomes itself; null becomes ""; other scalars
    // stringify with the invariant culture. Used only on leaves the oracle also
    // coerces with [string].
    public static string Str(object? node)
    {
        if (node is null)
        {
            return string.Empty;
        }
        if (node is string s)
        {
            return s;
        }
        if (node is bool b)
        {
            return b ? "True" : "False";
        }
        return Convert.ToString(node, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    // [bool]$x — JSON booleans pass through; absent/null is false. Non-bool
    // leaves coerce the way the oracle's [bool] cast does for the values that
    // actually reach these gates (JSON booleans).
    public static bool Bool(object? node)
    {
        if (node is bool b)
        {
            return b;
        }
        if (node is null)
        {
            return false;
        }
        if (node is long l)
        {
            return l != 0;
        }
        if (node is double d)
        {
            return d != 0;
        }
        if (node is string s)
        {
            return !string.IsNullOrEmpty(s);
        }
        return true;
    }

    // [int]$x for the JSON shapes that actually reach the schedule gate / read
    // projection. A JSON integer arrives as long and a JSON number as double, so
    // both are accepted (a non-integral double is rejected). Returns false for
    // any value that is not a whole number in the Int32 range. No raw cast: the
    // caller decides what an out-of-range / non-numeric value means.
    public static bool TryInt(object? node, out int value)
    {
        switch (node)
        {
            case long l when l >= int.MinValue && l <= int.MaxValue:
                value = (int)l;
                return true;
            case int i:
                value = i;
                return true;
            case double d when d >= int.MinValue && d <= int.MaxValue && d == (long)d:
                value = (int)d;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
