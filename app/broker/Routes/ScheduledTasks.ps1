# =====================================================================
# V1.S06c -- Native Windows Task Scheduler integration
# =====================================================================
#
# Chef's doctrine (verbatim, V1.S06b):
#   PAX is the execution engine.
#   Windows Task Scheduler is the scheduler.
#   Cookbook is the authoring, registration, wrapper, and history/
#   review shell.
#
# This route is the HTTP surface that authors, inspects, and removes
# the Windows Scheduled Tasks that fire the cookbook-supplied wrapper.
# It does NOT itself touch the Task Scheduler -- *-ScheduledTask
# cmdlets are forbidden in the broker. OS-side registration is
# delegated to a child pwsh process running the external helper
#
#     app/install/Register-PAXScheduledRecipe.ps1
#
# spawned with NON-SECRET argv only. The child receives the recipeId,
# the scheduledTaskId, and the recurrence JSON; it does NOT receive
# the client secret. The client secret remains in Windows Credential
# Manager (CredMan) under the same Windows account that runs the
# wrapper at fire-time, so the wrapper's call to Read-AuthProfileSecret
# succeeds without any cookbook-owned secret storage.
#
# Routes:
#
#     GET    /api/v1/scheduled-tasks
#         List every scheduled_tasks row (read-only). Returns the
#         minimal projection the Settings UI needs to render the
#         "Scheduled tasks" table: recipeId, recipeName (resolved at
#         query time), windowsTaskName, status, lastImportedAt,
#         lastImportedCookId, recipeProjectionHash, registeredAt.
#         No staleness check (single-row GET below performs that).
#
#     GET    /api/v1/recipes/<id>/scheduled-task
#         Read-only single-row inspection PLUS empirical staleness
#         check. Re-loads the recipe from disk, recomputes the
#         projection hash, compares to the stored row, and updates
#         last_stale_check_at. Returns { registered: bool, stale:
#         bool, currentProjectionHash, ... } so the editor can render
#         a "Schedule is stale" banner without a second round-trip.
#
#     PUT    /api/v1/recipes/<id>/scheduled-task
#         Register-or-update. Body: { "recurrence": {...},
#         "clientSecret": "<plaintext>" }. SEC-A doctrine:
#         clientSecret is REQUIRED on EVERY PUT (create AND update)
#         when the recipe's auth mode is AppRegistrationSecret. There
#         is NO 'already bound, skip prompt' path; every PUT performs
#         a fresh CredMan rebind via Set-AuthProfileSecret. The
#         wrapper at fire-time reads the secret back from CredMan
#         exactly because we know this PUT rebind is the only source
#         that could have put it there. clientSecret is omitted for
#         AppRegistrationCertificate (no secret involved). The
#         handler spawns the registrar with the recurrence +
#         scheduledTaskId argv (non-secret), then on registrar success
#         upserts the scheduled_tasks row. Stale-projection refusal
#         is enforced by the wrapper at fire-time, not here; the
#         editor only sees the new projection hash that the wrapper
#         will later validate.
#
#     DELETE /api/v1/recipes/<id>/scheduled-task
#         Unregister. Spawns the registrar with -Action unregister
#         then deletes the scheduled_tasks row. Cook history is NOT
#         touched (cook_artifacts rows produced by the wrapper-spawned
#         PAX run live in their own cook folder and survive the
#         unregister).
#
# Refusal taxonomy (returned as { "error": "<code>" } with HTTP 4xx):
#
#     recipe_not_found              recipe row missing or trashed
#     recipe_trashed                recipe row carries deleted_at
#     recipe_invalid                on-disk recipe failed Test-RecipeAll
#     recipe_not_local_manual       executionMode != 'local-manual'
#     recipe_auth_unsupported       auth.mode is WebLogin / DeviceCode
#     auth_profile_missing          auth.authProfileId does not resolve
#     auth_profile_secret_missing   AppRegistrationSecret + no CredMan
#     invalid_json                  body parse failed
#     invalid_recurrence            recurrence shape failed validation
#     schedule_already_registered   PUT with existing row + force=false
#     projection_failed             Get-RecipeProjectionHash threw
#     registrar_failed              registrar exit code != 0
#     task_not_found                DELETE without an existing row
#     db_write_failed               SQLite upsert/delete threw
#
# Re-auth (Phase AF doctrine):
#   PUT and DELETE call Invoke-AuthProfileReAuthOrShortCircuit with
#   OpClass='scheduleConfig'. GET is read-only and is NOT gated.
#   'scheduleConfig' is enumerated at BrokerLock.ps1 line 124 and was
#   reserved specifically for this slice.
#
# Dependencies (loaded by Start-Broker.ps1 before this file):
#   Read-RequestJson, Write-JsonResponse, Get-UtcNowIso          (Start-Broker.ps1)
#   $Script:SqliteConn, $Script:WorkspacePath, $Script:AppRoot   (Start-Broker.ps1)
#   $Script:PaxScriptPath, $Script:PaxScriptVersion              (Start-Broker.ps1)
#   $Script:RecipeIdPattern                                      (Recipes.ps1)
#   Get-RecipeRow, Read-RecipeFile                               (Recipes.ps1)
#   Test-RecipeAll                                               (RecipeValidator.ps1)
#   Get-AuthProfileRow                                           (AuthProfiles.ps1)
#   Set-AuthProfileSecret, Test-AuthProfileSecretPresent,
#   Get-AuthProfileCredManTarget                                 (CredentialManager.ps1)
#   Invoke-AuthProfileReAuthOrShortCircuit                       (AuthProfiles.ps1)
#   New-BrokerLockReAuthRequiredResponse                         (BrokerLock.ps1)
#   Get-RecipeProjectionHash, Get-PaxInvocationPlan              (Pax/Adapter.psm1)

# ---------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------

# The wrapper runs as 'local-scheduled' even though the saved recipe
# is 'local-manual'. The projection hash is computed against
# 'local-scheduled' at register-time AND at wrapper fire-time so the
# stored and live hashes agree exactly when nothing has drifted.
$Script:ScheduledTaskExecutionMode = 'local-scheduled'

# OS-side Task Scheduler folder. Used by the registrar; the broker
# stores it on the row for diagnostic visibility (the UI shows
# "Windows Task Scheduler -> Task Scheduler Library -> PAX Cookbook
# -> <task name>" so the operator can find the task in taskschd.msc).
$Script:ScheduledTaskFolderPath = '\PAX Cookbook\'

# Recurrence kinds the broker accepts. The registrar accepts the
# same enumeration; the wrapper does not care (it reads the row by
# recipeId, not by recurrence).
$Script:ScheduledTaskRecurrenceKinds = @('daily', 'weekly')

# Permitted auth modes for scheduling. WebLogin and DeviceCode are
# interactive and cannot run unattended; they are refused at PUT.
$Script:ScheduledTaskPermittedAuthModes = @('AppRegistrationSecret', 'AppRegistrationCertificate')

# ---------------------------------------------------------------------
# Row I/O
# ---------------------------------------------------------------------

