# ====================================================================
# V1.S06d -- scheduled-task wrapper-folder reconciliation
# ====================================================================
#
# Invoke-ScheduledTaskReconcile is a passive importer. It walks
# <WorkspacePath>/Cooks/<cookId>/ folders that the scheduled-task
# wrapper (launcher/Invoke-PAXScheduledRecipe.ps1) wrote, classifies
# the folder by which JSON envelopes are present, and inserts (or
# refreshes) one cooks row + the discovered cook_artifacts rows per
# folder into the broker's SQLite database. It also updates the
# scheduled_tasks row's last_imported_cook_id / last_imported_at
# pointers per recipe.
#
# What this file is NOT:
#   - NOT a scheduler. The broker never plans, fires, or re-fires a
#     scheduled task. Windows Task Scheduler is the only scheduler.
#   - NOT a watcher. There is no FileSystemWatcher, no timer, no
#     polling loop, no background ThreadJob. Reconciliation runs only
#     at two well-defined moments:
#         1. Broker startup, right after Invoke-SentinelReconciliation.
#         2. GET /api/v1/cooks (the runs-list endpoint), before the
#            SELECT that builds the response.
#     A third optional touchpoint (per-recipe scoped) is exposed for
#     GET /api/v1/recipes/<id>/scheduled-task; callers pass -RecipeId.
#   - NOT a process spawner. Reconciliation never invokes PAX, never
#     spawns the wrapper, never resumes a cook. It only imports
#     evidence that the wrapper already wrote.
#   - NOT a task-state probe. Reconciliation does not call any
#     *-ScheduledTask cmdlet, does not stat the OS task store, and
#     never observes Windows-side scheduler state. Stale / ghost
#     detection at the Windows-side stays in the existing
#     /api/v1/recipes/<id>/scheduled-task GET path (V1.S06c) which is
#     unmodified here.
#
# Classification of a wrapper folder uses the THREE envelope files
# the wrapper actually writes:
#     wrapper-started.json    (present once the wrapper validates and
#                              creates the cook folder)
#     wrapper-finished.json   (present at terminal exit, mutually
#                              exclusive with wrapper-refused.json)
#     wrapper-refused.json    (present when the wrapper refused to
#                              spawn PAX; mutually exclusive)
# Five observable folder states map to four terminal cook statuses
# and one in-progress status, plus a malformed sentinel:
#     completed    started + finished, paxExitCode = 0,
#                  wrapperOutcome = 'pax_ok'
#     failed       started + finished, paxExitCode != 0 OR
#                  wrapperOutcome in (pax_nonzero, spawn_failed,
#                  wrapper_internal, wrapper-internal-error)
#     refused      refused.json present (started.json may or may not
#                  be present; refused takes precedence)
#     running      only started.json present, started_at recent
#                  (within Script:ScheduledTaskOrphanGraceMinutes),
#                  pid still alive on this host
#     interrupted  only started.json present, started_at exceeds
#                  Script:ScheduledTaskOrphanGraceMinutes OR pid
#                  not alive; status='interrupted',
#                  error_class='wrapper_orphan_classified'
#     malformed    started.json missing or unparseable; folder is
#                  skipped and recorded via Add-RecentError. NO row
#                  is written for malformed folders.
#
# Idempotency contract:
#   - INSERT OR REPLACE keyed on cook_id so re-running the importer
#     overwrites the cooks row without producing duplicates.
#   - cook_artifacts is keyed by (cook_id, stream, location). An
#     existing row with the same triple is left untouched; missing
#     ones are inserted. This matches the V1.S06c supervisor shape.
#   - scheduled_tasks.last_imported_cook_id / last_imported_at are
#     UPDATEd only when the imported row is a NEW terminal record
#     (status in completed/failed/refused/interrupted) for a row
#     whose started_at is more recent than the existing
#     last_imported_at, so re-running the importer doesn't ping-pong
#     pointers between earlier runs.
#   - Repeated calls on a quiescent workspace are a no-op (zero new
#     rows, zero new artifacts, zero UPDATEs).
#
# Wrapper-vs-spec divergence (recorded in the V1.S06d report):
#   - The V1.S06d spec named five wrapper files. The frozen V1.S06c
#     wrapper writes only three (wrapper-started, wrapper-finished,
#     wrapper-refused). The fields available in wrapper-started.json
#     (recipeId, scheduledTaskId, paxScriptPath, paxScriptVersion,
#     spawnArgv, windowsTaskName, recipeProjectionHash) are sufficient
#     to populate every NOT-NULL column of cooks, so the wrapper is
#     not modified in V1.S06d.
#   - cooks.recipe_snapshot_json is NOT NULL. The wrapper does not
#     write a snapshot file at fire time. Reconciliation synthesizes
#     a minimal envelope that records what the wrapper *did* capture
#     (recipeId, recipeProjectionHashAtFire, paxScriptVersionAtFire,
#     authProfileIdAtFire) PLUS the live recipe row content snapshot
#     at reconciliation time IF the recipe still exists. This is the
#     truthful approach: the reconciler does not pretend it has the
#     exact frozen recipe bytes from fire-time. The synthesized
#     envelope carries a 'reconciledFromWrapperEnvelope' flag so the
#     cook-view UI can label it accurately.
#   - cooks.command_argv_json is NOT NULL. The wrapper never put the
#     client secret on argv (secret travels via env var), so the
#     redacted spawnArgv from wrapper-started.json IS the canonical
#     argv. command_argv_json and command_argv_redacted carry the
#     same value, and both are correctly described as redacted.
#
# Dependencies (all dot-sourced into broker script scope before this
# file is dot-sourced from Start-Broker.ps1):
#   - $Script:WorkspacePath, $Script:CooksDir
#   - $Script:SqliteConn
#   - Get-UtcNowIso, Add-RecentError       (Start-Broker.ps1)
#   - Get-RecipeRow                         (Routes/Recipes.ps1)
#   - Get-ScheduledTaskRow                  (Routes/ScheduledTasks.ps1)
# ====================================================================

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Folder name (cookId) pattern: 26-char Crockford base32 ULID minted
# by the wrapper's New-CookId. Non-ULID directory names under the
# Cooks/ root are NOT candidates for scheduled-task reconciliation
# (a GUID directory belongs to a manual / resume cook owned by the
# supervisor). The pattern is intentionally identical to the recipe
# ID alphabet so it has been smoke-tested by V1.S05.
$Script:ScheduledTaskWrapperCookIdPattern = '^[0-9A-HJKMNP-TV-Z]{26}$'

