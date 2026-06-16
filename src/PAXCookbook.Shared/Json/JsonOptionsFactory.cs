using System.Text.Json;

namespace PAXCookbook.Shared.Json;

// Shared System.Text.Json options. Matches the schema files: property
// names are camelCase, unknown properties are tolerated on read (forward
// compatibility), and indentation is enabled for human-readable
// install-state writes.
public static class JsonOptionsFactory
{
    public static JsonSerializerOptions Default { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow
        };
        return o;
    }
}
