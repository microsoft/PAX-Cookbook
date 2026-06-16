using System.Text;
using System.Text.Json;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3e -- discriminated read of <workspace>/Recipes/<recipeId>.recipe.json
// mirroring Read-RecipeFile (app/broker/Routes/Recipes.ps1 ~line 192).
// The PS broker returns a hashtable @{ status; recipe; detail }; the
// native broker returns the C# record below. Status values match the
// PS contract verbatim: ok | missing | malformed | unsupported_schema_version.
//
// Doctrine (parity with PS broker):
//   - UTF-8 NoBOM read via File.ReadAllText (which honors the BOM if
//     present but does not require one).
//   - JSON parse failure (including non-object root) -> 'malformed'.
//   - Missing recipeSchemaVersion -> 'unsupported_schema_version' with
//     the parsed object still returned for caller introspection.
//   - Schema version 1 is the only supported value at Stage 3e
//     (matches M1_RecipeSchemaVer in the PS broker).
//   - This reader is PURE: no FS mutation, no logging, no autofix.
public sealed class RecipeFileReader
{
    private readonly WorkspacePaths _paths;
    private const int SupportedSchemaVersion = 1;

    public RecipeFileReader(WorkspacePaths paths) => _paths = paths;

    // Resolves the recipe file location by id. Parity with
    // Get-RecipeFilePath: Join-Path $Script:RecipesDir
    // ($RecipeId + '.recipe.json').
    public string ResolvePath(string recipeId) =>
        Path.Combine(_paths.RecipesDir, recipeId + ".recipe.json");

    public RecipeFileReadResult Load(string recipeId)
    {
        var path = ResolvePath(recipeId);

        if (!File.Exists(path))
        {
            return RecipeFileReadResult.Missing(path);
        }

        string raw;
        try
        {
            // Explicit UTF-8 to mirror the PS broker's
            // [System.Text.UTF8Encoding]::new($false). File.ReadAllText
            // with a UTF8 instance reads BOM if present and validates.
            raw = File.ReadAllText(path, new UTF8Encoding(false, false));
        }
        catch (Exception ex)
        {
            return RecipeFileReadResult.Malformed(path,
                "file_read_failed: " + ex.Message);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            return RecipeFileReadResult.Malformed(path,
                "json_parse_failed: " + ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return RecipeFileReadResult.Malformed(path, "json_root_not_object");
            }

            // recipeSchemaVersion gate. The PS broker accepts integer
            // 1 only; the JSON spec permits floats, so we allow both
            // GetInt32 and a Number that round-trips to int.
            if (!root.TryGetProperty("recipeSchemaVersion", out var schemaVer))
            {
                return RecipeFileReadResult.UnsupportedSchemaVersion(path, raw,
                    "recipeSchemaVersion_missing");
            }
            if (schemaVer.ValueKind != JsonValueKind.Number
                || !schemaVer.TryGetInt32(out var sv))
            {
                return RecipeFileReadResult.UnsupportedSchemaVersion(path, raw,
                    "recipeSchemaVersion_not_integer");
            }
            if (sv != SupportedSchemaVersion)
            {
                return RecipeFileReadResult.UnsupportedSchemaVersion(path, raw,
                    "recipeSchemaVersion=" + sv);
            }

            // executionMode is required at the manual cook entry; PS
            // broker rejects non-'local-manual' with 412 recipe_invalid.
            string? executionMode = null;
            if (root.TryGetProperty("executionMode", out var em)
                && em.ValueKind == JsonValueKind.String)
            {
                executionMode = em.GetString();
            }

            string? authMode = null;
            if (root.TryGetProperty("auth", out var auth)
                && auth.ValueKind == JsonValueKind.Object
                && auth.TryGetProperty("mode", out var modeEl)
                && modeEl.ValueKind == JsonValueKind.String)
            {
                authMode = modeEl.GetString();
            }

            return RecipeFileReadResult.Ok(path, raw, executionMode, authMode);
        }
    }
}

public enum RecipeFileReadStatus
{
    Ok,
    Missing,
    Malformed,
    UnsupportedSchemaVersion,
}

public sealed record RecipeFileReadResult(
    RecipeFileReadStatus Status,
    string               Path,
    string?              RawJson,
    string?              ExecutionMode,
    string?              AuthMode,
    string?              Detail)
{
    public static RecipeFileReadResult Ok(string path, string raw,
        string? executionMode, string? authMode) =>
        new(RecipeFileReadStatus.Ok, path, raw, executionMode, authMode, null);

    public static RecipeFileReadResult Missing(string path) =>
        new(RecipeFileReadStatus.Missing, path, null, null, null, null);

    public static RecipeFileReadResult Malformed(string path, string detail) =>
        new(RecipeFileReadStatus.Malformed, path, null, null, null, detail);

    public static RecipeFileReadResult UnsupportedSchemaVersion(string path,
        string raw, string detail) =>
        new(RecipeFileReadStatus.UnsupportedSchemaVersion,
            path, raw, null, null, detail);
}
