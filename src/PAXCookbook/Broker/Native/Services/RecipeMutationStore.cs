using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B1 -- recipes table mutation surface (INSERT / UPDATE /
// UPDATE...deleted_at). Ports Add-RecipeRow / Update-RecipeRow /
// Set-RecipeRowDeleted from Recipes.ps1.
//
// Connections are per-call, ReadWrite mode. Matches the per-call
// lifecycle Stage 3g's ScheduledTaskStore uses for the same database.
public sealed class RecipeMutationStore
{
    private readonly string _connectionString;

    public RecipeMutationStore(string databaseFilePath)
    {
        if (string.IsNullOrWhiteSpace(databaseFilePath))
            throw new ArgumentException("databaseFilePath is required", nameof(databaseFilePath));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFilePath,
            Mode       = SqliteOpenMode.ReadWrite,
        }.ToString();
    }

    // Returns true if the row exists and is not soft-deleted.
    public RecipeMetaSummary? GetActiveRow(string recipeId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT recipe_id, name, file_path, file_hash, pax_adapter_version,
       recipe_schema_version, source, source_ref,
       created_at, updated_at, deleted_at
FROM recipes
WHERE recipe_id = $id;";
        cmd.Parameters.AddWithValue("$id", recipeId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var deletedAt = reader.IsDBNull(10) ? null : reader.GetString(10);
        if (deletedAt is not null) return null; // soft-deleted -> 404
        return new RecipeMetaSummary(
            RecipeId:            reader.GetString(0),
            Name:                reader.GetString(1),
            FilePath:            reader.GetString(2),
            FileHash:            reader.GetString(3),
            PaxAdapterVersion:   reader.GetString(4),
            RecipeSchemaVersion: reader.GetInt32(5),
            Source:              reader.GetString(6),
            SourceRef:           reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt:           reader.GetString(8),
            UpdatedAt:           reader.GetString(9));
    }

    public void Insert(RecipeInsertRow row)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO recipes
    (recipe_id, name, description, tags_json,
     pax_adapter_version, recipe_schema_version,
     source, source_ref, file_path, file_hash,
     status, is_pinned, created_at, updated_at)
VALUES
    ($recipe_id, $name, NULL, '[]',
     $pax_adapter_version, $recipe_schema_version,
     $source, $source_ref, $file_path, $file_hash,
     'ready', 0, $created_at, $updated_at);";
        cmd.Parameters.AddWithValue("$recipe_id",             row.RecipeId);
        cmd.Parameters.AddWithValue("$name",                  row.Name);
        cmd.Parameters.AddWithValue("$pax_adapter_version",   row.PaxAdapterVersion);
        cmd.Parameters.AddWithValue("$recipe_schema_version", row.RecipeSchemaVersion);
        cmd.Parameters.AddWithValue("$source",                row.Source);
        cmd.Parameters.AddWithValue("$source_ref",            (object?)row.SourceRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$file_path",             row.FilePath);
        cmd.Parameters.AddWithValue("$file_hash",             row.FileHash);
        cmd.Parameters.AddWithValue("$created_at",            row.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at",            row.UpdatedAt);
        cmd.ExecuteNonQuery();
    }

    // Returns affected row count (expected: 1).
    public int UpdateNameHashTimestamp(string recipeId, string name, string fileHash, string updatedAt)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE recipes
SET name = $name,
    file_hash = $file_hash,
    status = 'ready',
    updated_at = $updated_at
WHERE recipe_id = $recipe_id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$name",       name);
        cmd.Parameters.AddWithValue("$file_hash",  fileHash);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);
        cmd.Parameters.AddWithValue("$recipe_id",  recipeId);
        return cmd.ExecuteNonQuery();
    }

    public int SetDeletedAt(string recipeId, string deletedAt)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE recipes SET deleted_at = $deleted_at WHERE recipe_id = $recipe_id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$deleted_at", deletedAt);
        cmd.Parameters.AddWithValue("$recipe_id",  recipeId);
        return cmd.ExecuteNonQuery();
    }

    // Used for test-side rollback verification only -- production
    // paths never call this. Removes the row outright when an INSERT
    // succeeded but the subsequent file flush blew up.
    public int DeleteRow(string recipeId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM recipes WHERE recipe_id = $recipe_id;";
        cmd.Parameters.AddWithValue("$recipe_id", recipeId);
        return cmd.ExecuteNonQuery();
    }
}

public sealed record RecipeMetaSummary(
    string  RecipeId,
    string  Name,
    string  FilePath,
    string  FileHash,
    string  PaxAdapterVersion,
    int     RecipeSchemaVersion,
    string  Source,
    string? SourceRef,
    string  CreatedAt,
    string  UpdatedAt);
