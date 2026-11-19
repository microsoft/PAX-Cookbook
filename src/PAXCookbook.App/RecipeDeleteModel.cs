using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Native port of the recipe-delete route (Invoke-RecipeDelete in
// app\broker\Routes\Recipes.ps1). DELETE /api/v1/recipes/{id} is a soft delete:
// the recipe file is moved into Recipes\_trash with a timestamped name, the
// index row's deleted_at is stamped, and the row is retained. It never invokes
// PAX or touches cook or notification state. A stored task binding must be
// unregistered before the delete is persisted.
//
// Auth gating: the live oracle does NOT gate DELETE /api/v1/recipes/{id} with
// per-op re-auth — Invoke-RecipesRoute dispatches straight to Invoke-RecipeDelete
// with no Invoke-BrokerLockReAuthForOp call. To preserve parity, this route is
// gated only by the bearer token, CSRF header, and broker lock that the
// middleware already enforces upstream — no re-auth gate is added here.
internal static class RecipeDeleteModel
{
    private static string RecipesDir(string workspacePath) =>
        Path.Combine(workspacePath, "Recipes");

    private static string RecipesTrashDir(string workspacePath) =>
        Path.Combine(workspacePath, "Recipes", "_trash");

    private static string RecipeFilePath(string workspacePath, string recipeId) =>
        Path.Combine(RecipesDir(workspacePath), recipeId + ".recipe.json");

    private static string RecipeTrashFilePath(string workspacePath, string recipeId, string timestamp) =>
        Path.Combine(RecipesTrashDir(workspacePath), recipeId + ".recipe." + timestamp + ".json");

    private static string DatabaseFile(string workspacePath) =>
        Path.Combine(workspacePath, "Database", "cookbook.sqlite");

    // Oracle: Invoke-RecipeDelete. Returns (httpStatus, body). The recipe-id
    // format is validated by the caller before this is invoked.
    public static (int Status, object Body) Handle(string workspacePath, string appRoot, string recipeId,
        string? registrarPathOverride = null, string? pwshOverride = null)
    {
        lock (RecipeScheduleModel.GetMutationGate(workspacePath, recipeId))
        {
            return HandleCore(workspacePath, appRoot, recipeId, registrarPathOverride, pwshOverride);
        }
    }

    private static (int Status, object Body) HandleCore(string workspacePath, string appRoot, string recipeId,
        string? registrarPathOverride, string? pwshOverride)
    {
        // Oracle: Get-RecipeRow; not found OR already soft-deleted -> 404.
        if (!RowIsLive(workspacePath, recipeId))
        {
            return (404, new { error = "not_found" });
        }

        var stored = RecipeReadModel.LoadRecipeTree(workspacePath, recipeId);
        if (stored.Status == "malformed")
        {
            return (422, new { error = "recipe_file_malformed", recipeId });
        }
        string? taskId = stored.Recipe is { } recipe ? RecipeReadModel.ProjectSchedule(recipe)?.ScheduledTaskId : null;
        bool hasTask = !string.IsNullOrWhiteSpace(taskId);
        if (hasTask && RecipeScheduleModel.TryUnregisterTaskForRecipe(workspacePath, appRoot, recipeId,
            taskId, registrarPathOverride, taskFolderOverride: null, pwshOverride) is { } refusal)
        {
            return refusal;
        }

        // Oracle: $now = Get-UtcNowIso; $stamp = $now without ':' '-' '.'.
        string now = UtcNowIso();
        string stamp = now.Replace(":", string.Empty).Replace("-", string.Empty).Replace(".", string.Empty);

        string filePath = RecipeFilePath(workspacePath, recipeId);
        string trashPath = RecipeTrashFilePath(workspacePath, recipeId, stamp);

        bool fileMoved = false;
        try
        {
            Directory.CreateDirectory(RecipesTrashDir(workspacePath));
            if (File.Exists(filePath))
            {
                File.Move(filePath, trashPath, overwrite: true);
                fileMoved = true;
            }
            if (SetRowDeleted(workspacePath, recipeId, now) != 1)
            {
                return DeleteFailed();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return DeleteFailed();
        }

        if (hasTask && !ScheduleSkipMarker.TryClear(workspacePath, recipeId))
        {
            return (500, new { error = "schedule_skip_marker_clear_failed", recipeId, recipeDeleted = true, scheduleUnregistered = true });
        }

        return (200, new { recipeId, deletedAt = now, trashPath });

        (int Status, object Body) DeleteFailed()
        {
            if (fileMoved && File.Exists(trashPath))
            {
                try { File.Move(trashPath, filePath, overwrite: true); } catch { /* best-effort rollback */ }
            }
            return (500, hasTask ? (object)new { error = "delete_failed", scheduleUnregistered = true } : new { error = "delete_failed" });
        }
    }

    // Oracle: Get-RecipeRow + (-not $row -or $row.deleted_at) test. Returns true
    // only when the row exists and is not already soft-deleted.
    private static bool RowIsLive(string workspacePath, string recipeId)
    {
        string dbFile = DatabaseFile(workspacePath);
        if (!File.Exists(dbFile))
        {
            return false;
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
        cmd.CommandText = "SELECT deleted_at FROM recipes WHERE recipe_id = $id;";
        cmd.Parameters.AddWithValue("$id", recipeId);

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }
        return reader.IsDBNull(0);
    }

    // Oracle: Set-RecipeRowDeleted. UPDATE deleted_at WHERE recipe_id AND
    // deleted_at IS NULL. Returns the affected-row count.
    private static int SetRowDeleted(string workspacePath, string recipeId, string deletedAt)
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
            "UPDATE recipes SET deleted_at = $deleted_at " +
            "WHERE recipe_id = $recipe_id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$deleted_at", deletedAt);
        cmd.Parameters.AddWithValue("$recipe_id", recipeId);

        return cmd.ExecuteNonQuery();
    }

    // Oracle: Get-UtcNowIso -> (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ').
    private static string UtcNowIso() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
