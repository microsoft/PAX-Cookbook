using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B1 -- recipe file snapshot store.
//
// Ports the file-side helpers from app\broker\Routes\Recipes.ps1
// (Write-RecipeFile, Read-RecipeFile, Get-RecipeFilePath,
// Get-RecipeTrashFilePath, Initialize-RecipesDirs) plus the
// rollback bytes capture used by Invoke-RecipeUpdate /
// Invoke-RecipeDelete.
//
// Atomic write contract:
//   1. WriteAllBytes -> <id>.recipe.json.tmp
//   2. Remove final if it exists
//   3. Move .tmp -> final
//   4. Hash final bytes -> lowercase hex SHA-256
//
// Trash contract:
//   * Initialize-RecipesDirs ensures Recipes/ and Recipes/_trash/ exist
//     before either path is touched. Mirrors PS semantics so create-
//     into-trash never fails on a fresh workspace.
//   * Trash filename: <id>.recipe.<stamp>.json where stamp is
//     ISO-8601 UTC with ":", "-", "." stripped (matches PS line 743).
//
// The class is sealed + stateless; the workspace root is captured at
// construction so callers don't pass it on every call.
public sealed class RecipeSnapshotStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly string _recipesDir;
    private readonly string _trashDir;

    public RecipeSnapshotStore(string workspaceFolderPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceFolderPath))
            throw new ArgumentException("workspaceFolderPath is required", nameof(workspaceFolderPath));
        _recipesDir = Path.Combine(workspaceFolderPath, "Recipes");
        _trashDir   = Path.Combine(_recipesDir, "_trash");
    }

    public string RecipesDir => _recipesDir;
    public string TrashDir   => _trashDir;

    public string GetRecipeFilePath(string recipeId) =>
        Path.Combine(_recipesDir, recipeId + ".recipe.json");

    public string GetRecipeTrashFilePath(string recipeId, string stamp) =>
        Path.Combine(_trashDir, recipeId + ".recipe." + stamp + ".json");

    public void EnsureDirs()
    {
        Directory.CreateDirectory(_recipesDir);
        Directory.CreateDirectory(_trashDir);
    }

    // Returns the lowercase hex SHA-256 of the final on-disk bytes
    // (matches PS Write-RecipeFile).
    public RecipeSnapshotWriteResult Write(string recipeId, JsonObject recipe)
    {
        EnsureDirs();
        var finalPath = GetRecipeFilePath(recipeId);
        var tempPath  = finalPath + ".tmp";

        // PS uses ConvertTo-Json -Depth 12 which indents by default.
        // We pretty-print with System.Text.Json's WriteIndented for
        // hand-editing parity. The hash is computed over the file
        // bytes so any caller that reads back through Read() sees the
        // same bytes that were hashed.
        var json = recipe.ToJsonString(IndentedJson);
        var bytes = Utf8NoBom.GetBytes(json);
        File.WriteAllBytes(tempPath, bytes);
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tempPath, finalPath);

        var hash = HashFile(finalPath);
        return new RecipeSnapshotWriteResult(finalPath, hash);
    }

    public byte[]? ReadRawBytes(string recipeId)
    {
        var path = GetRecipeFilePath(recipeId);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void RestoreRawBytes(string recipeId, byte[] bytes)
    {
        var path = GetRecipeFilePath(recipeId);
        File.WriteAllBytes(path, bytes);
    }

    // Mirrors PS Read-RecipeFile's four-status envelope.
    public RecipeFileLoad Read(string recipeId, int supportedSchemaVersion)
    {
        var path = GetRecipeFilePath(recipeId);
        if (!File.Exists(path))
            return new RecipeFileLoad(RecipeFileStatus.Missing, null, null);

        string text;
        try
        {
            text = File.ReadAllText(path, Utf8NoBom);
        }
        catch (IOException ex)
        {
            return new RecipeFileLoad(RecipeFileStatus.Malformed, null, ex.Message);
        }

        JsonObject obj;
        try
        {
            var parsed = JsonNode.Parse(text);
            if (parsed is not JsonObject jo)
                return new RecipeFileLoad(RecipeFileStatus.Malformed, null, "root is not a JSON object");
            obj = jo;
        }
        catch (JsonException ex)
        {
            return new RecipeFileLoad(RecipeFileStatus.Malformed, null, ex.Message);
        }

        int? observed = null;
        if (obj.TryGetPropertyValue("recipeSchemaVersion", out var sv) && sv is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) observed = i;
            else if (v.TryGetValue<long>(out var l) && l <= int.MaxValue) observed = (int)l;
        }
        if (observed != supportedSchemaVersion)
        {
            return new RecipeFileLoad(
                RecipeFileStatus.UnsupportedSchemaVersion,
                obj,
                "observed=" + (observed?.ToString() ?? "<missing>"));
        }
        return new RecipeFileLoad(RecipeFileStatus.Ok, obj, null);
    }

    // Move final -> trash. Caller computes the stamp.
    public bool MoveToTrash(string recipeId, string stamp, out string trashPath)
    {
        EnsureDirs();
        var finalPath = GetRecipeFilePath(recipeId);
        trashPath = GetRecipeTrashFilePath(recipeId, stamp);
        if (!File.Exists(finalPath)) return false;
        if (File.Exists(trashPath)) File.Delete(trashPath);
        File.Move(finalPath, trashPath);
        return true;
    }

    // Rollback partner for MoveToTrash.
    public void MoveFromTrash(string trashPath, string recipeId)
    {
        var finalPath = GetRecipeFilePath(recipeId);
        if (!File.Exists(trashPath)) return;
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(trashPath, finalPath);
    }

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
