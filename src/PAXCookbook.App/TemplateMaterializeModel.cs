using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Native port of the template-materialize route (Invoke-TemplateMaterialize in
// app\broker\Routes\Templates.ps1). This is the last remaining recipe/template
// local-state mutation route: it turns a bundled, read-only template plus the
// operator's per-instance inputs into a brand-new recipe (one file, one index
// row). It never invokes PAX, never reads or mutates the PAX bytes, never
// acquires an engine, and never touches cook / scheduler / notification state.
//
// Parity is decision parity, not byte parity: the persisted JSON is produced by
// System.Text.Json rather than PowerShell's ConvertTo-Json, so the on-disk
// bytes differ from the oracle's, but the status codes, error shapes, decision
// order, materialized recipe shape, and row/file/hash relationship match the
// oracle.
//
// Decision order (oracle Invoke-TemplateMaterialize):
//   1. template not in catalog                     -> 404 template_not_found
//   2. template requires a newer bundled PAX       -> 412 template_incompatible
//   3. request body empty / malformed              -> 400 invalid_json
//   4. body fails the materialize body schema      -> 400 materialize_body_invalid
//   5. materialized recipe fails the recipe gates  -> 400 materialize_recipe_invalid
//   6. persist (file-first, row-second)            -> 201 { recipeId, recipe }
//
// Auth gating: matching the live oracle, this route is NOT re-auth gated
// (Invoke-TemplatesRoute dispatches materialize straight to
// Invoke-TemplateMaterialize with no Invoke-BrokerLockReAuthForOp); the bearer
// token, CSRF header, and broker lock are all enforced upstream before this
// runs.
internal static class TemplateMaterializeModel
{
    // Oracle parity: $Script:M1_RecipeSchemaVer (the single supported version).
    private const long RecipeSchemaVersion = 1L;

    private static string RecipesDir(string workspacePath) =>
        Path.Combine(workspacePath, "Recipes");

    private static string RecipesTrashDir(string workspacePath) =>
        Path.Combine(workspacePath, "Recipes", "_trash");

    private static string RecipeFilePath(string workspacePath, string recipeId) =>
        Path.Combine(RecipesDir(workspacePath), recipeId + ".recipe.json");

    private static string DatabaseFile(string workspacePath) =>
        Path.Combine(workspacePath, "Database", "cookbook.sqlite");

    // Oracle: Invoke-TemplateMaterialize. Returns (httpStatus, body). The route
    // validates the template-id pattern (400 invalid_template_id) before this
    // is reached.
    public static (int Status, object Body) Handle(
        TemplateReadModel templates,
        string workspacePath,
        VersionInfo versionInfo,
        string templateId,
        object? body)
    {
        // 1. Oracle: $Script:TemplateCatalog.ContainsKey($TemplateId) -> 404.
        if (!templates.TryGetTemplate(templateId, out JsonElement tpl))
        {
            return (404, new { error = "template_not_found", templateId });
        }

        // 2. Oracle: Test-TemplatePaxCompatibility -> 412 template_incompatible.
        Dictionary<string, object?>? paxError = PaxCompatibilityError(tpl, versionInfo.PaxVersion);
        if (paxError is not null)
        {
            return (412, new
            {
                error = "template_incompatible",
                templateId,
                bundledPaxVersion = versionInfo.PaxVersion,
                minPaxScriptVersion = TplStr(tpl, "minPaxScriptVersion"),
                details = new[] { paxError },
            });
        }

        // 3. Oracle: $body = Read-RequestJson; $null -> 400 invalid_json.
        if (body is not Dictionary<string, object?> bodyDict)
        {
            return (400, new { error = "invalid_json" });
        }

        // 4. Oracle: Test-RecipeSchemaNode against $Script:TemplateMaterializeBodySchema.
        List<object> bodyErrors = RecipeValidationModel.ValidateMaterializeBody(bodyDict);
        if (bodyErrors.Count > 0)
        {
            return (400, new { error = "materialize_body_invalid", errors = bodyErrors });
        }

        // 5. Oracle: $recipe = ConvertTo-MaterializedRecipe; Test-RecipeAll.
        string now = UtcNowIso();
        string id = NewRecipeId();
        Dictionary<string, object?> recipe = ConvertToMaterializedRecipe(tpl, bodyDict, versionInfo, id, now);

        (bool ok, List<object> recipeErrors) = RecipeValidationModel.ValidateAll(recipe);
        if (!ok)
        {
            return (400, new
            {
                error = "materialize_recipe_invalid",
                templateId,
                recipeId = id,
                errors = recipeErrors,
            });
        }

        // 6. Oracle: Initialize-RecipesDirs; Write-RecipeFile; Add-RecipeRow.
        Directory.CreateDirectory(RecipesDir(workspacePath));
        Directory.CreateDirectory(RecipesTrashDir(workspacePath));

        // File-first, row-second. On row-insert failure, delete the file so the
        // two never diverge (oracle: catch -> Remove-Item -> throw).
        string finalPath = RecipeFilePath(workspacePath, id);
        string fileHash = WriteRecipeFile(finalPath, recipe);

        // Oracle: source='template', source_ref = '<templateId>@<templateVersion>'.
        string sourceRef = TplStr(tpl, "templateId") + "@" + TplStr(tpl, "templateVersion");

        try
        {
            AddRecipeRow(
                workspacePath,
                recipeId: id,
                name: RecipeName(recipe),
                paxAdapterVersion: versionInfo.PaxVersion,
                sourceRef: sourceRef,
                filePath: finalPath,
                fileHash: fileHash,
                createdAt: now,
                updatedAt: now);
        }
        catch (Exception ex)
        {
            if (File.Exists(finalPath))
            {
                try { File.Delete(finalPath); } catch { /* best-effort cleanup */ }
            }
            return (500, new
            {
                error = "persist_failed",
                message = "The recipe index row could not be written; the recipe file was rolled back.",
                detail = ex.Message,
            });
        }

        return (201, new { recipeId = id, recipe });
    }

    // Oracle: ConvertTo-MaterializedRecipe. Pure builder, no I/O. Merges the
    // template's recipeDefaults (defensive reads — absent leaves become their
    // empty/false default) with the operator's per-instance inputs, then stamps
    // the server-managed fields. The materialized recipe records its template
    // provenance under createdBy.fromTemplate.
    private static Dictionary<string, object?> ConvertToMaterializedRecipe(
        JsonElement tpl,
        Dictionary<string, object?> body,
        VersionInfo versionInfo,
        string recipeId,
        string nowIso)
    {
        var createdBy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["cookbookVersion"] = versionInfo.CookbookVersion,
            ["bundledPaxVersion"] = versionInfo.PaxVersion,
            ["releaseChannel"] = versionInfo.ReleaseChannel,
            ["fromTemplate"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["templateId"] = TplStr(tpl, "templateId"),
                ["templateVersion"] = TplStr(tpl, "templateVersion"),
            },
        };

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["recipeId"] = recipeId,
            ["recipeSchemaVersion"] = RecipeSchemaVersion,
            ["paxAdapterVersion"] = versionInfo.PaxVersion,
            ["createdAt"] = nowIso,
            ["updatedAt"] = nowIso,
            ["createdBy"] = createdBy,
            ["identity"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = BodyStr(body, "identity", "name"),
            },
            ["ingredients"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["m365Usage"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["includeM365Usage"] = TplBool(tpl, "recipeDefaults", "ingredients", "m365Usage", "includeM365Usage"),
                },
                ["entraUserData"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["includeUserInfo"] = TplBool(tpl, "recipeDefaults", "ingredients", "entraUserData", "includeUserInfo"),
                },
            },
            ["query"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["startDate"] = BodyStr(body, "query", "startDate"),
                ["endDate"] = BodyStr(body, "query", "endDate"),
            },
            ["processing"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["rollup"] = TplStr(tpl, "recipeDefaults", "processing", "rollup"),
            },
            ["destinations"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["fact"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["path"] = BodyStr(body, "destinations", "fact", "path"),
                },
            },
            ["auth"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["mode"] = TplStr(tpl, "recipeDefaults", "auth", "mode"),
                ["tenantId"] = BodyStr(body, "auth", "tenantId"),
            },
        };
    }

    // Oracle: Test-TemplatePaxCompatibility. Returns null when compatible, or an
    // AJV-shaped error when the template's minPaxScriptVersion is greater than
    // the bundled PAX version. Pure semver comparison; blank or unparseable
    // versions are treated as compatible (no fallback, no auto-upgrade).
    private static Dictionary<string, object?>? PaxCompatibilityError(JsonElement tpl, string bundledPaxVersion)
    {
        string required = TplStr(tpl, "minPaxScriptVersion");
        if (string.IsNullOrWhiteSpace(required)) { return null; }

        int[]? req = ParseSemver(required);
        int[]? cur = ParseSemver(bundledPaxVersion);
        if (req is null || cur is null) { return null; }

        for (int i = 0; i < 3; i++)
        {
            if (cur[i] > req[i]) { return null; }
            if (cur[i] < req[i])
            {
                return new Dictionary<string, object?>
                {
                    ["instancePath"] = "/minPaxScriptVersion",
                    ["keyword"] = "paxIncompatible",
                    ["message"] = "template requires bundled PAX >= " + required + " but broker has " + bundledPaxVersion,
                    ["params"] = new Dictionary<string, object?>
                    {
                        ["requiredMin"] = required,
                        ["bundled"] = bundledPaxVersion,
                    },
                };
            }
        }

        return null;
    }

    private static int[]? ParseSemver(string v)
    {
        string[] parts = v.Split('.');
        if (parts.Length != 3) { return null; }
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a)) { return null; }
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b)) { return null; }
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int c)) { return null; }
        return new[] { a, b, c };
    }

    // Navigate the bundled-template JsonElement. Absent / wrong-typed leaves
    // resolve to their empty/false default, matching the oracle's defensive
    // [string]$x / [bool]$x coercions.
    private static bool TryNav(JsonElement root, string[] path, out JsonElement result)
    {
        JsonElement cur = root;
        foreach (string key in path)
        {
            if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(key, out JsonElement next))
            {
                cur = next;
            }
            else
            {
                result = default;
                return false;
            }
        }
        result = cur;
        return true;
    }

    private static string TplStr(JsonElement root, params string[] path)
    {
        if (TryNav(root, path, out JsonElement el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static bool TplBool(JsonElement root, params string[] path)
    {
        if (TryNav(root, path, out JsonElement el))
        {
            if (el.ValueKind == JsonValueKind.True) { return true; }
            if (el.ValueKind == JsonValueKind.False) { return false; }
        }
        return false;
    }

    // Navigate the parsed request body (CLR dictionary tree). Absent leaves
    // resolve to empty string; the body has already passed the materialize body
    // schema, so the required per-instance leaves are present strings.
    private static string BodyStr(Dictionary<string, object?> body, params string[] path)
    {
        object? node = body;
        foreach (string key in path)
        {
            if (node is Dictionary<string, object?> d && d.TryGetValue(key, out object? next))
            {
                node = next;
            }
            else
            {
                return string.Empty;
            }
        }
        return JsonModel.Str(node);
    }

    // Oracle: Write-RecipeFile. Write-temp + atomic rename so a concurrent
    // reader never sees a half-written file. Returns the SHA-256 hash (hex,
    // lowercase) of the final bytes. UTF-8 no BOM.
    private static string WriteRecipeFile(string finalPath, Dictionary<string, object?> recipe)
    {
        byte[] bytes = JsonModel.SerializeToUtf8Bytes(recipe);
        string tempPath = finalPath + ".tmp";

        File.WriteAllBytes(tempPath, bytes);
        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }
        File.Move(tempPath, finalPath);

        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Oracle: Add-RecipeRow for the template materialize path (source='template',
    // source_ref='<templateId>@<templateVersion>'). Opens the workspace SQLite
    // index read-write and inserts exactly one row with the column set the
    // oracle writes.
    private static void AddRecipeRow(
        string workspacePath,
        string recipeId,
        string name,
        string paxAdapterVersion,
        string sourceRef,
        string filePath,
        string fileHash,
        string createdAt,
        string updatedAt)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile(workspacePath),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };

        using var conn = new SqliteConnection(csb.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO recipes
    (recipe_id, name, description, tags_json,
     pax_adapter_version, recipe_schema_version,
     source, source_ref, file_path, file_hash,
     status, is_pinned, created_at, updated_at)
VALUES
    ($recipe_id, $name, NULL, '[]',
     $pax_adapter_version, $recipe_schema_version,
     'template', $source_ref, $file_path, $file_hash,
     'ready', 0, $created_at, $updated_at);";

        cmd.Parameters.AddWithValue("$recipe_id", recipeId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$pax_adapter_version", paxAdapterVersion);
        cmd.Parameters.AddWithValue("$recipe_schema_version", RecipeSchemaVersion);
        cmd.Parameters.AddWithValue("$source_ref", sourceRef);
        cmd.Parameters.AddWithValue("$file_path", filePath);
        cmd.Parameters.AddWithValue("$file_hash", fileHash);
        cmd.Parameters.AddWithValue("$created_at", createdAt);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);

        cmd.ExecuteNonQuery();
    }

    // Oracle: $recipe.identity.name coerced with [string].
    private static string RecipeName(Dictionary<string, object?> recipe)
    {
        if (recipe.TryGetValue("identity", out object? identityNode) &&
            identityNode is Dictionary<string, object?> identity &&
            identity.TryGetValue("name", out object? nameNode))
        {
            return JsonModel.Str(nameNode);
        }
        return string.Empty;
    }

    // Oracle: Get-UtcNowIso -> (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ').
    private static string UtcNowIso() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    // Oracle: New-RecipeId. 128-bit ULID (Crockford base32, 26 chars): 48-bit
    // ms-since-epoch timestamp (10 chars) + 80-bit randomness (16 chars).
    private static readonly char[] UlidAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();

    private static string NewRecipeId()
    {
        long msSinceEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var tsChars = new char[10];
        long v = msSinceEpoch;
        for (int i = 9; i >= 0; i--)
        {
            tsChars[i] = UlidAlphabet[(int)(v & 0x1F)];
            v >>= 5;
        }

        byte[] randBytes = new byte[10];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randBytes);
        }

        var rndChars = new char[16];
        long bitBuf = 0;
        int bitCount = 0;
        int outIdx = 0;
        foreach (byte b in randBytes)
        {
            bitBuf = (bitBuf << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                int idx = (int)((bitBuf >> bitCount) & 0x1F);
                rndChars[outIdx++] = UlidAlphabet[idx];
            }
        }

        return new string(tsChars) + new string(rndChars);
    }
}
