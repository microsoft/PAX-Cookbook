namespace PAXCookbook.Broker.Native.Models;

// Stage 3g -- row projections + abstraction models for the
// scheduled-task surface. Mirrors:
//   * Get-ScheduledTaskRow (single-row payload, 14 columns)
//   * Get-ScheduledTaskRowsAll (LEFT JOIN with recipes, +2 columns)
//   * Get-ScheduledTaskLastTerminalCook (5-column cook subset)
//   * Invoke-ScheduledTaskRegistrar (exitCode/stdout/stderr/log/duration)
// from app\broker\Routes\ScheduledTasks.ps1.
//
// Nullable string fields surface as JSON null when the underlying
// column is NULL -- same as the PowerShell broker's $null projection.

// scheduled_tasks single-row projection. Matches the 14 columns of
// the V1.S06c schema (Start-Broker.ps1 lines 2324-2342).
public sealed record ScheduledTaskRow(
    string ScheduledTaskId,
    string RecipeId,
    string WindowsTaskName,
    string WindowsTaskPath,
    string RecipeProjectionHash,
    string PaxScriptVersion,
    string RegisteredAt,
    string RegisteredByUser,
    string? LastImportedCookId,
    string? LastImportedAt,
    string? LastStaleCheckAt,
    string Status,
    string CreatedAt,
    string UpdatedAt);

// scheduled_tasks list row with recipes-table LEFT JOIN. Mirrors
// Get-ScheduledTaskRowsAll exactly. recipeName / recipeDeletedAt are
// nullable because the LEFT JOIN preserves orphaned scheduled_tasks
// rows when the recipe row was already cascade-deleted (in practice
// the ON DELETE CASCADE on scheduled_tasks.recipe_id should prevent
// that, but the PS broker projects null defensively and we match).
public sealed record ScheduledTaskListRow(
    string ScheduledTaskId,
    string RecipeId,
    string WindowsTaskName,
    string WindowsTaskPath,
    string RecipeProjectionHash,
    string PaxScriptVersion,
    string RegisteredAt,
    string RegisteredByUser,
    string? LastImportedCookId,
    string? LastImportedAt,
    string? LastStaleCheckAt,
    string Status,
    string CreatedAt,
    string UpdatedAt,
    string? RecipeName,
    string? RecipeDeletedAt);

// Last-terminal-cook projection for the health composer. Matches the
// SELECT in Get-ScheduledTaskLastTerminalCook verbatim. ExitCode is
// nullable because the cooks schema declares it nullable and some
// failure paths (wrapper_spawn_failed) record a null exit_code.
public sealed record ScheduledTaskTerminalCook(
    string CookId,
    string Status,
    string? ErrorClass,
    int? ExitCode,
    string? StartedAt,
    string? FinishedAt);

// Registrar invocation request. The wrapper at fire-time fetches the
// client secret from Windows Credential Manager itself -- this model
// deliberately has NO ClientSecret field so the registrar argv contract
// (non-secret only) is enforced at the type level. RecurrenceJson is
// the literal JSON string the registrar passes to Register-ScheduledTask;
// it is null/empty for the 'unregister' action.
public sealed record ScheduledTaskRegistrarRequest(
    string Action,
    string RecipeId,
    string ScheduledTaskId,
    string WorkspacePath,
    string? RecurrenceJson);

// Registrar invocation result. Mirrors Invoke-ScheduledTaskRegistrar's
// return hashtable. ExitCode 31 is reserved for 'registrar missing
// or spawn failure' (matches the PS broker's sentinel).
public sealed record ScheduledTaskRegistrarResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    string? LogPath,
    long DurationMs);

// V1.S07 health envelope. The composer in ScheduledTaskHealthComposer
// fills this from a Get-ScheduledTaskRow + a Get-ScheduledTaskLast
// TerminalCook + a Test-ScheduledTaskHasRunningCook count. Stage 3g
// deliberately leaves projection-hash fields null -- the native broker
// cannot compute the projection hash until Stage 3h lands the
// projection-hash composer, so we surface that honestly via Status
// and Message rather than faking equality.
public sealed record ScheduledTaskHealth(
    string Status,
    bool Stale,
    string? ProjectionHashCurrent,
    string? ProjectionHashRegistered,
    string? StaleProjectionCheckedAt,
    string? LastImportedCookId,
    string? LastImportedAt,
    string? LastTerminalCookId,
    string? LastTerminalStatus,
    string? LastTerminalErrorClass,
    string? LastTerminalAt,
    string Message);
