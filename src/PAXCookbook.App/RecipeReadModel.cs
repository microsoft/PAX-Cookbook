using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Read-only native recipe read model (X4). Mirrors the read paths of the
// PowerShell oracle (app\broker\Routes\Recipes.ps1) without porting any
// mutable behavior:
//   - GET /api/v1/recipes      -> active recipe list (SQLite index)
//   - GET /api/v1/recipes/{id} -> recipe detail (SQLite row + recipe file)
//
// The SQLite index (<workspace>\Database\cookbook.sqlite) is opened read-only;
// the per-recipe JSON document (<workspace>\Recipes\<id>.recipe.json) is the
// authoritative source for the recipe body. This model never writes, never
// creates the database, never repairs a malformed file, and never touches the
// PAX engine. A workspace with no database yields a real empty list.
internal static partial class RecipeReadModel
{
    // Oracle parity: $Script:M1_RecipeSchemaVer (the single supported version).
    private const int SupportedSchemaVersion = 1;

    // Oracle parity: $Script:RecipeIdPattern (ULID / Crockford base32, 26 chars).
    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{26}$")]
    private static partial Regex RecipeIdPattern();

    public static bool IsValidRecipeId(string id) => RecipeIdPattern().IsMatch(id);

    private static string DatabaseFile(string workspacePath) =>
        Path.Combine(workspacePath, "Database", "cookbook.sqlite");

    private static string RecipeFilePath(string workspacePath, string recipeId) =>
        Path.Combine(workspacePath, "Recipes", recipeId + ".recipe.json");

    // Opens the workspace SQLite index read-only. Returns null when the index
    // file does not exist (empty workspace), which the callers translate into a
    // real empty list / not-found rather than a fabricated stub.
    private static SqliteConnection? OpenReadOnly(string workspacePath)
    {
        string dbFile = DatabaseFile(workspacePath);
        if (!File.Exists(dbFile))
        {
            return null;
        }

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var conn = new SqliteConnection(csb.ConnectionString);
        conn.Open();
        return conn;
    }

    // GET /api/v1/recipes — active recipes, pinned first then created_at DESC.
    // Oracle: Get-RecipeRowsActive + Invoke-RecipesList. The pinned-first sort
    // and the isPinned projection are the X11 native product feature (the live
    // oracle sorts created_at DESC only and does not project is_pinned); the
    // within-group order (created_at DESC) is preserved exactly.
    public static object ListActive(string workspacePath)
    {
        var recipes = new List<object>();

        try
        {
            using SqliteConnection? conn = OpenReadOnly(workspacePath);
            if (conn is null)
            {
                // No index in this workspace: real empty list.
                return new { recipes };
            }

            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT recipe_id, name, status, is_pinned, last_cooked_at, last_cook_id, created_at, updated_at " +
                "FROM recipes WHERE deleted_at IS NULL ORDER BY is_pinned DESC, created_at DESC;";

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                recipes.Add(new
                {
                    recipeId = reader.GetString(0),
                    name = reader.GetString(1),
                    status = reader.GetString(2),
                    isPinned = reader.GetInt64(3) != 0,
                    lastCookedAt = reader.IsDBNull(4) ? null : reader.GetString(4),
                    lastCookId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    createdAt = reader.GetString(6),
                    updatedAt = reader.GetString(7),
                });
            }
        }
        catch (SqliteException)
        {
            // Index present but the recipes table is absent / unreadable: the
            // read model reports an empty list rather than crashing the SPA.
            recipes.Clear();
        }

