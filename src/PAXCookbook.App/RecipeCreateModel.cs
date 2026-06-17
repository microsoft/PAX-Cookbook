using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Native port of the recipe-create route (Invoke-RecipeCreate in
// app\broker\Routes\Recipes.ps1). This is the first true write slice: it
// validates an incoming recipe with the same validator the oracle uses,
// assigns the server-managed fields, writes exactly one recipe file, and
// inserts exactly one recipe row. It never invokes PAX, never reads or mutates
// the PAX bytes, and never touches cook / scheduler / notification state.
//
// Parity is decision parity, not byte parity: the persisted JSON is produced by
// System.Text.Json rather than PowerShell's ConvertTo-Json, so the on-disk
// bytes differ from the oracle's, but the status codes, error shapes,
// validation decisions, and row/file/hash relationship match the oracle.
//
// Auth gating: the live oracle does NOT gate POST /api/v1/recipes with per-op
// re-auth (only the auth-profile, updates, and cooks routes call
// Invoke-BrokerLockReAuthForOp). To preserve parity, this route is gated only
// by the bearer token, CSRF header, and broker lock that the middleware already
// enforces upstream — no re-auth gate is added here.
internal static class RecipeCreateModel
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

    // Oracle: Invoke-RecipeCreate. Returns (httpStatus, body). The caller emits
    // the body via Results.Json. The recipe object returned in the 201 body is
    // the same value tree that was persisted (with the server-managed fields
    // filled in).
    public static (int Status, object Body) Handle(
        string workspacePath, VersionInfo versionInfo, object? body)
    {
        // Oracle: $body = Read-RequestJson; $null -> 400 invalid_json.
        if (body is not Dictionary<string, object?> recipe)
        {
            Console.WriteLine(
                "[RECIPE-SAVE-DIAG] Rejected: invalid_json \u2014 request body was not a JSON object.");
            return (400, new { error = "invalid_json" });
        }

        // Server assigns recipeId, schema version, bundled-PAX version,
        // timestamps, and the createdBy provenance block. Any client-supplied
        // values for these are overwritten.
        string now = UtcNowIso();
        string id = NewRecipeId();
        recipe["recipeId"] = id;
        recipe["recipeSchemaVersion"] = RecipeSchemaVersion;
        recipe["paxAdapterVersion"] = versionInfo.PaxVersion;
        recipe["createdAt"] = now;
        recipe["updatedAt"] = now;
        recipe["createdBy"] = CreatedByBlock(versionInfo);

        // Oracle: Test-RecipeAll; on failure -> 400 validation_failed { errors }.
        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(recipe);
        if (!ok)
        {
            Console.WriteLine(
                $"[RECIPE-SAVE-DIAG] Rejected: validation_failed \u2014 {errors.Count} validation error(s).");
            return (400, new { error = "validation_failed", errors });
        }

        // Belt-and-suspenders: a bound Chef's Key whose type does not match the
        // recipe's sign-in mode must never be persisted. Reads CK metadata only
        // -- never a secret (constraint 14). The builder already prevents this
        // (it lists only matching keys and clears a bound key on mode switch);
        // this is the last-resort guard for hand-edited imports or a future
        // regression. RecipeValidationModel stays pure (no WCM read), so this
        // route-handler-level check owns the credential-store lookup.
        if (ChefKeyModel.TryGetRecipeModeMismatch(recipe, out string mismatchMode, out string mismatchType))
        {
            Console.WriteLine(
                $"[RECIPE-SAVE-DIAG] Rejected: chef_key_mode_mismatch \u2014 recipe mode {mismatchMode} vs Chef's Key type {mismatchType}.");
            return (400, new
            {
                error = "chef_key_mode_mismatch",
                message = "The bound Chef's Key type does not match this recipe's sign-in mode.",
                recipeMode = mismatchMode,
                chefKeyType = mismatchType,
            });
        }

        // Oracle: Initialize-RecipesDirs creates Recipes\ and Recipes\_trash\.
        Directory.CreateDirectory(RecipesDir(workspacePath));
        Directory.CreateDirectory(RecipesTrashDir(workspacePath));

        // File-first, row-second. On row-insert failure, delete the file so the
        // two never diverge.
        string finalPath = RecipeFilePath(workspacePath, id);
        string fileHash = WriteRecipeFile(finalPath, recipe);

        try
        {
            AddRecipeRow(
                workspacePath,
                recipeId: id,
                name: RecipeName(recipe),
                paxAdapterVersion: versionInfo.PaxVersion,
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
            Console.WriteLine(
                $"[RECIPE-SAVE-DIAG] Rejected: persist_failed \u2014 {ex.Message}");
            return (500, new
            {
                error = "persist_failed",
                message = "The recipe index row could not be written; the recipe file was rolled back.",
                detail = ex.Message,
            });
        }

        return (201, new { recipeId = id, recipe });
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

    // Oracle: Add-RecipeRow for the 'new' create path (source='new',
    // source_ref=NULL). Opens the workspace SQLite index read-write and inserts
    // exactly one row with the same column set the oracle writes.
    private static void AddRecipeRow(
        string workspacePath,
        string recipeId,
        string name,
        string paxAdapterVersion,
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
     'new', NULL, $file_path, $file_hash,
     'ready', 0, $created_at, $updated_at);";

        cmd.Parameters.AddWithValue("$recipe_id", recipeId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$pax_adapter_version", paxAdapterVersion);
        cmd.Parameters.AddWithValue("$recipe_schema_version", RecipeSchemaVersion);
        cmd.Parameters.AddWithValue("$file_path", filePath);
        cmd.Parameters.AddWithValue("$file_hash", fileHash);
        cmd.Parameters.AddWithValue("$created_at", createdAt);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);

        cmd.ExecuteNonQuery();
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

    // Oracle: Get-RecipeCreatedByBlock. Provenance sourced from authoritative
    // version metadata (VERSION.json), populated at broker startup.
    private static Dictionary<string, object?> CreatedByBlock(VersionInfo v) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cookbookVersion"] = v.CookbookVersion,
            ["bundledPaxVersion"] = v.PaxVersion,
            ["releaseChannel"] = v.ReleaseChannel,
        };

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