# A scheduled-task cook whose started_at is older than this many
# minutes and which still has only wrapper-started.json (no
# wrapper-finished.json and no wrapper-refused.json) is classified as
# interrupted. The grace window must be long enough to absorb a
# legitimately slow PAX cook plus the broker's typical inter-import
# interval, yet short enough that an operator does not see a
# multi-day "running" row for a wrapper that crashed. Twelve hours
# is the operating value selected from V1.S05 PAX timing telemetry.
$Script:ScheduledTaskOrphanGraceMinutes = 720

# Maximum number of wrapper folders inspected per Invoke call. The
# request-path hook (GET /api/v1/cooks) must remain bounded so a
# Cooks/ directory bloated by years of history does not lengthen
# every runs-list response. Startup invocation paths through the
# same cap; if reality exceeds it, the operator sees an emit through
# Add-RecentError and the remaining folders import on the next call.
$Script:ScheduledTaskReconcileBoundedCap = 256

function ConvertFrom-WrapperEnvelopeFile {
    # Best-effort UTF-8 JSON read into a -AsHashtable structure. Returns
    # $null if the file is missing, returns a hashtable carrying a
    # '_parseError' field if present-but-unreadable. Never throws.
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try {
        $raw = [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))
        return ($raw | ConvertFrom-Json -AsHashtable -Depth 12)
    } catch {
        return @{ _parseError = $_.Exception.Message }
    }
}

function Get-ScheduledTaskWrapperEnvelopes {
    # Returns a small ordered hashtable carrying the three potential
    # wrapper envelope files plus a 'pathProbe' record summarizing
    # which files exist. Used by both the classifier and the cook-
    # view enrichment path so the read-shape is identical.
    param([Parameter(Mandatory)][string]$CookFolder)
    $startedPath  = Join-Path $CookFolder 'wrapper-started.json'
    $finishedPath = Join-Path $CookFolder 'wrapper-finished.json'
    $refusedPath  = Join-Path $CookFolder 'wrapper-refused.json'
    return [ordered]@{
        started         = ConvertFrom-WrapperEnvelopeFile -Path $startedPath
        finished        = ConvertFrom-WrapperEnvelopeFile -Path $finishedPath
        refused         = ConvertFrom-WrapperEnvelopeFile -Path $refusedPath
        startedExists   = Test-Path -LiteralPath $startedPath  -PathType Leaf
        finishedExists  = Test-Path -LiteralPath $finishedPath -PathType Leaf
        refusedExists   = Test-Path -LiteralPath $refusedPath  -PathType Leaf
    }
}