function Get-ScheduledTaskRow {
    # Returns the scheduled_tasks row keyed by recipe_id, or $null if
    # none. The route handlers use this both for "does this recipe
    # have a registered task" and for "what is the stored projection
    # hash". Read-only.
    param([Parameter(Mandatory)][string]$RecipeId)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
SELECT scheduled_task_id, recipe_id, windows_task_name, windows_task_path,
       recipe_projection_hash, pax_script_version, registered_at,
       registered_by_user, last_imported_cook_id, last_imported_at,
       last_stale_check_at, status, created_at, updated_at,
       registered_recurrence_json
FROM scheduled_tasks
WHERE recipe_id = $rid;
'@
    [void]$cmd.Parameters.AddWithValue('$rid', $RecipeId)
    $reader = $cmd.ExecuteReader()
    try {
        if ($reader.Read()) {
            return [ordered]@{
                scheduledTaskId       = [string]$reader.GetValue(0)
                recipeId              = [string]$reader.GetValue(1)
                windowsTaskName       = [string]$reader.GetValue(2)
                windowsTaskPath       = [string]$reader.GetValue(3)
                recipeProjectionHash  = [string]$reader.GetValue(4)
                paxScriptVersion      = [string]$reader.GetValue(5)
                registeredAt          = [string]$reader.GetValue(6)
                registeredByUser      = [string]$reader.GetValue(7)
                lastImportedCookId    = if ($reader.IsDBNull(8))  { $null } else { [string]$reader.GetValue(8) }
                lastImportedAt        = if ($reader.IsDBNull(9))  { $null } else { [string]$reader.GetValue(9) }
                lastStaleCheckAt      = if ($reader.IsDBNull(10)) { $null } else { [string]$reader.GetValue(10) }
                status                = [string]$reader.GetValue(11)
                createdAt             = [string]$reader.GetValue(12)
                updatedAt             = [string]$reader.GetValue(13)
                registeredRecurrenceJson = if ($reader.IsDBNull(14)) { $null } else { [string]$reader.GetValue(14) }
            }
        }
        return $null
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
}

function Get-ScheduledTaskRowsAll {
    # Returns every scheduled_tasks row (UI list view). Joined with
    # recipes for display name resolution. The ordering is by
    # registered_at DESC so the most recently authored tasks appear
    # at the top of the Settings table.
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
SELECT s.scheduled_task_id, s.recipe_id, s.windows_task_name, s.windows_task_path,
       s.recipe_projection_hash, s.pax_script_version, s.registered_at,
       s.registered_by_user, s.last_imported_cook_id, s.last_imported_at,
       s.last_stale_check_at, s.status, s.created_at, s.updated_at,
       r.name AS recipe_name, r.deleted_at AS recipe_deleted_at
FROM scheduled_tasks s
LEFT JOIN recipes r ON r.recipe_id = s.recipe_id
ORDER BY s.registered_at DESC, s.scheduled_task_id ASC;
'@
    $rows = New-Object System.Collections.Generic.List[object]
    $reader = $cmd.ExecuteReader()
    try {
        while ($reader.Read()) {
            $rows.Add( [ordered]@{
                scheduledTaskId      = [string]$reader.GetValue(0)
                recipeId             = [string]$reader.GetValue(1)
                windowsTaskName      = [string]$reader.GetValue(2)
                windowsTaskPath      = [string]$reader.GetValue(3)
                recipeProjectionHash = [string]$reader.GetValue(4)
                paxScriptVersion     = [string]$reader.GetValue(5)
                registeredAt         = [string]$reader.GetValue(6)
                registeredByUser     = [string]$reader.GetValue(7)
                lastImportedCookId   = if ($reader.IsDBNull(8))  { $null } else { [string]$reader.GetValue(8) }
                lastImportedAt       = if ($reader.IsDBNull(9))  { $null } else { [string]$reader.GetValue(9) }
                lastStaleCheckAt     = if ($reader.IsDBNull(10)) { $null } else { [string]$reader.GetValue(10) }
                status               = [string]$reader.GetValue(11)
                createdAt            = [string]$reader.GetValue(12)
                updatedAt            = [string]$reader.GetValue(13)
                recipeName           = if ($reader.IsDBNull(14)) { $null } else { [string]$reader.GetValue(14) }
                recipeDeletedAt      = if ($reader.IsDBNull(15)) { $null } else { [string]$reader.GetValue(15) }
            } )
        }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
    return ,@($rows.ToArray())
}

function Update-ScheduledTaskStaleCheck {
    # Updates last_stale_check_at on an existing row; idempotent.
    # Used by the single-row GET to record the observation that the
    # broker actually compared the live projection to the stored
    # projection. Distinct from registeredAt / lastImportedAt --
    # those mark write events; this marks a read event.
    #
    # V1.S07 -- this column is also surfaced verbatim in the new
    # health.staleProjectionCheckedAt field returned by the same GET.
    # The S07 slice does NOT add a second column for the same purpose;
    # the existing watermark is the single source of truth.
    param(
        [Parameter(Mandatory)][string]$RecipeId,
        [Parameter(Mandatory)][string]$NowIso
    )
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
UPDATE scheduled_tasks
SET last_stale_check_at = $now, updated_at = $now
WHERE recipe_id = $rid;
'@
    [void]$cmd.Parameters.AddWithValue('$now', $NowIso)
    [void]$cmd.Parameters.AddWithValue('$rid', $RecipeId)
    try { return [int]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

# =====================================================================
# V1.S07 -- Scheduled-task health surfacing
# =====================================================================
#
# Read-only helpers that compose the bounded health object returned by
# GET /api/v1/recipes/<id>/scheduled-task. The single-row GET handler
# is the only caller. None of these functions touch PAX, the
# scheduled-task wrapper, the registrar, Task Scheduler cmdlets, or
# Windows Credential Manager. They read from the cooks +
# scheduled_tasks tables that V1.S06c / V1.S06d already populate.
#
# Status priority (deterministic, first match wins, evaluated by
# New-ScheduledTaskHealthObject):
#
#   1. not_registered      -- no scheduled_tasks row
#   2. stale               -- live projection hash differs from
#                             scheduled_tasks.recipe_projection_hash
#   3. last_run_refused    -- last terminal scheduled cook has
#                             status='refused' OR error_class is
#                             'refused_stale_projection'
#   4. last_run_failed     -- last terminal scheduled cook has
#                             status='failed'
#   5. last_run_interrupted-- last terminal scheduled cook has
#                             status='interrupted'
#   6. last_run_running    -- any scheduled cook is currently in
#                             status='running'
#   7. current             -- registered, hash matches, no bad
#                             terminal outcome and nothing running
#   8. unknown             -- registered but live hash recompute
#                             failed (e.g. recipe failed to load)
#
# Health is advisory and read-only. The handler never registers,
# updates, or deletes a Windows scheduled task as a side effect of
# this read.

function Get-ScheduledTaskLastTerminalCook {
    # Returns the most recent terminal scheduled cook for this
    # scheduled task as an ordered hashtable, or $null if none. The
    # V1.S06d reconciler is the only writer for trigger='scheduled'
    # rows; this query reads the already-reconciled view. Terminal
    # statuses are completed / failed / refused / interrupted.
    #
    # Ordering uses COALESCE(finished_at, started_at) DESC so a
    # finished row is dated by its finish time and a row that started
    # but never finished (e.g. an interrupted-by-orphan-classification
    # row whose finished_at was left null by the reconciler) is dated
    # by its start time.
    param([Parameter(Mandatory)][string]$ScheduledTaskId)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
SELECT cook_id, status, error_class, exit_code, started_at, finished_at
FROM cooks
WHERE schedule_id = $sid
  AND trigger = 'scheduled'
  AND status IN ('completed','failed','refused','interrupted')
ORDER BY COALESCE(finished_at, started_at) DESC, cook_id DESC
LIMIT 1;
'@
    [void]$cmd.Parameters.AddWithValue('$sid', $ScheduledTaskId)
    $reader = $cmd.ExecuteReader()
    try {
        if ($reader.Read()) {
            return [ordered]@{
                cookId     = [string]$reader.GetValue(0)
                status     = [string]$reader.GetValue(1)
                errorClass = if ($reader.IsDBNull(2)) { $null } else { [string]$reader.GetValue(2) }
                exitCode   = if ($reader.IsDBNull(3)) { $null } else { [int]$reader.GetValue(3) }
                startedAt  = if ($reader.IsDBNull(4)) { $null } else { [string]$reader.GetValue(4) }
                finishedAt = if ($reader.IsDBNull(5)) { $null } else { [string]$reader.GetValue(5) }
            }
        }
        return $null
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
}

function Test-ScheduledTaskHasRunningCook {
    # Returns $true if any scheduled cook for this task is currently
    # in status='running'. The V1.S06d reconciler classifies a
    # wrapper-started-but-not-finished folder as 'running' until the
    # 720-minute grace window expires (then 'interrupted'). The
    # health priority chain uses this to render last_run_running ONLY
    # when no bad terminal outcome outranks it.
    param([Parameter(Mandatory)][string]$ScheduledTaskId)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
SELECT COUNT(1) FROM cooks
WHERE schedule_id = $sid
  AND trigger = 'scheduled'
  AND status = 'running';
'@
    [void]$cmd.Parameters.AddWithValue('$sid', $ScheduledTaskId)
    try {
        return ([int]$cmd.ExecuteScalar() -gt 0)
    } finally {
        $cmd.Dispose()
    }
}

function New-ScheduledTaskHealthObject {
    # Pure-function composer for the { health: { ... } } block. The
    # route handler is the only caller; this function performs no
    # SQL, no IO, no PAX call. Inputs are already-gathered:
    #   $TaskRow        -- ordered hashtable from Get-ScheduledTaskRow
    #                      or $null when not registered.
    #   $CurrentHash    -- live projection hash or $null on recompute
    #                      failure.
    #   $HashRecomputed -- $true if the live recompute succeeded
    #                      (regardless of equality), $false otherwise.
    #   $StaleCheckedAt -- ISO of the just-recorded stale check.
    #   $LastTerminal   -- hashtable from Get-ScheduledTaskLastTerminalCook
    #                      or $null.
    #   $HasRunning     -- $true if Test-ScheduledTaskHasRunningCook
    #                      returned $true.
    # See the V1.S07 banner above for the priority chain.
    param(
        $TaskRow,
        [string]$CurrentHash,
        [bool]$HashRecomputed,
        [string]$StaleCheckedAt,
        $LastTerminal,
        [bool]$HasRunning
    )

    if (-not $TaskRow) {
        return [ordered]@{
            status                   = 'not_registered'
            stale                    = $false
            projectionHashCurrent    = $null
            projectionHashRegistered = $null
            staleProjectionCheckedAt = $null
            lastImportedCookId       = $null
            lastImportedAt           = $null
            lastTerminalCookId       = $null
            lastTerminalStatus       = $null
            lastTerminalErrorClass   = $null
            lastTerminalAt           = $null
            message                  = 'Not registered with Windows Task Scheduler.'
        }
    }

    $registeredHash = [string]$TaskRow.recipeProjectionHash
    $stale = $false
    if ($HashRecomputed -and $CurrentHash -and ($CurrentHash -ne $registeredHash)) {
        $stale = $true
    }

    $lastTerminalCookId     = if ($LastTerminal) { [string]$LastTerminal.cookId } else { $null }
    $lastTerminalStatus     = if ($LastTerminal) { [string]$LastTerminal.status } else { $null }
    $lastTerminalErrorClass = if ($LastTerminal) { $LastTerminal.errorClass } else { $null }
    $lastTerminalAt         = $null
    if ($LastTerminal) {
        if ($LastTerminal.finishedAt) { $lastTerminalAt = [string]$LastTerminal.finishedAt }
        elseif ($LastTerminal.startedAt) { $lastTerminalAt = [string]$LastTerminal.startedAt }
    }

    $status  = $null
    $message = $null

    if ($stale) {
        $status  = 'stale'
        $message = 'Schedule is stale: the saved recipe or PAX engine has changed since registration. Re-save the schedule to refresh the projection.'
    } elseif ($LastTerminal -and ($lastTerminalStatus -eq 'refused' -or $lastTerminalErrorClass -eq 'refused_stale_projection')) {
        $status  = 'last_run_refused'
        $message = 'Last scheduled run refused: recipe changed since registration. Update / re-register the scheduled task.'
    } elseif ($LastTerminal -and $lastTerminalStatus -eq 'failed') {
        $status = 'last_run_failed'
        if ($lastTerminalErrorClass -eq 'pax_nonzero_exit') {
            $message = 'Last scheduled run failed in PAX. Open the run and inspect the PAX log for the exit code and reason.'
        } elseif ($lastTerminalErrorClass -eq 'wrapper_spawn_failed') {
            $message = 'Last scheduled run failed: the wrapper could not spawn PAX. Open the run and inspect the wrapper envelope.'
        } elseif ($lastTerminalErrorClass -eq 'wrapper_internal_error') {
            $message = 'Last scheduled run failed: wrapper internal error. Open the run and inspect the wrapper envelope.'
        } else {
            $message = 'Last scheduled run failed. Open the run and inspect the PAX log.'
        }
    } elseif ($LastTerminal -and $lastTerminalStatus -eq 'interrupted') {
        $status = 'last_run_interrupted'
        if ($lastTerminalErrorClass -eq 'wrapper_orphan_classified') {
            $message = 'Last scheduled run was orphan-classified after the grace window. Inspect the wrapper folder and Task Scheduler history.'
        } else {
            $message = 'Last scheduled run was interrupted. Inspect the wrapper folder and Task Scheduler history.'
        }
    } elseif ($HasRunning) {
        $status  = 'last_run_running'
        $message = 'A scheduled cook is currently running.'
    } elseif ($HashRecomputed -and $CurrentHash -and ($CurrentHash -eq $registeredHash)) {
        $status = 'current'
        if ($LastTerminal -and $lastTerminalStatus -eq 'completed') {
            $message = 'Schedule is current. Last scheduled run completed.'
        } else {
            $message = 'Schedule is current. No scheduled runs have completed yet.'
        }
    } else {
        $status  = 'unknown'
        $message = 'Schedule registered, but staleness could not be determined. Check the recipe and re-save the schedule if needed.'
    }

    return [ordered]@{
        status                   = $status
        stale                    = $stale
        projectionHashCurrent    = $CurrentHash
        projectionHashRegistered = $registeredHash
        staleProjectionCheckedAt = $StaleCheckedAt
        lastImportedCookId       = $TaskRow.lastImportedCookId
        lastImportedAt           = $TaskRow.lastImportedAt
        lastTerminalCookId       = $lastTerminalCookId
        lastTerminalStatus       = $lastTerminalStatus
        lastTerminalErrorClass   = $lastTerminalErrorClass
        lastTerminalAt           = $lastTerminalAt
        message                  = $message
    }
}

function Set-ScheduledTaskRow {
    # INSERT ... ON CONFLICT(recipe_id) DO UPDATE pattern. The
    # scheduled_tasks schema declares recipe_id UNIQUE (and the FK
    # to recipes is ON DELETE CASCADE) so the natural identity of a
    # row is the recipeId, not the scheduledTaskId. On a re-PUT
    # against an already-registered recipe the same row is updated
    # in place; the scheduledTaskId stays stable across re-registers
    # so the windows_task_name (which is derived from it) does not
    # churn.
    param(
        [Parameter(Mandatory)][string]$ScheduledTaskId,
        [Parameter(Mandatory)][string]$RecipeId,
        [Parameter(Mandatory)][string]$WindowsTaskName,
        [Parameter(Mandatory)][string]$WindowsTaskPath,
        [Parameter(Mandatory)][string]$RecipeProjectionHash,
        [Parameter(Mandatory)][string]$PaxScriptVersion,
        [Parameter(Mandatory)][string]$NowIso,
        [string]$RegisteredByUser = '',
        [string]$RegisteredRecurrenceJson = ''
    )
    if ([string]::IsNullOrWhiteSpace($RegisteredByUser)) {
        $RegisteredByUser = [string]([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
    }
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
INSERT INTO scheduled_tasks (
    scheduled_task_id, recipe_id, windows_task_name, windows_task_path,
    recipe_projection_hash, pax_script_version,
    registered_at, registered_by_user, status,
    registered_recurrence_json, created_at, updated_at
) VALUES (
    $stid, $rid, $tname, $tpath,
    $hash, $pver,
    $now, $usr, 'active',
    $rrj, $now, $now
)
ON CONFLICT(recipe_id) DO UPDATE SET
    windows_task_name        = excluded.windows_task_name,
    windows_task_path        = excluded.windows_task_path,
    recipe_projection_hash   = excluded.recipe_projection_hash,
    pax_script_version       = excluded.pax_script_version,
    registered_at            = excluded.registered_at,
    registered_by_user       = excluded.registered_by_user,
    status                   = 'active',
    registered_recurrence_json = excluded.registered_recurrence_json,
    updated_at               = excluded.updated_at;
'@
    [void]$cmd.Parameters.AddWithValue('$stid',  $ScheduledTaskId)
    [void]$cmd.Parameters.AddWithValue('$rid',   $RecipeId)
    [void]$cmd.Parameters.AddWithValue('$tname', $WindowsTaskName)
    [void]$cmd.Parameters.AddWithValue('$tpath', $WindowsTaskPath)
    [void]$cmd.Parameters.AddWithValue('$hash',  $RecipeProjectionHash)
    [void]$cmd.Parameters.AddWithValue('$pver',  $PaxScriptVersion)
    [void]$cmd.Parameters.AddWithValue('$now',   $NowIso)
    [void]$cmd.Parameters.AddWithValue('$usr',   $RegisteredByUser)
    if ([string]::IsNullOrWhiteSpace($RegisteredRecurrenceJson)) {
        [void]$cmd.Parameters.AddWithValue('$rrj', [System.DBNull]::Value)
    } else {
        [void]$cmd.Parameters.AddWithValue('$rrj', $RegisteredRecurrenceJson)
    }
    try { return [int]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

function Remove-ScheduledTaskRow {
    param([Parameter(Mandatory)][string]$RecipeId)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'DELETE FROM scheduled_tasks WHERE recipe_id = $rid;'
    [void]$cmd.Parameters.AddWithValue('$rid', $RecipeId)
    try { return [int]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

# ---------------------------------------------------------------------
# Body validation
# ---------------------------------------------------------------------

function Test-ScheduledTaskPutBody {
    # Validates the request body for PUT /api/v1/recipes/<id>/scheduled-task.
    # Returns @{ ok=<bool>; errors=@(...); recurrence=@{...}; secretPresent=<bool> }.
    # Errors are AJV-shaped (instancePath, keyword, message, params)
    # so the editor can surface them with the same renderer used for
    # recipe-validation errors.
    param($Body)
    $errors = New-Object System.Collections.Generic.List[object]
    $add = {
        param($path, $kw, $msg, $params = @{})
        $errors.Add( [pscustomobject]@{ instancePath = $path; keyword = $kw; message = $msg; params = $params } )
    }

    if ($null -eq $Body -or -not (($Body -is [hashtable]) -or ($Body -is [System.Collections.IDictionary]))) {
        & $add '' 'type' 'request body must be a JSON object' @{}
        return @{ ok = $false; errors = @($errors.ToArray()); recurrence = $null; secretPresent = $false }
    }

    if (-not $Body.ContainsKey('recurrence') -or $null -eq $Body['recurrence']) {
        & $add '/recurrence' 'required' "must have property 'recurrence'" @{}
        return @{ ok = $false; errors = @($errors.ToArray()); recurrence = $null; secretPresent = $false }
    }

    $rec = $Body['recurrence']
    if (-not (($rec -is [hashtable]) -or ($rec -is [System.Collections.IDictionary]))) {
        & $add '/recurrence' 'type' "recurrence must be an object" @{}
        return @{ ok = $false; errors = @($errors.ToArray()); recurrence = $null; secretPresent = $false }
    }

    if (-not $rec.ContainsKey('kind')) {
        & $add '/recurrence/kind' 'required' "recurrence must have property 'kind'" @{}
    } else {
        $kind = [string]$rec['kind']
        if ($Script:ScheduledTaskRecurrenceKinds -notcontains $kind) {
            & $add '/recurrence/kind' 'enum' ("recurrence.kind must be one of: " + ($Script:ScheduledTaskRecurrenceKinds -join ', ')) @{ allowed = $Script:ScheduledTaskRecurrenceKinds }
        }
    }

    $hour = $null
    if (-not $rec.ContainsKey('hour')) {
        & $add '/recurrence/hour' 'required' "recurrence must have property 'hour'" @{}
    } else {
        try { $hour = [int]$rec['hour'] } catch { $hour = $null }
        if ($null -eq $hour -or $hour -lt 0 -or $hour -gt 23) {
            & $add '/recurrence/hour' 'range' "recurrence.hour must be an integer in [0, 23]" @{}
            $hour = $null
        }
    }

    $minute = $null
    if (-not $rec.ContainsKey('minute')) {
        & $add '/recurrence/minute' 'required' "recurrence must have property 'minute'" @{}
    } else {
        try { $minute = [int]$rec['minute'] } catch { $minute = $null }
        if ($null -eq $minute -or $minute -lt 0 -or $minute -gt 59) {
            & $add '/recurrence/minute' 'range' "recurrence.minute must be an integer in [0, 59]" @{}
            $minute = $null
        }
    }

    $daysOfWeek = $null
    if ($rec.ContainsKey('kind') -and [string]$rec['kind'] -eq 'weekly') {
        if (-not $rec.ContainsKey('daysOfWeek')) {
            & $add '/recurrence/daysOfWeek' 'required' "weekly recurrence must specify daysOfWeek" @{}
        } else {
            $raw = @($rec['daysOfWeek'])
            if ($raw.Count -lt 1 -or $raw.Count -gt 7) {
                & $add '/recurrence/daysOfWeek' 'length' "daysOfWeek must contain 1..7 entries" @{}
            } else {
                $bad = $false
                $dows = New-Object System.Collections.Generic.List[int]
                foreach ($d in $raw) {
                    $di = $null
                    try { $di = [int]$d } catch { $di = $null }
                    if ($null -eq $di -or $di -lt 0 -or $di -gt 6) {
                        & $add '/recurrence/daysOfWeek' 'range' "daysOfWeek entries must be integers in [0, 6] (0 = Sunday)" @{}
                        $bad = $true
                        break
                    }
                    if (-not $dows.Contains($di)) { $dows.Add($di) | Out-Null }
                }
                if (-not $bad) { $daysOfWeek = @($dows.ToArray()) }
            }
        }
    }

    $secretPresent = $false
    if ($Body.ContainsKey('clientSecret')) {
        $cs = [string]$Body['clientSecret']
        if (-not [string]::IsNullOrEmpty($cs)) { $secretPresent = $true }
    }

    if ($errors.Count -ne 0) {
        return @{ ok = $false; errors = @($errors.ToArray()); recurrence = $null; secretPresent = $secretPresent }
    }

    $kind = [string]$rec['kind']
    $normalized = [ordered]@{
        kind   = $kind
        hour   = $hour
        minute = $minute
    }
    if ($kind -eq 'weekly') { $normalized['daysOfWeek'] = $daysOfWeek }
    return @{ ok = $true; errors = @(); recurrence = $normalized; secretPresent = $secretPresent }
}

# ---------------------------------------------------------------------
# Helpers: task naming, ULID, registrar invocation
# ---------------------------------------------------------------------

function New-ScheduledTaskId {
    # Reuses the recipe-id pattern (Crockford base32 ULID, 26 chars,
    # case-insensitive). We do NOT re-derive identity from recipeId
    # so a future "re-register from scratch" can rotate the
    # windows_task_name without colliding with the previous one even
    # if the OS-side unregister failed.
    $alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'.ToCharArray()
    $sb = New-Object System.Text.StringBuilder
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $buf = New-Object byte[] 26
        $rng.GetBytes($buf)
        foreach ($b in $buf) {
            [void]$sb.Append($alphabet[[int]($b -band 0x1f)])
        }
    } finally { $rng.Dispose() }
    return $sb.ToString()
}

function Get-WindowsTaskNameForRecipe {
    # Stable derivation: PAXCookbook_<recipeId>. The recipeId is a
    # 26-char ULID; the resulting task name is 39 chars, well inside
    # the 232-char Task Scheduler limit. The recipeId is the
    # collision-free authority, not a chef-supplied display name.
    param([Parameter(Mandatory)][string]$RecipeId)
    return 'PAXCookbook_' + $RecipeId
}

function Invoke-ScheduledTaskRegistrar {
    # Spawns app/install/Register-PAXScheduledRecipe.ps1 as a child
    # pwsh process with NON-SECRET argv. The registrar is the only
    # file in the install tree permitted to call *-ScheduledTask
    # cmdlets; the broker MUST NOT import ScheduledTasks itself
    # (Phase AB harness contract scan).
    #
    # Stdout/stderr are redirected to per-invocation temp files
    # under <WorkspacePath>\_tmp\scheduler\ so the response can
    # surface the registrar's diagnostic output on failure without
    # mixing it into the broker's stdout. Temp files are deleted on
    # success and retained on failure (last 16 are kept; older are
    # pruned).
    #
    # Returns @{ exitCode = <int>; stdout = <string>; stderr =
    # <string>; logPath = <string>; durationMs = <int> }.
    param(
        [Parameter(Mandatory)][ValidateSet('register','unregister')][string]$Action,
        [Parameter(Mandatory)][string]$RecipeId,
        [Parameter(Mandatory)][string]$ScheduledTaskId,
        [string]$RecurrenceJson = ''
    )
    $registrar = Join-Path $Script:AppRoot 'install\Register-PAXScheduledRecipe.ps1'
    if (-not (Test-Path -LiteralPath $registrar -PathType Leaf)) {
        return @{
            exitCode   = 31
            stdout     = ''
            stderr     = ('registrar_missing: ' + $registrar)
            logPath    = $null
            durationMs = 0
        }
    }

    $stagingDir = Join-Path $Script:WorkspacePath '_tmp\scheduler'
    if (-not (Test-Path -LiteralPath $stagingDir -PathType Container)) {
        try { [void](New-Item -ItemType Directory -Path $stagingDir -Force -ErrorAction Stop) } catch { }
    }
    $stamp   = (Get-Date -Format 'yyyyMMdd_HHmmss_fff')
    $outPath = Join-Path $stagingDir ($Action + '_' + $RecipeId + '_' + $stamp + '.out.log')
    $errPath = Join-Path $stagingDir ($Action + '_' + $RecipeId + '_' + $stamp + '.err.log')

    $argList = New-Object System.Collections.Generic.List[string]
    $argList.Add('-NoProfile')        | Out-Null
    $argList.Add('-NoLogo')           | Out-Null
    $argList.Add('-NonInteractive')   | Out-Null
    $argList.Add('-File')             | Out-Null
    $argList.Add($registrar)          | Out-Null
    $argList.Add('-Action')           | Out-Null
    $argList.Add($Action)             | Out-Null
    $argList.Add('-WorkspacePath')    | Out-Null
    $argList.Add($Script:WorkspacePath) | Out-Null
    $argList.Add('-RecipeId')         | Out-Null
    $argList.Add($RecipeId)           | Out-Null
    $argList.Add('-ScheduledTaskId')  | Out-Null
    $argList.Add($ScheduledTaskId)    | Out-Null
    if ($Action -eq 'register') {
        $argList.Add('-RecurrenceJson') | Out-Null
        $argList.Add($RecurrenceJson)   | Out-Null
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $exit = 31
    $proc = $null
    try {
        $proc = Start-Process -FilePath 'pwsh' -ArgumentList $argList -NoNewWindow -Wait -PassThru `
                              -RedirectStandardOutput $outPath -RedirectStandardError $errPath -ErrorAction Stop
        $exit = [int]$proc.ExitCode
    } catch {
        $exit = 31
        try { Set-Content -LiteralPath $errPath -Value ('spawn_failed: ' + $_.Exception.Message) -Encoding utf8 -ErrorAction SilentlyContinue } catch { }
    } finally {
        $sw.Stop()
        if ($proc) { try { $proc.Dispose() } catch { } }
    }

    $stdout = ''
    $stderr = ''
    try { if (Test-Path -LiteralPath $outPath -PathType Leaf) { $stdout = [System.IO.File]::ReadAllText($outPath, [System.Text.UTF8Encoding]::new($false)) } } catch { }
    try { if (Test-Path -LiteralPath $errPath -PathType Leaf) { $stderr = [System.IO.File]::ReadAllText($errPath, [System.Text.UTF8Encoding]::new($false)) } } catch { }

    # Retention: keep the most recent 16 transcript pairs per workspace.
    try {
        $files = Get-ChildItem -LiteralPath $stagingDir -File -ErrorAction SilentlyContinue | Sort-Object -Property LastWriteTime -Descending
        if ($files -and $files.Count -gt 32) {
            $stale = $files | Select-Object -Skip 32
            foreach ($f in $stale) { try { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue } catch { } }
        }
    } catch { }

    return @{
        exitCode   = $exit
        stdout     = $stdout
        stderr     = $stderr
        logPath    = $errPath
        durationMs = [int]$sw.ElapsedMilliseconds
    }
}

# ---------------------------------------------------------------------
# Recipe + auth-profile loading shared by GET/PUT/DELETE
# ---------------------------------------------------------------------

function Resolve-ScheduledTaskRecipeContext {
    # Loads the recipe row + on-disk file + auth profile in one place
    # so the route handlers can use a single dispatch on .ok / .error.
    # Returns @{
    #   ok        = <bool>
    #   error     = <code or null>
    #   status    = <int http status on failure>
    #   detail    = <string or hashtable>
    #   recipe    = <recipe hashtable on success>
    #   authProfile = <auth profile row on success / null when AppRegistration mode not present>
    # }
    #
    # The function does NOT enforce executionMode / auth.mode policy
    # gating -- that is per-verb (GET reports; PUT refuses). The
    # function DOES gate on existence + parseability so the verb
    # handlers can short-circuit on a single Resolve.
    param([Parameter(Mandatory)][string]$RecipeId)

    $row = Get-RecipeRow -RecipeId $RecipeId
    if (-not $row) {
        return @{ ok = $false; error = 'recipe_not_found'; status = 404; detail = @{ recipeId = $RecipeId } }
    }
    if ($row.deleted_at) {
        return @{ ok = $false; error = 'recipe_trashed'; status = 404; detail = @{ recipeId = $RecipeId } }
    }
    $loaded = Read-RecipeFile -RecipeId $RecipeId
    switch ([string]$loaded.status) {
        'ok' { }
        'missing' {
            return @{ ok = $false; error = 'recipe_invalid'; status = 412; detail = @{ loaderStatus = 'missing' } }
        }
        'malformed' {
            return @{ ok = $false; error = 'recipe_invalid'; status = 412; detail = @{ loaderStatus = 'malformed'; detail = [string]$loaded.detail } }
        }
        'unsupported_schema_version' {
            return @{ ok = $false; error = 'recipe_invalid'; status = 412; detail = @{ loaderStatus = 'unsupported_schema_version'; detail = [string]$loaded.detail } }
        }
        default {
            return @{ ok = $false; error = 'recipe_invalid'; status = 412; detail = @{ loaderStatus = [string]$loaded.status } }
        }
    }
    $recipe = $loaded.recipe
    $verdict = Test-RecipeAll -Recipe $recipe
    if (-not $verdict.ok) {
        return @{ ok = $false; error = 'recipe_invalid'; status = 412; detail = @{ errors = $verdict.errors } }
    }

    $authProfile = $null
    if ($recipe.ContainsKey('auth') -and $recipe.auth.ContainsKey('authProfileId') -and -not [string]::IsNullOrWhiteSpace([string]$recipe.auth.authProfileId)) {
        $authProfile = Get-AuthProfileRow -AuthProfileId ([string]$recipe.auth.authProfileId)
    }

    return @{ ok = $true; error = $null; status = 200; detail = $null; recipe = $recipe; authProfile = $authProfile }
}

# ---------------------------------------------------------------------
# Route handlers
# ---------------------------------------------------------------------

function Invoke-ScheduledTasksList {
    param($Context)
    $rows = Get-ScheduledTaskRowsAll
    Write-JsonResponse -Context $Context -Status 200 -Body @{
        scheduledTasks = $rows
        count          = $rows.Count
    }
}

function Invoke-ScheduledTaskGet {
    # Read + staleness check. Always returns 200 with { registered:
    # bool } so the editor can render uniformly; absence of a row is
    # not a 404, it is an explicit "not registered" state. A 404 IS
    # returned if the recipe itself is missing or trashed (because
    # there is no meaningful "scheduled-task on a trashed recipe"
    # surface).
    param($Context, [string]$RecipeId)

    $row = Get-RecipeRow -RecipeId $RecipeId
    if (-not $row) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'recipe_not_found'; recipeId = $RecipeId }
        return
    }
    if ($row.deleted_at) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'recipe_trashed'; recipeId = $RecipeId }
        return
    }

    $taskRow = Get-ScheduledTaskRow -RecipeId $RecipeId
    if (-not $taskRow) {
        # V1.S07 -- still emit a bounded health object on the
        # not-registered branch so the Schedule card can render a
        # consistent pill ("Not registered") on first load.
        $unregisteredHealth = New-ScheduledTaskHealthObject `
            -TaskRow        $null `
            -CurrentHash    $null `
            -HashRecomputed $false `
            -StaleCheckedAt $null `
            -LastTerminal   $null `
            -HasRunning     $false
        Write-JsonResponse -Context $Context -Status 200 -Body ([ordered]@{
            registered = $false
            recipeId   = $RecipeId
            health     = $unregisteredHealth
            osDrift    = (New-ScheduledTaskOsDriftObject -TaskRow $null)
        })
        return
    }

    # Live projection-hash recompute. We deliberately ignore
    # validation failures here: a stale schedule on top of an
    # un-cookable recipe is still observable as "stale=true",
    # which is more useful than swallowing the row entirely.
    $loaded = Read-RecipeFile -RecipeId $RecipeId
    $currentHash = $null
    $hashError = $null
    if ([string]$loaded.status -eq 'ok') {
        $recipe = $loaded.recipe
        $authProfile = $null
        if ($recipe.ContainsKey('auth') -and $recipe.auth.ContainsKey('authProfileId') -and -not [string]::IsNullOrWhiteSpace([string]$recipe.auth.authProfileId)) {
            $authProfile = Get-AuthProfileRow -AuthProfileId ([string]$recipe.auth.authProfileId)
        }
        try {
            $currentHash = Get-RecipeProjectionHash `
                                -Recipe $recipe `
                                -PaxScriptPath $Script:PaxScriptPath `
                                -AuthProfile $authProfile `
                                -ExecutionMode $Script:ScheduledTaskExecutionMode `
                                -PaxScriptVersion $Script:PaxScriptVersion
        } catch {
            $hashError = [string]$_.Exception.Message
        }
    } else {
        $hashError = ('recipe_load_status=' + [string]$loaded.status)
    }

    $stale = $null
    if ($currentHash) { $stale = ($currentHash -ne $taskRow.recipeProjectionHash) }

    $now = Get-UtcNowIso
    try { [void](Update-ScheduledTaskStaleCheck -RecipeId $RecipeId -NowIso $now) } catch { }

    # V1.S07 -- gather the bounded inputs the health composer needs.
    # Both reads are read-only and cheap (the indexes on
    # cooks(schedule_id, started_at) and the IN-list on status keep
    # the planner happy). Neither call touches Task Scheduler, PAX,
    # the wrapper, or the registrar.
    $lastTerminal = $null
    $hasRunning   = $false
    try {
        $lastTerminal = Get-ScheduledTaskLastTerminalCook -ScheduledTaskId $taskRow.scheduledTaskId
        $hasRunning   = Test-ScheduledTaskHasRunningCook -ScheduledTaskId $taskRow.scheduledTaskId
    } catch {
        # Health is advisory. A failure to read the cooks table must
        # not abort the schedule GET; the composer's 'unknown' path
        # covers the missing-evidence case.
        $lastTerminal = $null
        $hasRunning   = $false
    }

    $hashRecomputed = [bool]([string]::IsNullOrEmpty($hashError))
    $health = New-ScheduledTaskHealthObject `
        -TaskRow        $taskRow `
        -CurrentHash    $currentHash `
        -HashRecomputed $hashRecomputed `
        -StaleCheckedAt $now `
        -LastTerminal   $lastTerminal `
        -HasRunning     $hasRunning

    $payload = [ordered]@{
        registered            = $true
        recipeId              = $RecipeId
        scheduledTaskId       = $taskRow.scheduledTaskId
        windowsTaskName       = $taskRow.windowsTaskName
        windowsTaskPath       = $taskRow.windowsTaskPath
        recipeProjectionHash  = $taskRow.recipeProjectionHash
        currentProjectionHash = $currentHash
        paxScriptVersion      = $taskRow.paxScriptVersion
        currentPaxVersion     = $Script:PaxScriptVersion
        stale                 = $stale
        staleReason           = $null
        registeredAt          = $taskRow.registeredAt
        registeredByUser      = $taskRow.registeredByUser
        lastImportedCookId    = $taskRow.lastImportedCookId
        lastImportedAt        = $taskRow.lastImportedAt
        lastStaleCheckAt      = $now
        status                = $taskRow.status
    }
    if ($hashError) {
        $payload['stale']       = $true
        $payload['staleReason'] = $hashError
    } elseif ($stale -eq $true) {
        $payload['staleReason'] = 'projection_changed'
    } elseif ($taskRow.paxScriptVersion -ne $Script:PaxScriptVersion) {
        # Hash equality implies PAX version match too (the hash
        # includes the version), but record the version observation
        # explicitly so the UI can render a precise message even when
        # the live recompute succeeded.
        $payload['stale']       = $true
        $payload['staleReason'] = 'pax_version_changed'
    }

    # V1.S07 -- emit the bounded health object alongside the legacy
    # top-level fields. The UI prefers $payload.health.* for new
    # rendering; existing JS that reads $payload.stale / $payload
    # .staleReason / $payload.lastImportedCookId is preserved.
    $payload['health'] = $health

    # V1.S2 -- additive read-only OS-task drift block. Child-spawns the
    # registrar probe, classifies the live Windows task against this
    # row, and attaches the verdict next to health. Wrapped so a probe
    # failure degrades to a probe_failed verdict and never breaks GET.
    try {
        $payload['osDrift'] = New-ScheduledTaskOsDriftObject -TaskRow $taskRow
    } catch {
        $payload['osDrift'] = [ordered]@{
            status         = 'probe_failed'
            checked        = $true
            taskExists     = $null
            enabledInOs    = $null
            actionMatches  = $null
            triggerMatch   = 'unknown'
            lastRunTime    = $null
            nextRunTime    = $null
            lastTaskResult = $null
            probeError     = [string]$_.Exception.Message
            checkedAt      = (Get-Date).ToUniversalTime().ToString('o')
            message        = 'Could not read the Windows task state to compare against the registered schedule.'
        }
    }

    Write-JsonResponse -Context $Context -Status 200 -Body $payload
}

function Invoke-ScheduledTaskPut {
    # Register-or-update flow. See the file header for the refusal
    # taxonomy. SEC-A: when the recipe's auth mode is
    # AppRegistrationSecret and the body carries clientSecret, the
    # plaintext is converted to a SecureString, written to CredMan
    # via Set-AuthProfileSecret, and the local plaintext is blanked.
    # The dispose-secure block lives in a try/finally so the
    # SecureString is always disposed even on later failure.
    param($Context, [string]$RecipeId)

    # Acquisition gate. The 'acquisitionRequired' refusal is global
    # state (it does not depend on the recipeId) so gating before
    # re-auth does not narrow recipe enumeration; gating AFTER would
    # force the operator to verify Windows Hello for a request that
    # will be refused regardless, defeating the SPA's redirect to
    # the engine-acquisition overlay.
    if (Invoke-AcquisitionGateOrShortCircuit -Context $Context -Endpoint 'PUT /api/v1/recipes/<id>/scheduled-task') { return }

    # Re-auth FIRST among per-recipe checks. We do not want to
    # surface refusal reasons (recipe shape, auth profile state,
    # body shape) to a session that has not just proven its Windows
    # identity, because every such reason narrows the attacker's
    # enumeration of valid recipeIds.
    $h = Invoke-AuthProfileReAuthOrShortCircuit -Context $Context -OpClass 'scheduleConfig' -Message ("Verify to register or update the Windows Scheduled Task for this recipe.")
    if ($h) { return }

    $ctx = Resolve-ScheduledTaskRecipeContext -RecipeId $RecipeId
    if (-not $ctx.ok) {
        $body = @{ error = $ctx.error; recipeId = $RecipeId }
        if ($ctx.detail) { $body['detail'] = $ctx.detail }
        Write-JsonResponse -Context $Context -Status $ctx.status -Body $body
        return
    }

    $recipe = $ctx.recipe
    $execMode = [string]$recipe.executionMode
    if ([string]::IsNullOrWhiteSpace($execMode)) { $execMode = 'local-manual' }
    if ($execMode -ne 'local-manual') {
        # Per V1.S06b chef decision: only recipes saved as 'local-
        # manual' are schedulable. The wrapper substitutes 'local-
        # scheduled' at fire-time; the on-disk recipe stays
        # 'local-manual'.
        Write-JsonResponse -Context $Context -Status 422 -Body @{
            error          = 'recipe_not_local_manual'
            recipeId       = $RecipeId
            executionMode  = $execMode
        }
        return
    }

    if (-not $recipe.ContainsKey('auth') -or -not $recipe.auth.ContainsKey('mode')) {
        Write-JsonResponse -Context $Context -Status 422 -Body @{
            error    = 'recipe_invalid'
            recipeId = $RecipeId
            detail   = 'recipe is missing auth.mode'
        }
        return
    }
    $authMode = [string]$recipe.auth.mode
    if ($Script:ScheduledTaskPermittedAuthModes -notcontains $authMode) {
        Write-JsonResponse -Context $Context -Status 422 -Body @{
            error    = 'recipe_auth_unsupported'
            recipeId = $RecipeId
            authMode = $authMode
            allowed  = $Script:ScheduledTaskPermittedAuthModes
        }
        return
    }

    $authProfile = $ctx.authProfile
    if (-not $authProfile) {
        Write-JsonResponse -Context $Context -Status 422 -Body @{
            error         = 'auth_profile_missing'
            recipeId      = $RecipeId
            authProfileId = if ($recipe.auth.ContainsKey('authProfileId')) { [string]$recipe.auth.authProfileId } else { $null }
        }
        return
    }

    # Body parse + recurrence validation
    $body = Read-RequestJson -Context $Context
    if ($null -eq $body) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json'; detail = "request body must be { recurrence: { kind, hour, minute, daysOfWeek? }, clientSecret? }" }
        return
    }
    $bv = Test-ScheduledTaskPutBody -Body $body
    if (-not $bv.ok) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_recurrence'; errors = $bv.errors }
        return
    }
    $recurrence = $bv.recurrence
    $secretSupplied = $bv.secretPresent

    # SEC-A: client-secret rebind. The chef's V1.S06b decision is
    # that EVERY scheduled-task PUT (create AND update) for an
    # AppRegistrationSecret recipe must carry a fresh clientSecret
    # in the body. There is no "already bound, skip prompt" path:
    # the binding the wrapper later reads from CredMan must be
    # traceable to a one-shot operator input from this exact PUT.
    # AppRegistrationCertificate recipes do not require a secret
    # (the certificate is dereferenced from the user cert store at
    # PAX fire-time via the thumbprint stored on the auth profile).
    if ($authMode -eq 'AppRegistrationSecret' -and -not $secretSupplied) {
        Write-JsonResponse -Context $Context -Status 422 -Body @{
            error          = 'auth_profile_secret_missing'
            recipeId       = $RecipeId
            authProfileId  = $authProfile.auth_profile_id
            detail         = "AppRegistrationSecret scheduled-task PUT requires 'clientSecret' in the request body (one-shot, not stored). Cookbook rebinds the secret to Windows Credential Manager on every PUT."
        }
        return
    }

    $secretRebound = $false
    if ($authMode -eq 'AppRegistrationSecret' -and $secretSupplied) {
        $plain = [string]$body['clientSecret']
        $secure = $null
        try {
            $secure = New-Object System.Security.SecureString
            foreach ($ch in $plain.ToCharArray()) { $secure.AppendChar($ch) }
            $secure.MakeReadOnly()
            $plain = ''
        } catch {
            if ($secure) { try { $secure.Dispose() } catch { } }
            Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'secret_marshal_failed'; detail = [string]$_.Exception.Message }
            return
        }
        try {
            Set-AuthProfileSecret -AuthProfileId $authProfile.auth_profile_id -Secret $secure
            $secretRebound = $true
        } catch {
            try { $secure.Dispose() } catch { }
            Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'secret_write_failed'; detail = [string]$_.Exception.Message }
            return
        } finally {
            try { $secure.Dispose() } catch { }
        }
        # Mirror AuthProfiles.Invoke-AuthProfileSecretBind: refresh
        # cred_man_target column so external observers see the row
        # carrying the same target the credential was written under.
        try {
            $now0 = Get-UtcNowIso
            $tgt0 = Get-AuthProfileCredManTarget -AuthProfileId $authProfile.auth_profile_id
            [void](Update-AuthProfileCredManTarget -AuthProfileId $authProfile.auth_profile_id -Target $tgt0 -NowIso $now0)
        } catch { }
    }

    # Projection hash. Computed against 'local-scheduled' so the
    # wrapper's recompute at fire-time produces the same value when
    # nothing has drifted.
    $projectionHash = $null
    try {
        $projectionHash = Get-RecipeProjectionHash `
                            -Recipe $recipe `
                            -PaxScriptPath $Script:PaxScriptPath `
                            -AuthProfile $authProfile `
                            -ExecutionMode $Script:ScheduledTaskExecutionMode `
                            -PaxScriptVersion $Script:PaxScriptVersion
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'projection_failed'; detail = [string]$_.Exception.Message }
        return
    }

    # Identity. If a row already exists we keep the same
    # scheduledTaskId so the OS-side windows_task_name does not
    # churn across re-PUTs.
    $existing = Get-ScheduledTaskRow -RecipeId $RecipeId
    $stid = if ($existing) { $existing.scheduledTaskId } else { New-ScheduledTaskId }
    $taskName = Get-WindowsTaskNameForRecipe -RecipeId $RecipeId

    # Spawn registrar. The recurrence is serialized to a compact
    # JSON token so the child receives a single argv element. The
    # registrar parses it back into a CIM trigger object.
    $recurrenceJson = $recurrence | ConvertTo-Json -Depth 6 -Compress
    $registrar = Invoke-ScheduledTaskRegistrar -Action 'register' -RecipeId $RecipeId -ScheduledTaskId $stid -RecurrenceJson $recurrenceJson
    if ($registrar.exitCode -ne 0) {
        Write-JsonResponse -Context $Context -Status 502 -Body @{
            error            = 'registrar_failed'
            recipeId         = $RecipeId
            scheduledTaskId  = $stid
            exitCode         = $registrar.exitCode
            durationMs       = $registrar.durationMs
            stdout           = $registrar.stdout
            stderr           = $registrar.stderr
            logPath          = $registrar.logPath
        }
        return
    }

    $now = Get-UtcNowIso

    # Canonical recurrence snapshot for the registry row. daysOfWeek is
    # sorted ascending so the stored form matches the order Windows
    # Task Scheduler records, making a later drift diff order-
    # independent. This is written only here on PUT (register/update);
    # no read path ever writes it.
    $canonicalRecurrence = [ordered]@{
        kind   = $recurrence['kind']
        hour   = $recurrence['hour']
        minute = $recurrence['minute']
    }
    if ($recurrence.Contains('daysOfWeek')) {
        $canonicalRecurrence['daysOfWeek'] = @($recurrence['daysOfWeek'] | Sort-Object)
    }
    $registeredRecurrenceJson = $canonicalRecurrence | ConvertTo-Json -Depth 6 -Compress

    try {
        [void](Set-ScheduledTaskRow `
                    -ScheduledTaskId       $stid `
                    -RecipeId              $RecipeId `
                    -WindowsTaskName       $taskName `
                    -WindowsTaskPath       $Script:ScheduledTaskFolderPath `
                    -RecipeProjectionHash  $projectionHash `
                    -PaxScriptVersion      $Script:PaxScriptVersion `
                    -NowIso                $now `
                    -RegisteredRecurrenceJson $registeredRecurrenceJson)
    } catch {
        # Registrar succeeded but DB write failed. The OS-side task
        # is now ahead of the broker's view; surface the failure
        # explicitly. Operator can re-PUT or run the verifier.
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error            = 'db_write_failed'
            recipeId         = $RecipeId
            scheduledTaskId  = $stid
            detail           = [string]$_.Exception.Message
        }
        return
    }

    Write-JsonResponse -Context $Context -Status 200 -Body @{
        ok                    = $true
        recipeId              = $RecipeId
        scheduledTaskId       = $stid
        windowsTaskName       = $taskName
        windowsTaskPath       = $Script:ScheduledTaskFolderPath
        recipeProjectionHash  = $projectionHash
        paxScriptVersion      = $Script:PaxScriptVersion
        registeredAt          = $now
        secretRebound         = $secretRebound
        registrarDurationMs   = $registrar.durationMs
    }
}

function Invoke-ScheduledTaskDelete {
    param($Context, [string]$RecipeId)

    $h = Invoke-AuthProfileReAuthOrShortCircuit -Context $Context -OpClass 'scheduleConfig' -Message ("Verify to unregister the Windows Scheduled Task for this recipe.")
    if ($h) { return }

    # We deliberately allow DELETE against a trashed recipe so
    # unscheduling can clean up after a chef who trashed the recipe
    # while it still had a registered task. The recipe-row 404 below
    # only fires when there's neither a row nor a task.
    $taskRow = Get-ScheduledTaskRow -RecipeId $RecipeId
    if (-not $taskRow) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{
            error    = 'task_not_found'
            recipeId = $RecipeId
        }
        return
    }

    $registrar = Invoke-ScheduledTaskRegistrar -Action 'unregister' -RecipeId $RecipeId -ScheduledTaskId $taskRow.scheduledTaskId
    if ($registrar.exitCode -ne 0) {
        Write-JsonResponse -Context $Context -Status 502 -Body @{
            error            = 'registrar_failed'
            recipeId         = $RecipeId
            scheduledTaskId  = $taskRow.scheduledTaskId
            exitCode         = $registrar.exitCode
            durationMs       = $registrar.durationMs
            stdout           = $registrar.stdout
            stderr           = $registrar.stderr
            logPath          = $registrar.logPath
        }
        return
    }

    try {
        [void](Remove-ScheduledTaskRow -RecipeId $RecipeId)
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error    = 'db_write_failed'
            recipeId = $RecipeId
            detail   = [string]$_.Exception.Message
        }
        return
    }

    Write-JsonResponse -Context $Context -Status 200 -Body @{
        ok                  = $true
        recipeId            = $RecipeId
        scheduledTaskId     = $taskRow.scheduledTaskId
        windowsTaskName     = $taskRow.windowsTaskName
        registrarDurationMs = $registrar.durationMs
    }
}

# ---------------------------------------------------------------------
# Dispatcher
# ---------------------------------------------------------------------

function Invoke-ScheduledTasksRoute {
    # Returns $true if the request was consumed.
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -eq '/api/v1/scheduled-tasks') {
        switch ($method) {
            'GET' { Invoke-ScheduledTasksList -Context $Context; return $true }
            default {
                Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
                return $true
            }
        }
    }

    if ($path -match '^/api/v1/recipes/([^/]+)/scheduled-task$') {
        $recipeId = $matches[1]
        if ($recipeId -notmatch $Script:RecipeIdPattern) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_recipe_id'; recipeId = $recipeId }
            return $true
        }
        switch ($method) {
            'GET'    { Invoke-ScheduledTaskGet    -Context $Context -RecipeId $recipeId; return $true }
            'PUT'    { Invoke-ScheduledTaskPut    -Context $Context -RecipeId $recipeId; return $true }
            'DELETE' { Invoke-ScheduledTaskDelete -Context $Context -RecipeId $recipeId; return $true }
            default {
                Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
                return $true
            }
        }
    }

    return $false
}
