using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3g -- read + restricted-write gateway for the scheduled_tasks
// table and the cook-row reads the V1.S07 health composer needs.
// Mirrors the SQL in app\broker\Routes\ScheduledTasks.ps1's row I/O
// helpers verbatim:
//
//   * Get-ScheduledTaskRow             -> GetByRecipeIdAsync
//   * Get-ScheduledTaskRowsAll         -> GetAllAsync (LEFT JOIN recipes)
//   * Update-ScheduledTaskStaleCheck   -> UpdateStaleCheckAsync
//   * Set-ScheduledTaskRow             -> UpsertAsync       (Stage 3h-ready)
//   * Remove-ScheduledTaskRow          -> DeleteByRecipeIdAsync (Stage 3h-ready)
//   * Get-ScheduledTaskLastTerminalCook -> GetLastTerminalCookAsync
//   * Test-ScheduledTaskHasRunningCook  -> HasRunningCookAsync
//
// Doctrine:
//   * Per-call connection. UpdateStaleCheckAsync / UpsertAsync /
//     DeleteByRecipeIdAsync open Mode=ReadWrite; reads open
//     Mode=ReadWrite (NOT ReadOnly) so they participate in the same
//     workspace database the production PS broker writes. Stage 3g
//     does NOT introduce schema migrations -- the table is created
//     by Start-Broker.ps1.
//   * All SQL is parameterised; no string concatenation of caller
//     input.
//   * Missing DB file -> SqliteException at Open(); callers catch it
//     and surface the route-layer "workspace_database_unavailable"
//     sentinel.
public sealed class ScheduledTaskStore
{
    private readonly string _databaseFile;

