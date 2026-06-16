using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3e -- writer side of the cooks table. SqliteWorkspaceReader
// is strict ReadOnly so the native broker preserves the parallel-
// implementation invariant that "reading the DB cannot create or
// mutate it"; the writer needed for cook lifecycle has its own
// service with its own ReadWriteCreate connection.
//
// Doctrine:
//   - SqliteOpenMode.ReadWriteCreate matches the PS broker's startup
//     open (Start-Broker.ps1 ~line 1252). Stage 3e never creates the
//     DB at request time -- the PS broker creates it at install and
//     the Stage 3e tests seed it in their fixture -- but ReadWriteCreate
//     remains the canonical mode so the open never refuses a fresh
//     workspace.
//   - Each call opens a fresh connection, runs its statement, and
//     disposes. SQLite handles the file-locking; no shared state in
//     the writer.
//   - INSERT covers every NOT NULL column the production schema
//     declares (cook_id, recipe_id, recipe_snapshot_json,
//     command_argv_json, command_argv_redacted, pax_script_path,
//     pax_script_version, trigger, cook_folder, status, created_at,
//     updated_at). started_at is set NOT NULL by the route immediately
//     before INSERT to match the PS broker.
//   - UPDATE applies the terminal state mapped from PaxRunResult:
//     status, exit_code, pid, started_at, finished_at, duration_seconds,
//     error_class, error_message, updated_at. closure_reason /
//     closure_evidence_json / abnormal_close_recorded_utc are
//     intentionally left NULL at Stage 3e (Stage 3f wires the full
//     terminal taxonomy).
public sealed class CookRowWriter
{
    private readonly WorkspacePaths _paths;

    public CookRowWriter(WorkspacePaths paths) => _paths = paths;

    public void InsertCookRow(CookInsertParams p)
    {
        using var conn = OpenReadWriteCreate();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO cooks (
                cook_id, recipe_id, recipe_snapshot_json,
                command_argv_json, command_argv_redacted,
                pax_script_path, pax_script_version,
                trigger, cook_folder, status,
                pid, started_at,
                created_at, updated_at
            ) VALUES (
                $cook_id, $recipe_id, $recipe_snapshot_json,
                $command_argv_json, $command_argv_redacted,
                $pax_script_path, $pax_script_version,
                $trigger, $cook_folder, $status,
                $pid, $started_at,
                $created_at, $updated_at
            );";
        cmd.Parameters.AddWithValue("$cook_id",               p.CookId);
        cmd.Parameters.AddWithValue("$recipe_id",             p.RecipeId);
        cmd.Parameters.AddWithValue("$recipe_snapshot_json",  p.RecipeSnapshotJson);
        cmd.Parameters.AddWithValue("$command_argv_json",     p.CommandArgvJson);
        cmd.Parameters.AddWithValue("$command_argv_redacted", p.CommandArgvRedacted);
        cmd.Parameters.AddWithValue("$pax_script_path",       p.PaxScriptPath);
        cmd.Parameters.AddWithValue("$pax_script_version",    p.PaxScriptVersion);
        cmd.Parameters.AddWithValue("$trigger",               p.Trigger);
        cmd.Parameters.AddWithValue("$cook_folder",           p.CookFolderRelative);
        cmd.Parameters.AddWithValue("$status",                p.Status);
        cmd.Parameters.AddWithValue("$pid",                   (object?)p.Pid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started_at",            (object?)p.StartedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at",            p.CreatedAtUtc);
        cmd.Parameters.AddWithValue("$updated_at",            p.UpdatedAtUtc);
        cmd.ExecuteNonQuery();
    }

    public void UpdateTerminalState(CookTerminalUpdate u)
    {
        using var conn = OpenReadWriteCreate();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE cooks
               SET status           = $status,
                   exit_code        = $exit_code,
                   pid              = $pid,
                   finished_at      = $finished_at,
                   duration_seconds = $duration_seconds,
                   error_class      = $error_class,
                   error_message    = $error_message,
                   updated_at       = $updated_at
             WHERE cook_id = $cook_id;";
        cmd.Parameters.AddWithValue("$status",           u.Status);
        cmd.Parameters.AddWithValue("$exit_code",        (object?)u.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pid",              (object?)u.Pid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finished_at",      (object?)u.FinishedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duration_seconds", (object?)u.DurationSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error_class",      (object?)u.ErrorClass ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error_message",    (object?)u.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at",       u.UpdatedAtUtc);
        cmd.Parameters.AddWithValue("$cook_id",          u.CookId);
        cmd.ExecuteNonQuery();
    }

    // Returns the cook_id of the running cook for the given recipe,
    // or null when no running cook exists. Used for the per-recipe
    // concurrency guard (parity with Get-RunningCookIdForRecipe).
    public string? GetRunningCookIdForRecipe(string recipeId)
    {
        if (!File.Exists(_paths.DatabaseFile)) return null;
        using var conn = OpenReadWriteCreate();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT cook_id
              FROM cooks
             WHERE recipe_id = $recipe_id
               AND status = 'running'
             ORDER BY created_at DESC
             LIMIT 1;";
        cmd.Parameters.AddWithValue("$recipe_id", recipeId);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : (string)v;
    }

    private SqliteConnection OpenReadWriteCreate()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabaseFile,
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Cache      = SqliteCacheMode.Private,
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }
}

public sealed record CookInsertParams(
    string  CookId,
    string  RecipeId,
    string  RecipeSnapshotJson,
    string  CommandArgvJson,
    string  CommandArgvRedacted,
    string  PaxScriptPath,
    string  PaxScriptVersion,
    string  Trigger,
    string  CookFolderRelative,
    string  Status,
    int?    Pid,
    string? StartedAtUtc,
    string  CreatedAtUtc,
    string  UpdatedAtUtc);

public sealed record CookTerminalUpdate(
    string  CookId,
    string  Status,
    int?    ExitCode,
    int?    Pid,
    string? FinishedAtUtc,
    double? DurationSeconds,
    string? ErrorClass,
    string? ErrorMessage,
    string  UpdatedAtUtc);
