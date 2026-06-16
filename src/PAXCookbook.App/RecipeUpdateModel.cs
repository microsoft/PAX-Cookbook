using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Native port of the recipe-update route (Invoke-RecipeUpdate in
// app\broker\Routes\Recipes.ps1). PUT /api/v1/recipes/{id} replaces the body of
// an existing, non-deleted recipe: it re-validates with the same validator the
// oracle uses, preserves the server-owned provenance leaves, writes the recipe
// file in place, and updates exactly one recipe row. It never invokes PAX,
// never reads or mutates the PAX bytes, and never touches cook / scheduler /
// notification state.
//
// Parity is decision parity, not byte parity: the persisted JSON is produced by
// System.Text.Json rather than PowerShell's ConvertTo-Json, so the on-disk
// bytes differ from the oracle's, but the status codes, error shapes,
// validation decisions, and row/file/hash relationship match the oracle.
//
// Auth gating: the live oracle does NOT gate PUT /api/v1/recipes/{id} with
// per-op re-auth — Invoke-RecipesRoute dispatches straight to Invoke-RecipeUpdate
// with no Invoke-BrokerLockReAuthForOp call (re-auth is reserved for the
// auth-profile and scheduled-task routes). To preserve parity, this route is
// gated only by the bearer token, CSRF header, and broker lock that the
// middleware already enforces upstream — no re-auth gate is added here.
internal static class RecipeUpdateModel
{
    // Oracle parity: $Script:M1_RecipeSchemaVer (the single supported version).
    private const long RecipeSchemaVersion = 1L;
    private const int SupportedSchemaVersion = 1;

    private static string RecipeFilePath(string workspacePath, string recipeId) =>
        Path.Combine(workspacePath, "Recipes", recipeId + ".recipe.json");

    private static string DatabaseFile(string workspacePath) =>
        Path.Combine(workspacePath, "Database", "cookbook.sqlite");

    // Oracle: Invoke-RecipeUpdate. Returns (httpStatus, body). The recipe-id
    // format is validated by the caller before this is invoked.
    public static (int Status, object Body) Handle(
        string workspacePath, VersionInfo versionInfo, string recipeId, object? body)
    {
        // Oracle: Get-RecipeRow; not found OR soft-deleted -> 404 not_found.
        // This precedes the body parse so a missing row wins over a bad body.
        RowSnapshot? row = GetRow(workspacePath, recipeId);
        if (row is null || row.Value.DeletedAt is not null)
        {
            return (404, new { error = "not_found" });
        }

        // Oracle: $body = Read-RequestJson; $null -> 400 invalid_json.
        if (body is not Dictionary<string, object?> recipe)
        {
            return (400, new { error = "invalid_json" });
        }

        // Oracle: if $body.recipeId present AND != url id -> 400 id_mismatch.
        if (recipe.ContainsKey("recipeId"))
        {
            string bodyRecipeId = JsonModel.Str(recipe["recipeId"]);
            if (bodyRecipeId != recipeId)
            {
                return (400, new
                {
                    error = "id_mismatch",
                    urlRecipeId = recipeId,
                    bodyRecipeId,
                });
            }
        }

        // Oracle: Read-RecipeFile; the on-disk document is authoritative for the
        // preserved provenance leaves and gates the same load-side 422 / 500
        // vocabulary the GET detail path emits.
        RecipeReadModel.RecipeTreeLoad existing =
            RecipeReadModel.LoadRecipeTree(workspacePath, recipeId);
        switch (existing.Status)
        {
            case "ok":
                break;
            case "missing":
                return (422, new { error = "recipe_file_missing", recipeId });
            case "malformed":
                return (422, new { error = "recipe_file_malformed", recipeId, detail = existing.Detail });
            case "unsupported_schema_version":
                return (422, new
                {
                    error = "recipe_unsupported_schema_version",
                    recipeId,
                    supportedSchemaVersion = SupportedSchemaVersion,
                    detail = existing.Detail,
                });
            default:
                return (500, new { error = "recipe_load_unknown_status", recipeId, status = existing.Status });
        }

        Dictionary<string, object?>? existingRecipe = existing.Recipe;

        // Server-owned fields. recipeId / schema version / bundled-PAX version
        // are re-asserted; createdAt is preserved from the index row; updatedAt
        // is stamped now.
        string now = UtcNowIso();
        recipe["recipeId"] = recipeId;
        recipe["recipeSchemaVersion"] = RecipeSchemaVersion;
        recipe["paxAdapterVersion"] = versionInfo.PaxVersion;
        recipe["createdAt"] = row.Value.CreatedAt;
        recipe["updatedAt"] = now;

        // createdBy: preserved verbatim from the existing document when present;
        // otherwise any client-supplied createdBy is dropped (never inferred).
        if (existingRecipe is not null && existingRecipe.ContainsKey("createdBy"))
        {
            recipe["createdBy"] = existingRecipe["createdBy"];
        }
        else
        {
            recipe.Remove("createdBy");
        }

        // importMetadata: same preserve-or-drop discipline as createdBy.
        if (existingRecipe is not null && existingRecipe.ContainsKey("importMetadata"))
        {
            recipe["importMetadata"] = existingRecipe["importMetadata"];
        }
        else
        {
            recipe.Remove("importMetadata");
        }

        // Oracle: Test-RecipeAll; on failure -> 400 validation_failed { errors }.
        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(recipe);
        if (!ok)
        {
            return (400, new { error = "validation_failed", errors });
        }

        // Belt-and-suspenders: a bound Chef's Key whose type does not match the
        // recipe's sign-in mode must never be persisted. Reads CK metadata only
        // -- never a secret (constraint 14). The builder already prevents this;
        // this is the last-resort guard for hand-edited imports or a future
        // regression. RecipeValidationModel stays pure (no WCM read), so this
        // route-handler-level check owns the credential-store lookup.
        if (ChefKeyModel.TryGetRecipeModeMismatch(recipe, out string mismatchMode, out string mismatchType))
        {
            return (400, new
            {
                error = "chef_key_mode_mismatch",
                message = "The bound Chef's Key type does not match this recipe's sign-in mode.",
                recipeMode = mismatchMode,
                chefKeyType = mismatchType,
            });
        }

        // File-first with byte-level rollback. Capture the current on-disk bytes
        // so a row-update failure restores the prior file exactly.
        string finalPath = RecipeFilePath(workspacePath, recipeId);
        byte[]? oldBytes = File.Exists(finalPath) ? File.ReadAllBytes(finalPath) : null;

        string fileHash = WriteRecipeFile(finalPath, recipe);

        try
        {
            int affected = UpdateRecipeRow(
                workspacePath,
                recipeId: recipeId,
                name: RecipeName(recipe),
                fileHash: fileHash,
                updatedAt: now);

            if (affected != 1)
            {
                RestoreFile(finalPath, oldBytes);
                return (500, new
                {
                    error = "persist_failed",
                    message = "The recipe index row could not be updated; the recipe file was rolled back.",
                });
            }
        }
        catch (Exception ex)
        {
            RestoreFile(finalPath, oldBytes);
            return (500, new
            {
                error = "persist_failed",
                message = "The recipe index row could not be updated; the recipe file was rolled back.",
                detail = ex.Message,
            });
        }

        return (200, new { recipeId, recipe });
    }

    private readonly record struct RowSnapshot(string CreatedAt, string? DeletedAt);

    // Oracle: Get-RecipeRow (no deleted_at filter — the caller inspects
    // deleted_at). Opens the workspace index read-write so the subsequent
    // UPDATE runs against the same database; only created_at and deleted_at are
    // needed here.
    private static RowSnapshot? GetRow(string workspacePath, string recipeId)
    {
        string dbFile = DatabaseFile(workspacePath);
        if (!File.Exists(dbFile))
        {
            return null;
        }

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbFile,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };

        using var conn = new SqliteConnection(csb.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT created_at, deleted_at FROM recipes WHERE recipe_id = $id;";
        cmd.Parameters.AddWithValue("$id", recipeId);

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        string createdAt = reader.GetString(0);
        string? deletedAt = reader.IsDBNull(1) ? null : reader.GetString(1);
        return new RowSnapshot(createdAt, deletedAt);
    }

    // Oracle: Update-RecipeRow. UPDATE name, file_hash, status='ready',
    // updated_at WHERE recipe_id AND deleted_at IS NULL. created_at, createdBy,
    // source, and source_ref are write-once and intentionally untouched.
    private static int UpdateRecipeRow(
        string workspacePath, string recipeId, string name, string fileHash, string updatedAt)
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
        cmd.CommandText =
            "UPDATE recipes SET name = $name, file_hash = $file_hash, " +
            "status = 'ready', updated_at = $updated_at " +
            "WHERE recipe_id = $recipe_id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$file_hash", fileHash);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);
        cmd.Parameters.AddWithValue("$recipe_id", recipeId);

        return cmd.ExecuteNonQuery();
    }

    // Oracle: Write-RecipeFile. Write-temp + atomic rename; returns the SHA-256
    // hash (hex, lowercase) of the final bytes. UTF-8 no BOM.
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

    // Restores the recipe file to its prior bytes (rollback). When the file did
    // not previously exist, the just-written file is removed.
    private static void RestoreFile(string finalPath, byte[]? oldBytes)
    {
        try
        {
            if (oldBytes is null)
            {
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }
            }
            else
            {
                File.WriteAllBytes(finalPath, oldBytes);
            }
        }
        catch
        {
            // Best-effort rollback; the failure is already being reported as 500.
        }
    }

    // Oracle: $body.identity.name coerced with [string].
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
}