    public ScheduledTaskStore(string databaseFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFile);
        _databaseFile = databaseFile;
    }

    public string DatabaseFile => _databaseFile;

    public bool DatabaseFileExists() => File.Exists(_databaseFile);

    // ---------------- Row I/O (read) ----------------

    public ScheduledTaskRow? GetByRecipeId(string recipeId)
    {
        if (!DatabaseFileExists()) return null;
        try
        {
            using var conn = Open(SqliteOpenMode.ReadWrite);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT scheduled_task_id, recipe_id, windows_task_name, windows_task_path,
       recipe_projection_hash, pax_script_version, registered_at,
       registered_by_user, last_imported_cook_id, last_imported_at,
       last_stale_check_at, status, created_at, updated_at
FROM scheduled_tasks
WHERE recipe_id = $rid;";
            cmd.Parameters.AddWithValue("$rid", recipeId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new ScheduledTaskRow(
                ScheduledTaskId:      r.GetString(0),
                RecipeId:             r.GetString(1),
                WindowsTaskName:      r.GetString(2),
                WindowsTaskPath:      r.GetString(3),
                RecipeProjectionHash: r.GetString(4),
                PaxScriptVersion:     r.GetString(5),
                RegisteredAt:         r.GetString(6),
                RegisteredByUser:     r.GetString(7),
                LastImportedCookId:   GetNullableString(r, 8),
                LastImportedAt:       GetNullableString(r, 9),
                LastStaleCheckAt:     GetNullableString(r, 10),
                Status:               r.GetString(11),
                CreatedAt:            r.GetString(12),
                UpdatedAt:            r.GetString(13));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public IReadOnlyList<ScheduledTaskListRow>? TryGetAll()
    {
        if (!DatabaseFileExists()) return null;
        try
        {
            using var conn = Open(SqliteOpenMode.ReadWrite);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT s.scheduled_task_id, s.recipe_id, s.windows_task_name, s.windows_task_path,
       s.recipe_projection_hash, s.pax_script_version, s.registered_at,
       s.registered_by_user, s.last_imported_cook_id, s.last_imported_at,
       s.last_stale_check_at, s.status, s.created_at, s.updated_at,
       r.name AS recipe_name, r.deleted_at AS recipe_deleted_at
FROM scheduled_tasks s
LEFT JOIN recipes r ON r.recipe_id = s.recipe_id
ORDER BY s.registered_at DESC, s.scheduled_task_id ASC;";
            var list = new List<ScheduledTaskListRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new ScheduledTaskListRow(
                    ScheduledTaskId:      r.GetString(0),
                    RecipeId:             r.GetString(1),
                    WindowsTaskName:      r.GetString(2),
                    WindowsTaskPath:      r.GetString(3),
                    RecipeProjectionHash: r.GetString(4),
                    PaxScriptVersion:     r.GetString(5),
                    RegisteredAt:         r.GetString(6),
                    RegisteredByUser:     r.GetString(7),
                    LastImportedCookId:   GetNullableString(r, 8),
                    LastImportedAt:       GetNullableString(r, 9),
                    LastStaleCheckAt:     GetNullableString(r, 10),
                    Status:               r.GetString(11),
                    CreatedAt:            r.GetString(12),
                    UpdatedAt:            r.GetString(13),
                    RecipeName:           GetNullableString(r, 14),
                    RecipeDeletedAt:      GetNullableString(r, 15)));
            }
            return list;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    // ---------------- Row I/O (write) ----------------

    // Update last_stale_check_at + updated_at on an existing row.
    // Parity with Update-ScheduledTaskStaleCheck. Idempotent. Returns
    // affected row count (0 when no row exists for the recipe).
    public int UpdateStaleCheck(string recipeId, string nowIso)
    {
        if (!DatabaseFileExists()) return 0;
        try
        {
            using var conn = Open(SqliteOpenMode.ReadWrite);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE scheduled_tasks
SET last_stale_check_at = $now, updated_at = $now
WHERE recipe_id = $rid;";
            cmd.Parameters.AddWithValue("$now", nowIso);
            cmd.Parameters.AddWithValue("$rid", recipeId);
            return cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    // Stage-3h-ready: INSERT ... ON CONFLICT(recipe_id) DO UPDATE
    // pattern. The PUT route does NOT call this in Stage 3g (it
    // returns 501). Provided so tests can pre-seed rows and so the
    // upsert SQL is review-ready when Stage 3h activates PUT.
    public int Upsert(
        string scheduledTaskId,
        string recipeId,
        string windowsTaskName,
        string windowsTaskPath,
        string recipeProjectionHash,
        string paxScriptVersion,
        string nowIso,
        string registeredByUser)
    {
        using var conn = Open(SqliteOpenMode.ReadWrite);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO scheduled_tasks (
    scheduled_task_id, recipe_id, windows_task_name, windows_task_path,
    recipe_projection_hash, pax_script_version,
    registered_at, registered_by_user, status, created_at, updated_at
) VALUES (
    $stid, $rid, $tname, $tpath,
    $hash, $pver,
    $now, $usr, 'active', $now, $now
)
ON CONFLICT(recipe_id) DO UPDATE SET
    windows_task_name       = excluded.windows_task_name,
    windows_task_path       = excluded.windows_task_path,
    recipe_projection_hash  = excluded.recipe_projection_hash,
    pax_script_version      = excluded.pax_script_version,
    registered_at           = excluded.registered_at,
    registered_by_user      = excluded.registered_by_user,
    status                  = 'active',
    updated_at              = excluded.updated_at;";
        cmd.Parameters.AddWithValue("$stid",  scheduledTaskId);
        cmd.Parameters.AddWithValue("$rid",   recipeId);
        cmd.Parameters.AddWithValue("$tname", windowsTaskName);
        cmd.Parameters.AddWithValue("$tpath", windowsTaskPath);
        cmd.Parameters.AddWithValue("$hash",  recipeProjectionHash);
        cmd.Parameters.AddWithValue("$pver",  paxScriptVersion);
        cmd.Parameters.AddWithValue("$now",   nowIso);
        cmd.Parameters.AddWithValue("$usr",   registeredByUser);
        return cmd.ExecuteNonQuery();
    }

    // Stage-3h-ready: DELETE by recipe_id. Not invoked by Stage 3g
    // DELETE route (returns 501). Provided so the abstraction is
    // ready and tests can exercise the path end-to-end with
    // FakeScheduledTaskRegistrar.
    public int DeleteByRecipeId(string recipeId)
    {
        using var conn = Open(SqliteOpenMode.ReadWrite);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scheduled_tasks WHERE recipe_id = $rid;";
        cmd.Parameters.AddWithValue("$rid", recipeId);
        return cmd.ExecuteNonQuery();
    }

    // ---------------- Cook-row reads for V1.S07 health composer ----------------

    // Mirrors Get-ScheduledTaskLastTerminalCook verbatim. Returns the
    // most recent terminal scheduled cook for the given scheduledTaskId
    // (status in completed / failed / refused / interrupted), ordered
    // by COALESCE(finished_at, started_at) DESC then cook_id DESC.
    public ScheduledTaskTerminalCook? GetLastTerminalCook(string scheduledTaskId)
    {
        if (!DatabaseFileExists()) return null;
        try
        {
            using var conn = Open(SqliteOpenMode.ReadWrite);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT cook_id, status, error_class, exit_code, started_at, finished_at
FROM cooks
WHERE schedule_id = $sid
  AND trigger = 'scheduled'
  AND status IN ('completed','failed','refused','interrupted')
ORDER BY COALESCE(finished_at, started_at) DESC, cook_id DESC
LIMIT 1;";
            cmd.Parameters.AddWithValue("$sid", scheduledTaskId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new ScheduledTaskTerminalCook(
                CookId:     r.GetString(0),
                Status:     r.GetString(1),
                ErrorClass: GetNullableString(r, 2),
                ExitCode:   r.IsDBNull(3) ? null : r.GetInt32(3),
                StartedAt:  GetNullableString(r, 4),
                FinishedAt: GetNullableString(r, 5));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    // Mirrors Test-ScheduledTaskHasRunningCook verbatim. Returns true
    // iff any scheduled cook for this task is currently in status
    // 'running'.
    public bool HasRunningCook(string scheduledTaskId)
    {
        if (!DatabaseFileExists()) return false;
        try
        {
            using var conn = Open(SqliteOpenMode.ReadWrite);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT COUNT(1) FROM cooks
WHERE schedule_id = $sid
  AND trigger = 'scheduled'
  AND status = 'running';";
            cmd.Parameters.AddWithValue("$sid", scheduledTaskId);
            var n = cmd.ExecuteScalar();
            return n is not null && Convert.ToInt32(n) > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    // ---------------- Internals ----------------

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _databaseFile,
            Mode       = mode,
            Cache      = SqliteCacheMode.Private,
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    private static string? GetNullableString(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
}