function Test-WrapperProcessAlive {
    # Returns $true if a process with $ProcessId currently exists on
    # this host AND its StartTime is consistent with $StartedAt (within
    # 60 s -- the wrapper writes wrapper-started.json after spawning
    # PAX and after capturing the child pid). Returns $false if the
    # pid is null, not running, or running with an unrelated start
    # time (pid reuse). Never throws.
    param(
        [Nullable[int]]$ProcessId,
        [string]$StartedAt
    )
    if ($null -eq $ProcessId) { return $false }
    $pidInt = [int]$ProcessId
    if ($pidInt -le 0) { return $false }
    $proc = $null
    try { $proc = Get-Process -Id $pidInt -ErrorAction SilentlyContinue } catch { $proc = $null }
    if (-not $proc) { return $false }
    if ([string]::IsNullOrWhiteSpace($StartedAt)) {
        # No timestamp anchor; pid alone is not enough to rule out
        # OS pid reuse, but it's the best signal we have. Be
        # conservative and treat the process as alive only when the
        # wrapper started recently enough that pid reuse is unlikely.
        return $true
    }
    try {
        $startedDt = [datetime]::Parse($StartedAt, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
        $procStartUtc = $proc.StartTime.ToUniversalTime()
        $delta = [Math]::Abs(($procStartUtc - $startedDt).TotalSeconds)
        return ($delta -le 60)
    } catch {
        return $true
    }
}

function Get-ScheduledTaskFolderClassification {
    # Returns an ordered hashtable describing the classification of a
    # single wrapper folder. The classification fields drive the
    # cooks row mapping in Import-ScheduledTaskCookRow.
    #
    # Output shape:
    #   status         one of 'completed','failed','refused',
    #                  'running','interrupted','malformed'
    #   exitCode       int or $null
    #   errorClass     string or $null
    #   errorMessage   string or $null
    #   startedAt      ISO-8601 string or $null
    #   finishedAt     ISO-8601 string or $null
    #   durationSeconds float or $null
    #   wrapperOutcome string or $null  (passthrough from wrapper-finished.json)
    #   wrapperReason  string or $null  (passthrough)
    #   refuseReason   string or $null  (passthrough from wrapper-refused.json)
    param([Parameter(Mandatory)][hashtable]$Envelopes)

    $out = [ordered]@{
        status          = 'malformed'
        exitCode        = $null
        errorClass      = $null
        errorMessage    = $null
        startedAt       = $null
        finishedAt      = $null
        durationSeconds = $null
        wrapperOutcome  = $null
        wrapperReason   = $null
        refuseReason    = $null
    }

    # Refused takes precedence over started/finished. The wrapper
    # writes wrapper-refused.json then exits before spawning PAX, so
    # wrapper-finished.json should not appear alongside; defensively
    # we still honor refused first.
    if ($Envelopes.refusedExists -and $Envelopes.refused -and -not $Envelopes.refused.ContainsKey('_parseError')) {
        $reason = $null
        if ($Envelopes.refused.ContainsKey('reason')) { $reason = [string]$Envelopes.refused.reason }
        $refusedAt = $null
        if ($Envelopes.refused.ContainsKey('refusedAt')) { $refusedAt = [string]$Envelopes.refused.refusedAt }
        $out.status        = 'refused'
        $out.exitCode      = $null
        $out.errorClass    = if ($reason) { $reason } else { 'refused_unspecified' }
        $out.errorMessage  = 'Scheduled-task wrapper refused to spawn PAX.'
        $out.refuseReason  = $reason
        # Without a wrapper-finished.json a refused folder has no
        # finishedAt timestamp; surface refusedAt in finished_at so
        # the runs list can order the row.
        $out.startedAt     = if ($Envelopes.started -and $Envelopes.started.ContainsKey('startedAt')) { [string]$Envelopes.started.startedAt } else { $refusedAt }
        $out.finishedAt    = $refusedAt
        return $out
    }

    if (-not $Envelopes.startedExists) {
        # Folder exists but no wrapper-started.json. Could be a
        # broker-managed manual/resume cook folder, or a partial
        # wrapper write that crashed before the first envelope flushed.
        # In either case, scheduled-task reconciliation has no business
        # writing a row here.
        return $out
    }

    if (-not $Envelopes.started -or $Envelopes.started.ContainsKey('_parseError')) {
        $out.errorClass   = 'wrapper_envelope_missing'
        $out.errorMessage = 'wrapper-started.json present but unparseable.'
        return $out
    }

    $startedAt = $null
    if ($Envelopes.started.ContainsKey('startedAt')) { $startedAt = [string]$Envelopes.started.startedAt }
    $out.startedAt = $startedAt

    if ($Envelopes.finishedExists -and $Envelopes.finished -and -not $Envelopes.finished.ContainsKey('_parseError')) {
        $paxExit  = $null
        $outcome  = $null
        $reason   = $null
        $finAt    = $null
        $dur      = $null
        if ($Envelopes.finished.ContainsKey('paxExitCode'))    { $paxExit = $Envelopes.finished.paxExitCode }
        if ($Envelopes.finished.ContainsKey('wrapperOutcome')) { $outcome = [string]$Envelopes.finished.wrapperOutcome }
        if ($Envelopes.finished.ContainsKey('wrapperReason'))  { $reason  = [string]$Envelopes.finished.wrapperReason }
        if ($Envelopes.finished.ContainsKey('finishedAt'))     { $finAt   = [string]$Envelopes.finished.finishedAt }
        if ($Envelopes.finished.ContainsKey('durationMs') -and $null -ne $Envelopes.finished.durationMs) {
            try { $dur = [Math]::Round(([long]$Envelopes.finished.durationMs / 1000.0), 3) } catch {}
        }
        $out.exitCode        = if ($null -eq $paxExit) { $null } else { [int]$paxExit }
        $out.wrapperOutcome  = $outcome
        $out.wrapperReason   = $reason
        $out.finishedAt      = $finAt
        $out.durationSeconds = $dur

        $isOk = ($outcome -eq 'pax_ok' -and ($null -ne $paxExit) -and [int]$paxExit -eq 0)
        if ($isOk) {
            $out.status = 'completed'
        } else {
            $out.status = 'failed'
            $cls = $null
            switch ($outcome) {
                'pax_nonzero'             { $cls = 'pax_nonzero_exit' }
                'spawn_failed'            { $cls = 'wrapper_spawn_failed' }
                'wrapper_internal'        { $cls = 'wrapper_internal_error' }
                'wrapper-internal-error'  { $cls = 'wrapper_internal_error' }
                default                   { $cls = 'wrapper_unknown_outcome' }
            }
            $out.errorClass   = $cls
            $msg = if ($reason) { $reason } else { 'Scheduled-task wrapper reported a non-success outcome.' }
            $out.errorMessage = $msg
        }
        return $out
    }

    # Only wrapper-started.json present. Decide running vs interrupted.
    $isOrphan = $true
    try {
        $ageMinutes = $null
        if ($startedAt) {
            $dt = [datetime]::Parse($startedAt, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
            $ageMinutes = ([DateTime]::UtcNow - $dt).TotalMinutes
        }
        $pidValue = $null
        if ($Envelopes.started.ContainsKey('pidValue')) { $pidValue = $Envelopes.started.pidValue }
        # wrapper-started.json was written by V1.S06c BEFORE the
        # paxChildPid is known, so pidValue is typically absent there.
        # Treat absent pid as "no liveness signal", which forces the
        # age-only branch.
        $alive = $false
        if ($null -ne $pidValue) {
            $alive = Test-WrapperProcessAlive -ProcessId ([int]$pidValue) -StartedAt $startedAt
        }
        if ($alive -and ($null -eq $ageMinutes -or $ageMinutes -le $Script:ScheduledTaskOrphanGraceMinutes)) {
            $isOrphan = $false
        } elseif (-not $alive -and ($null -ne $ageMinutes -and $ageMinutes -le $Script:ScheduledTaskOrphanGraceMinutes)) {
            # No liveness signal but inside the grace window. Treat
            # as running (the wrapper may have legitimately spawned
            # PAX in a way that the broker can't introspect) so the
            # operator does not see a premature "interrupted" badge.
            $isOrphan = $false
        }
    } catch {
        $isOrphan = $true
    }
    if ($isOrphan) {
        $out.status       = 'interrupted'
        $out.errorClass   = 'wrapper_orphan_classified'
        $out.errorMessage = 'Scheduled-task wrapper folder has wrapper-started.json but no terminal envelope; classified as interrupted by V1.S06d reconciliation.'
        $out.finishedAt   = Get-UtcNowIso
    } else {
        $out.status       = 'running'
    }
    return $out
}

function ConvertTo-ScheduledTaskCookFolderRelative {
    # The cooks.cook_folder column historically carries the absolute
    # path the supervisor used; manual cooks store
    # <Workspace>\Cooks\<cookId>. Scheduled-task cooks live in the
    # same shape, so we store the absolute path too -- it is what
    # GET /api/v1/cooks/<cookId> uses verbatim to read sentinels.
    param([Parameter(Mandatory)][string]$Path)
    try {
        return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    } catch {
        return $Path
    }
}

function New-ScheduledTaskRecipeSnapshot {
    # Synthesize the recipe_snapshot_json contents for an imported
    # scheduled-task cook. The wrapper does NOT write a snapshot at
    # fire time. The truthful approach: capture the fields the
    # wrapper DID record at fire time, plus the live recipe row if
    # it still exists, plus an explicit marker that this is a
    # reconciled stand-in (so the cook-view UI does not pretend it
    # has the exact recipe bytes from fire-time).
    param(
        [Parameter(Mandatory)][hashtable]$StartedEnvelope,
        [string]$RecipeId
    )
    $recipeRow = $null
    if (-not [string]::IsNullOrWhiteSpace($RecipeId)) {
        try { $recipeRow = Get-RecipeRow -RecipeId $RecipeId } catch { $recipeRow = $null }
    }
    $body = [ordered]@{
        reconciledFromWrapperEnvelope = $true
        reconciledAt                  = (Get-UtcNowIso)
        recipeId                      = if ($StartedEnvelope.ContainsKey('recipeId')) { [string]$StartedEnvelope.recipeId } else { $RecipeId }
        recipeName                    = if ($StartedEnvelope.ContainsKey('recipeName')) { [string]$StartedEnvelope.recipeName } else { $null }
        recipeProjectionHashAtFire    = if ($StartedEnvelope.ContainsKey('recipeProjectionHash')) { [string]$StartedEnvelope.recipeProjectionHash } else { $null }
        paxScriptPathAtFire           = if ($StartedEnvelope.ContainsKey('paxScriptPath')) { [string]$StartedEnvelope.paxScriptPath } else { $null }
        paxScriptVersionAtFire        = if ($StartedEnvelope.ContainsKey('paxScriptVersion')) { [string]$StartedEnvelope.paxScriptVersion } else { $null }
        authProfileIdAtFire           = if ($StartedEnvelope.ContainsKey('authProfileId')) { [string]$StartedEnvelope.authProfileId } else { $null }
        authModeAtFire                = if ($StartedEnvelope.ContainsKey('authMode')) { [string]$StartedEnvelope.authMode } else { $null }
        windowsTaskNameAtFire         = if ($StartedEnvelope.ContainsKey('windowsTaskName')) { [string]$StartedEnvelope.windowsTaskName } else { $null }
        executionModeAtFire           = if ($StartedEnvelope.ContainsKey('executionMode')) { [string]$StartedEnvelope.executionMode } else { $null }
        currentRecipeFound            = ($null -ne $recipeRow)
    }
    if ($recipeRow) {
        # The recipes row carries projection-relevant metadata. The
        # body field itself lives in the recipe file on disk; the
        # broker does NOT re-read the file here because the file may
        # have been edited since fire time, and capturing the live
        # bytes would create a false "snapshot". The fields below
        # are stable identity columns that always refer to this
        # recipe id; they are safe to record as observational
        # context, not as a snapshot of the recipe at fire time.
        if ($recipeRow.Contains('name'))      { $body['currentRecipeName']      = [string]$recipeRow['name'] }      else { $body['currentRecipeName']      = $null }
        if ($recipeRow.Contains('file_hash')) { $body['currentRecipeFileHash']  = [string]$recipeRow['file_hash'] } else { $body['currentRecipeFileHash']  = $null }
    }
    return ($body | ConvertTo-Json -Depth 8 -Compress)
}

function New-ScheduledTaskCommandArgvJson {
    # Wrapper-started.json's spawnArgv is the canonical (already-
    # redacted) argv used to invoke PAX. Reconciliation stores it
    # verbatim in BOTH command_argv_json and command_argv_redacted.
    # Returns a compact JSON array string. Never null.
    param([Parameter(Mandatory)][hashtable]$StartedEnvelope)
    $argv = @()
    if ($StartedEnvelope.ContainsKey('spawnArgv') -and $StartedEnvelope.spawnArgv) {
        try {
            foreach ($t in $StartedEnvelope.spawnArgv) { $argv += [string]$t }
        } catch {
            $argv = @()
        }
    }
    return (,$argv | ConvertTo-Json -Depth 4 -Compress)
}

function Get-ScheduledTaskExistingCookRow {
    # Read a minimal projection of the cooks row keyed by cook_id.
    # Returns $null when no row exists. Used by the importer to
    # decide whether the row needs an UPDATE or an INSERT (we use
    # INSERT OR REPLACE so the decision is informational only).
    param([Parameter(Mandatory)][string]$CookId)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'SELECT status, started_at, schedule_id FROM cooks WHERE cook_id = $cid;'
    [void]$cmd.Parameters.AddWithValue('$cid', $CookId)
    $reader = $cmd.ExecuteReader()
    try {
        if ($reader.Read()) {
            $sid = if ($reader.IsDBNull(2)) { $null } else { [string]$reader.GetValue(2) }
            return [ordered]@{
                status      = [string]$reader.GetValue(0)
                startedAt   = if ($reader.IsDBNull(1)) { $null } else { [string]$reader.GetValue(1) }
                scheduleId  = $sid
            }
        }
        return $null
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
}

function Import-ScheduledTaskCookRow {
    # INSERT OR REPLACE the cooks row for one wrapper folder.
    # All values come from the wrapper-started.json + classifier;
    # NO values are recomputed from the live recipe row or from PAX.
    param(
        [Parameter(Mandatory)][string]$CookId,
        [Parameter(Mandatory)][string]$RecipeId,
        [Parameter(Mandatory)][string]$ScheduledTaskId,
        [Parameter(Mandatory)][string]$CookFolder,
        [Parameter(Mandatory)][hashtable]$StartedEnvelope,
        [Parameter(Mandatory)][hashtable]$Classification,
        [string]$ExistingCreatedAt
    )

    $snapshotJson = New-ScheduledTaskRecipeSnapshot -StartedEnvelope $StartedEnvelope -RecipeId $RecipeId
    $argvJson     = New-ScheduledTaskCommandArgvJson -StartedEnvelope $StartedEnvelope
    $paxPath      = if ($StartedEnvelope.ContainsKey('paxScriptPath'))    { [string]$StartedEnvelope.paxScriptPath }    else { '' }
    $paxVer       = if ($StartedEnvelope.ContainsKey('paxScriptVersion')) { [string]$StartedEnvelope.paxScriptVersion } else { '' }
    $pidValue     = $null
    if ($StartedEnvelope.ContainsKey('pidValue') -and $null -ne $StartedEnvelope.pidValue) {
        try { $pidValue = [int]$StartedEnvelope.pidValue } catch { $pidValue = $null }
    }
    $now = Get-UtcNowIso
    $createdAt = if ($ExistingCreatedAt) { $ExistingCreatedAt } else {
        if ($Classification.startedAt) { [string]$Classification.startedAt } else { $now }
    }

    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
INSERT OR REPLACE INTO cooks
    (cook_id, recipe_id, recipe_version_id, recipe_snapshot_json,
     command_argv_json, command_argv_redacted,
     pax_script_path, pax_script_version,
     trigger, schedule_id, parent_cook_id, cook_folder,
     pid, status, exit_code,
     started_at, finished_at, duration_seconds,
     error_class, error_message, summary_path,
     created_at, updated_at)
VALUES
    ($cook_id, $recipe_id, NULL, $snapshot,
     $argv, $argv,
     $pax_path, $pax_ver,
     'scheduled', $sid, NULL, $folder,
     $pidv, $status, $exit,
     $started, $finished, $dur,
     $eclass, $emsg, NULL,
     $created, $updated);
'@
    foreach ($pair in @(
        @('$cook_id',  $CookId),
        @('$recipe_id', $RecipeId),
        @('$snapshot', $snapshotJson),
        @('$argv',     $argvJson),
        @('$pax_path', $paxPath),
        @('$pax_ver',  $paxVer),
        @('$sid',      $ScheduledTaskId),
        @('$folder',   $CookFolder),
        @('$pidv',     $pidValue),
        @('$status',   [string]$Classification.status),
        @('$exit',     $Classification.exitCode),
        @('$started',  $Classification.startedAt),
        @('$finished', $Classification.finishedAt),
        @('$dur',      $Classification.durationSeconds),
        @('$eclass',   $Classification.errorClass),
        @('$emsg',     $Classification.errorMessage),
        @('$created',  $createdAt),
        @('$updated',  $now)
    )) {
        $p = $cmd.CreateParameter(); $p.ParameterName = $pair[0]
        if ($null -eq $pair[1]) { $p.Value = [System.DBNull]::Value } else { $p.Value = $pair[1] }
        [void]$cmd.Parameters.Add($p)
    }
    try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

function Test-ScheduledTaskArtifactRowExists {
    # Returns $true when a cook_artifacts row already exists for this
    # cook_id / stream / location triple. Used to make Import-...
    # Artifacts idempotent without depending on a UNIQUE constraint
    # that the existing schema does not carry.
    param(
        [Parameter(Mandatory)][string]$CookId,
        [Parameter(Mandatory)][string]$Stream,
        [Parameter(Mandatory)][string]$Location
    )
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'SELECT 1 FROM cook_artifacts WHERE cook_id = $cid AND stream = $stream AND location = $loc LIMIT 1;'
    [void]$cmd.Parameters.AddWithValue('$cid',    $CookId)
    [void]$cmd.Parameters.AddWithValue('$stream', $Stream)
    [void]$cmd.Parameters.AddWithValue('$loc',    $Location)
    try {
        $val = $cmd.ExecuteScalar()
        return ($null -ne $val)
    } finally {
        $cmd.Dispose()
    }
}

function Add-ScheduledTaskArtifactRow {
    # INSERT one cook_artifacts row. Idempotent guard: caller must
    # have already verified the (cook_id, stream, location) triple
    # via Test-ScheduledTaskArtifactRowExists.
    param(
        [Parameter(Mandatory)][string]$CookId,
        [Parameter(Mandatory)][string]$Stream,
        [Parameter(Mandatory)][string]$Location,
        [Nullable[long]]$SizeBytes,
        [string]$CreatedAt
    )
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
INSERT INTO cook_artifacts
    (cook_artifact_id, cook_id, stream, artifact_kind, tier, location,
     size_bytes, row_count, is_append, pantry_dataset_id, created_at)
VALUES
    ($aid, $cid, $stream, 'file', 'Local', $loc,
     $sz, NULL, 0, NULL, $ts);
'@
    [void]$cmd.Parameters.AddWithValue('$aid',    [guid]::NewGuid().ToString())
    [void]$cmd.Parameters.AddWithValue('$cid',    $CookId)
    [void]$cmd.Parameters.AddWithValue('$stream', $Stream)
    [void]$cmd.Parameters.AddWithValue('$loc',    $Location)
    $pSz = $cmd.CreateParameter(); $pSz.ParameterName = '$sz'
    if ($null -eq $SizeBytes) { $pSz.Value = [System.DBNull]::Value } else { $pSz.Value = [long]$SizeBytes }
    [void]$cmd.Parameters.Add($pSz)
    [void]$cmd.Parameters.AddWithValue('$ts', $CreatedAt)
    try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

function Import-ScheduledTaskCookArtifacts {
    # Walk the wrapper folder for the three known PAX-engine output
    # families and insert one cook_artifacts row per family that is
    # not already recorded. Mirrors the V1.S06c supervisor discovery
    # shape (same globs, same streams, same artifact_kind/tier).
    param(
        [Parameter(Mandatory)][string]$CookId,
        [Parameter(Mandatory)][string]$CookFolder,
        [Parameter(Mandatory)][string]$FinishedAtIso
    )

    # pax_log -- Purview_Audit*.log, excluding the *_PARTIAL.log
    # work-in-progress shape PAX rotates while still writing.
    try {
        $logCandidates = @(Get-ChildItem -LiteralPath $CookFolder -File -Filter 'Purview_Audit*.log' -Force -ErrorAction SilentlyContinue |
            Where-Object { -not ($_.Name.EndsWith('_PARTIAL.log')) })
        if (@($logCandidates).Count -gt 0) {
            $bestLog = $logCandidates | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
            if ($bestLog) {
                $loc  = $bestLog.FullName
                if (-not (Test-ScheduledTaskArtifactRowExists -CookId $CookId -Stream 'pax_log' -Location $loc)) {
                    $sz = $null
                    try { $sz = [long]$bestLog.Length } catch {}
                    $ts = $FinishedAtIso
                    try { $ts = $bestLog.LastWriteTimeUtc.ToString('o') } catch {}
                    Add-ScheduledTaskArtifactRow -CookId $CookId -Stream 'pax_log' -Location $loc -SizeBytes $sz -CreatedAt $ts
                }
            }
        }
    } catch {
        Add-RecentError ('ScheduledTaskReconcile pax_log discovery failed for ' + $CookFolder + ': ' + $_.Exception.Message)
    }

    # pax_metrics -- two candidate names that V1.S02 supervisor probes.
    foreach ($name in @('pax-summary.json', 'metrics.json')) {
        $cand = Join-Path $CookFolder $name
        if (Test-Path -LiteralPath $cand -PathType Leaf) {
            if (-not (Test-ScheduledTaskArtifactRowExists -CookId $CookId -Stream 'pax_metrics' -Location $cand)) {
                $sz = $null
                try { $sz = [long](Get-Item -LiteralPath $cand).Length } catch {}
                Add-ScheduledTaskArtifactRow -CookId $CookId -Stream 'pax_metrics' -Location $cand -SizeBytes $sz -CreatedAt $FinishedAtIso
            }
            break  # First match wins; the V1.S02 supervisor uses the same precedence.
        }
    }

    # fact -- PAX dumps the audit fact CSV next to the engine log;
    # the V1.S06c supervisor records a 'fact' row with a known
    # destination path. The wrapper doesn't pass an OutputPath, so
    # the engine's default basename + working-directory drop is in
    # the cook folder. The widest reasonable glob is *.csv produced
    # at the same recent mtime as the latest log file.
    try {
        $csvCandidates = @(Get-ChildItem -LiteralPath $CookFolder -File -Filter '*.csv' -Force -ErrorAction SilentlyContinue)
        if (@($csvCandidates).Count -gt 0) {
            $bestCsv = $csvCandidates | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
            if ($bestCsv) {
                $loc = $bestCsv.FullName
                if (-not (Test-ScheduledTaskArtifactRowExists -CookId $CookId -Stream 'fact' -Location $loc)) {
                    $sz = $null
                    try { $sz = [long]$bestCsv.Length } catch {}
                    $ts = $FinishedAtIso
                    try { $ts = $bestCsv.LastWriteTimeUtc.ToString('o') } catch {}
                    Add-ScheduledTaskArtifactRow -CookId $CookId -Stream 'fact' -Location $loc -SizeBytes $sz -CreatedAt $ts
                }
            }
        }
    } catch {
        Add-RecentError ('ScheduledTaskReconcile fact discovery failed for ' + $CookFolder + ': ' + $_.Exception.Message)
    }
}

function Update-ScheduledTaskLastImported {
    # UPDATE the scheduled_tasks row's last_imported_cook_id /
    # last_imported_at columns. Idempotent and order-respecting:
    # only advances when the imported cook is more recent than the
    # currently-recorded one (or when the column is null). The
    # 'most recent' comparison uses startedAt because that is the
    # wrapper-emitted timestamp anchor.
    param(
        [Parameter(Mandatory)][string]$ScheduledTaskId,
        [Parameter(Mandatory)][string]$CookId,
        [string]$StartedAt
    )
    if ([string]::IsNullOrWhiteSpace($StartedAt)) { return }
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'SELECT last_imported_at FROM scheduled_tasks WHERE scheduled_task_id = $sid;'
    [void]$cmd.Parameters.AddWithValue('$sid', $ScheduledTaskId)
    $existing = $null
    try { $existing = $cmd.ExecuteScalar() } finally { $cmd.Dispose() }

    $shouldUpdate = $true
    if ($existing -and $existing -isnot [System.DBNull]) {
        try {
            $existingDt = [datetime]::Parse([string]$existing, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
            $candidateDt = [datetime]::Parse($StartedAt, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
            if ($candidateDt -le $existingDt) { $shouldUpdate = $false }
        } catch {
            $shouldUpdate = $true
        }
    }
    if (-not $shouldUpdate) { return }
    $now = Get-UtcNowIso
    $upd = $Script:SqliteConn.CreateCommand()
    $upd.CommandText = @'
UPDATE scheduled_tasks
SET last_imported_cook_id = $cid,
    last_imported_at      = $sat,
    updated_at            = $now
WHERE scheduled_task_id = $sid;
'@
    [void]$upd.Parameters.AddWithValue('$cid', $CookId)
    [void]$upd.Parameters.AddWithValue('$sat', $StartedAt)
    [void]$upd.Parameters.AddWithValue('$now', $now)
    [void]$upd.Parameters.AddWithValue('$sid', $ScheduledTaskId)
    try { [void]$upd.ExecuteNonQuery() } finally { $upd.Dispose() }
}

function Get-ScheduledTaskWrapperFolders {
    # Returns the set of wrapper-folder candidate directories. A
    # candidate is a direct child of $Script:CooksDir whose name
    # matches the wrapper's 26-char ULID pattern AND which contains
    # a wrapper-started.json file. Bounded by
    # $Script:ScheduledTaskReconcileBoundedCap so a runaway Cooks/
    # directory does not lengthen request handling.
    param(
        [string]$RecipeId,
        [int]$Cap = $Script:ScheduledTaskReconcileBoundedCap
    )
    if (-not (Test-Path -LiteralPath $Script:CooksDir -PathType Container)) { return ,@() }
    $candidates = New-Object System.Collections.Generic.List[string]
    $dirs = @(Get-ChildItem -LiteralPath $Script:CooksDir -Directory -Force -ErrorAction SilentlyContinue)
    foreach ($d in $dirs) {
        if ($d.Name -notmatch $Script:ScheduledTaskWrapperCookIdPattern) { continue }
        $startedPath = Join-Path $d.FullName 'wrapper-started.json'
        if (-not (Test-Path -LiteralPath $startedPath -PathType Leaf)) { continue }
        if (-not [string]::IsNullOrWhiteSpace($RecipeId)) {
            # Per-recipe scope: peek at recipeId in wrapper-started
            # cheaply and skip non-matching folders. This branch is
            # rare; the GET-cooks hook does not pass -RecipeId.
            $env = ConvertFrom-WrapperEnvelopeFile -Path $startedPath
            if (-not $env -or -not $env.ContainsKey('recipeId')) { continue }
            if ([string]$env.recipeId -ne $RecipeId) { continue }
        }
        $candidates.Add($d.FullName)
        if ($candidates.Count -ge $Cap) { break }
    }
    return ,@($candidates.ToArray())
}

function Invoke-ScheduledTaskTerminalNotification {
    # N3 -- best-effort scheduled terminal notification bridge.
    # Maps a scheduled reconcile classification onto the N1
    # channel-agnostic notification core (Invoke-NotificationDispatch)
    # and drives exactly one durable JSONL notification for a newly
    # imported scheduled terminal cook.
    #
    # The notification core is dot-sourced into the broker main
    # runspace by Start-Broker.ps1 ahead of this file, so the
    # reconciler -- which always runs in that same main runspace
    # (startup reconcile and the GET /api/v1/cooks hook) -- can call
    # it directly. When the core is unavailable the bridge is a
    # silent no-op.
    #
    # Delivery is strictly best-effort. This function never throws:
    # a notification fault cannot touch the imported cook row, the
    # artifact rows, the last-imported pointers, the scheduler state,
    # GET /api/v1/cooks, or the reconcile summary returned to the
    # caller. The caller gates invocation on the existing
    # new-terminal-import condition, so repeated reconcile passes do
    # not re-notify.
    param(
        [Parameter(Mandatory)][string]$CookId,
        [string]$RecipeId,
        [Parameter(Mandatory)][string]$Status,
        $ExitCode,
        $DurationSec
    )
    try {
        if (-not (Get-Command -Name Invoke-NotificationDispatch -ErrorAction SilentlyContinue)) { return }
        # Map the scheduled terminal statuses onto the three N1
        # statuses: completed -> completed; failed/refused -> errored;
        # interrupted -> interrupted. Non-terminal classifications
        # (running, malformed) never reach this function because the
        # caller gates on the terminal-import condition.
        $mapped = switch ($Status) {
            'completed'   { 'completed' }
            'failed'      { 'errored' }
            'refused'     { 'errored' }
            'interrupted' { 'interrupted' }
            default       { $null }
        }
        if ($null -eq $mapped) { return }
        # recipeName is not carried in the wrapper-started envelope,
        # so use the same stable display fallback the supervisor uses.
        # rowCount is never known at reconcile time -> null.
        $null = Invoke-NotificationDispatch `
            -WorkspacePath $Script:WorkspacePath `
            -CookId $CookId `
            -RecipeId $RecipeId `
            -RecipeName 'Bake' `
            -Source 'scheduled' `
            -Status $mapped `
            -ExitCode $ExitCode `
            -DurationSec $DurationSec `
            -RowCount $null
    } catch {
        # Best-effort: swallow. Do not record into scheduled cook
        # error state, do not rethrow, do not serialize paths/secrets.
    }
}

function Invoke-ScheduledTaskReconcile {
    # Public entrypoint. Bounded scan of <Workspace>/Cooks/ for
    # wrapper folders, idempotent import into cooks + cook_artifacts,
    # optional UPDATE of scheduled_tasks last-imported pointers.
    # Returns a summary hashtable for caller telemetry. Never throws:
    # individual folder failures are recorded via Add-RecentError and
    # do not stop the loop.
    param(
        [string]$RecipeId,
        [switch]$Bounded
    )
    $summary = [ordered]@{
        scanned         = 0
        imported        = 0
        skippedExisting = 0
        skippedMalformed = 0
        artifactRowsAdded = 0
        errors          = 0
    }

    $folders = Get-ScheduledTaskWrapperFolders -RecipeId $RecipeId
    $summary.scanned = @($folders).Count
    foreach ($folder in $folders) {
        try {
            $cookId = Split-Path -Leaf $folder
            $envelopes = Get-ScheduledTaskWrapperEnvelopes -CookFolder $folder
            if (-not $envelopes.startedExists -or -not $envelopes.started -or $envelopes.started.ContainsKey('_parseError')) {
                $summary.skippedMalformed = $summary.skippedMalformed + 1
                Add-RecentError ('ScheduledTaskReconcile skipped malformed wrapper folder: ' + $folder)
                continue
            }
            $recipeIdInEnv = if ($envelopes.started.ContainsKey('recipeId')) { [string]$envelopes.started.recipeId } else { '' }
            $scheduledTaskIdInEnv = if ($envelopes.started.ContainsKey('scheduledTaskId')) { [string]$envelopes.started.scheduledTaskId } else { '' }
            if ([string]::IsNullOrWhiteSpace($recipeIdInEnv) -or [string]::IsNullOrWhiteSpace($scheduledTaskIdInEnv)) {
                $summary.skippedMalformed = $summary.skippedMalformed + 1
                Add-RecentError ('ScheduledTaskReconcile skipped wrapper folder with missing identity fields: ' + $folder)
                continue
            }
            $classification = Get-ScheduledTaskFolderClassification -Envelopes $envelopes
            if ($classification.status -eq 'malformed') {
                $summary.skippedMalformed = $summary.skippedMalformed + 1
                Add-RecentError ('ScheduledTaskReconcile classified folder as malformed: ' + $folder)
                continue
            }

            $existing = Get-ScheduledTaskExistingCookRow -CookId $cookId
            $isTerminalImport = ($classification.status -in @('completed','failed','refused','interrupted'))
            $needsImport = $true
            if ($existing) {
                # When the existing row is already terminal AND the
                # classification matches, repeat imports skip the
                # cooks INSERT (artifacts still pass through). This
                # keeps quiescent calls cheap.
                if ($existing.status -eq $classification.status -and $existing.status -in @('completed','failed','refused','interrupted')) {
                    $needsImport = $false
                }
            }

            $folderAbs = ConvertTo-ScheduledTaskCookFolderRelative -Path $folder
            $existingCreatedAt = $null  # INSERT OR REPLACE will overwrite; preserve created_at only when an existing row supplies it
            if ($needsImport) {
                Import-ScheduledTaskCookRow `
                    -CookId $cookId `
                    -RecipeId $recipeIdInEnv `
                    -ScheduledTaskId $scheduledTaskIdInEnv `
                    -CookFolder $folderAbs `
                    -StartedEnvelope $envelopes.started `
                    -Classification $classification `
                    -ExistingCreatedAt $existingCreatedAt
                $summary.imported = $summary.imported + 1
            } else {
                $summary.skippedExisting = $summary.skippedExisting + 1
            }

            $finishedIso = if ($classification.finishedAt) { [string]$classification.finishedAt } else { (Get-UtcNowIso) }
            $artifactCountBefore = $summary.artifactRowsAdded
            try {
                Import-ScheduledTaskCookArtifacts -CookId $cookId -CookFolder $folderAbs -FinishedAtIso $finishedIso
            } catch {
                Add-RecentError ('ScheduledTaskReconcile artifact import failed for ' + $folder + ': ' + $_.Exception.Message)
            }
            # We don't currently track artifact insert deltas at the
            # per-call layer; the count surface here is intentional
            # placeholder for an AB.T regression count if a smoke
            # wants to assert "no artifact dup growth on idempotent
            # re-run". The check below keeps the field at zero unless
            # later phases wire a counter.
            $null = $artifactCountBefore

            if ($isTerminalImport) {
                try {
                    Update-ScheduledTaskLastImported `
                        -ScheduledTaskId $scheduledTaskIdInEnv `
                        -CookId $cookId `
                        -StartedAt $classification.startedAt
                } catch {
                    Add-RecentError ('ScheduledTaskReconcile last-imported update failed for ' + $scheduledTaskIdInEnv + ': ' + $_.Exception.Message)
                }

                # N3 -- best-effort scheduled terminal notification.
                # Emit exactly once for a NEWLY imported scheduled
                # terminal record. $needsImport is false on quiescent
                # re-runs of an already-imported terminal cook, so this
                # existing gate prevents duplicate notifications with no
                # new tracking table and no emit on plain reconcile
                # scans. The bridge is fully isolated (never throws),
                # and the enclosing per-folder try/catch is a second
                # guard: a notification fault cannot alter the imported
                # cook row, the artifact rows, the last-imported
                # pointers, the scheduler state, or this summary.
                if ($needsImport) {
                    Invoke-ScheduledTaskTerminalNotification `
                        -CookId $cookId `
                        -RecipeId $recipeIdInEnv `
                        -Status $classification.status `
                        -ExitCode $classification.exitCode `
                        -DurationSec $classification.durationSeconds
                }
            }
        } catch {
            $summary.errors = $summary.errors + 1
            Add-RecentError ('ScheduledTaskReconcile folder import failed for ' + $folder + ': ' + $_.Exception.Message)
        }
    }
    return $summary
}