        return new { recipes };
    }

    // GET /api/v1/recipes/{id} — recipe detail. Oracle: Invoke-RecipeGet.
    // Returns (httpStatus, body). The recipe-id format is validated by the
    // caller before this is invoked.
    public static (int Status, object Body) GetDetail(string workspacePath, string recipeId)
    {
        IReadOnlyDictionary<string, object?>? row = GetRecipeRow(workspacePath, recipeId);
        if (row is null || row["deleted_at"] is not null)
        {
            return (404, new { error = "not_found" });
        }

        RecipeFileResult loaded = ReadRecipeFile(workspacePath, recipeId);
        switch (loaded.Status)
        {
            case "ok":
                return (200, new { recipe = loaded.Recipe!.Value, meta = row });
            case "missing":
                return (404, new { error = "recipe_file_missing", recipeId });
            case "malformed":
                return (422, new { error = "recipe_file_malformed", recipeId, detail = loaded.Detail });
            case "unsupported_schema_version":
                return (422, new
                {
                    error = "recipe_unsupported_schema_version",
                    recipeId,
                    supportedSchemaVersion = SupportedSchemaVersion,
                    detail = loaded.Detail,
                });
            default:
                return (500, new { error = "recipe_load_unknown_status", recipeId, status = loaded.Status });
        }
    }

    // Oracle: Get-RecipeRow (full 17-column projection). Snake_case keys are
    // preserved verbatim because the SPA detail view reads `meta.<column>`.
    private static IReadOnlyDictionary<string, object?>? GetRecipeRow(string workspacePath, string recipeId)
    {
        try
        {
            using SqliteConnection? conn = OpenReadOnly(workspacePath);
            if (conn is null)
            {
                return null;
            }

            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT recipe_id, name, file_path, file_hash, status, is_pinned, " +
                "pax_adapter_version, recipe_schema_version, source, source_ref, " +
                "last_validated_at, last_validation_status, " +
                "last_cooked_at, last_cook_id, " +
                "created_at, updated_at, deleted_at " +
                "FROM recipes WHERE recipe_id = $id;";
            SqliteParameter p = cmd.CreateParameter();
            p.ParameterName = "$id";
            p.Value = recipeId;
            cmd.Parameters.Add(p);

            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new Dictionary<string, object?>
            {
                ["recipe_id"] = reader.GetString(0),
                ["name"] = reader.GetString(1),
                ["file_path"] = reader.GetString(2),
                ["file_hash"] = reader.GetString(3),
                ["status"] = reader.GetString(4),
                ["is_pinned"] = (int)reader.GetInt64(5),
                ["pax_adapter_version"] = reader.GetString(6),
                ["recipe_schema_version"] = (int)reader.GetInt64(7),
                ["source"] = reader.GetString(8),
                ["source_ref"] = reader.IsDBNull(9) ? null : reader.GetString(9),
                ["last_validated_at"] = reader.IsDBNull(10) ? null : reader.GetString(10),
                ["last_validation_status"] = reader.IsDBNull(11) ? null : reader.GetString(11),
                ["last_cooked_at"] = reader.IsDBNull(12) ? null : reader.GetString(12),
                ["last_cook_id"] = reader.IsDBNull(13) ? null : reader.GetString(13),
                ["created_at"] = reader.GetString(14),
                ["updated_at"] = reader.GetString(15),
                ["deleted_at"] = reader.IsDBNull(16) ? null : reader.GetString(16),
            };
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private readonly record struct RecipeFileResult(string Status, JsonElement? Recipe, string? Detail);

    // Oracle: Read-RecipeFile. Discriminated, read-only load of the recipe
    // document. No auto-repair, no rewrite, no side effects.
    private static RecipeFileResult ReadRecipeFile(string workspacePath, string recipeId)
    {
        string path = RecipeFilePath(workspacePath, recipeId);
        if (!File.Exists(path))
        {
            return new RecipeFileResult("missing", null, null);
        }

        string raw;
        try
        {
            raw = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            return new RecipeFileResult("malformed", null, "file_read_failed: " + ex.Message);
        }

        JsonElement parsed;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            parsed = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new RecipeFileResult("malformed", null, "json_parse_failed: " + ex.Message);
        }

        if (parsed.ValueKind != JsonValueKind.Object)
        {
            return new RecipeFileResult("malformed", null, "json_root_not_object");
        }

        if (!parsed.TryGetProperty("recipeSchemaVersion", out JsonElement versionEl))
        {
            return new RecipeFileResult("unsupported_schema_version", parsed, "absent");
        }

        int? observed = ReadSchemaVersion(versionEl);
        if (observed is null || observed.Value != SupportedSchemaVersion)
        {
            return new RecipeFileResult("unsupported_schema_version", parsed, "observed=" + DescribeVersion(versionEl));
        }

        return new RecipeFileResult("ok", parsed, null);
    }

    // Discriminated load that surfaces the recipe as a mutable CLR tree
    // (Dictionary<string, object?>) so the mutation routes (update) can preserve
    // provenance leaves and reuse the same 422 vocabulary the GET detail path
    // emits. Read-only; no repair, no rewrite, no side effects. createdAt /
    // updatedAt remain strings — System.Text.Json performs no datetime coercion,
    // matching the oracle's `ConvertFrom-Json -AsHashtable -DateKind String`.
    internal readonly record struct RecipeTreeLoad(
        string Status, Dictionary<string, object?>? Recipe, string? Detail);

    public static RecipeTreeLoad LoadRecipeTree(string workspacePath, string recipeId)
    {
        RecipeFileResult r = ReadRecipeFile(workspacePath, recipeId);
        Dictionary<string, object?>? dict =
            r.Recipe is { } el ? JsonModel.FromElement(el) as Dictionary<string, object?> : null;
        return new RecipeTreeLoad(r.Status, dict, r.Detail);
    }

    // Tolerant version reader: accepts a JSON integer or a numeric string,
    // mirroring the oracle's loader (which validates the version truthfully
    // rather than performing full schema validation).
    private static int? ReadSchemaVersion(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetInt32(out int n) ? n : null;
            case JsonValueKind.String:
                string s = el.GetString() ?? string.Empty;
                return Regex.IsMatch(s, "^[0-9]+$") && int.TryParse(s, out int parsed) ? parsed : null;
            default:
                return null;
        }
    }

    private static string DescribeVersion(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.String => el.GetString() ?? string.Empty,
        _ => el.ValueKind.ToString(),
    };
}
