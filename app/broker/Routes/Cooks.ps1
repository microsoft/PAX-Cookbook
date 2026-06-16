#requires -Version 7.4

# Cooks.ps1 — HTTP routes for cook lifecycle.
#
#   POST /api/v1/recipes/<recipeId>/cook        -> 201 { cookId }
#   POST /api/v1/cooks/<cookId>/stop            -> 202
#   POST /api/v1/cooks/<cookId>/kill            -> 202
#   GET  /api/v1/cooks?recipeId=&status=        -> 200 { cooks: [...] }
#   GET  /api/v1/cooks/<cookId>                 -> 200 { cook, context, sentinels, artifacts }
#   GET  /api/v1/cooks/<cookId>/log             -> 200 text/plain (whole file)
#
# Persistence authority chain (filesystem authoritative, SQLite operational):
#
#   <Workspace>/Cooks/<recipeId>/<cookId>/
#     recipe-snapshot.json   point-in-time recipe at cook-start. Frozen.
#                            Authoritative answer to "what recipe was
#                            executed?". Carries the recipe's createdBy
#                            provenance block (see Recipes.ps1).
#     cook-context.json      point-in-time RUNTIME identity at cook-start
#                            (cookbookVersion, bundledPaxVersion,
#                            releaseChannel, bundled PAX path + sha256,
#                            host, trigger). Frozen. Authoritative answer
#                            to "what runtime executed this cook?".
#                            Written by broker dispatch before spawn so
#                            it survives spawn failures.
#     command.txt            rendered PAX argv string. Frozen. Mirrors
#                            plan.paxCommand from Get-PaxInvocationPlan.
#     command-argv.json      structured projection record. Frozen.
#                            JSON object:
#                              { paxArgv: [...],            # plan.paxArgv
#                                extraArguments: "...",     # trimmed user trailer
#                                spawnArgv: [...],          # plan.spawnArgv
#                                paxScriptPath: "..." }     # plan.paxScriptPath
#                            All four fields are emitted from a single
#                            Get-PaxInvocationPlan call at cook-start.
#                            Authoritative answer to "what argv was
#                            projected from this recipe?". Mirrors the
#                            cooks.command_argv_json row content.
#     started.json           supervisor sentinel: process actually started.
#     finished.json          supervisor sentinel: process exited cleanly.
#     interrupted.json       supervisor sentinel: cancel/spawn-failure/
#                            orphan reconciliation (synthesized by C5 if
#                            broker crashed mid-cook).
#     cook.log               tee'd stdout + [STDERR]-prefixed stderr.
#
#   SQLite cooks row:
#     Operational index for queryability. Mirrors a subset of the
#     filesystem-authoritative fields above. Never the source of truth
#     for migration or historical archaeology. Forward-looking columns
#     (recipe_version_id, schedule_id, parent_cook_id, summary_path) are
#     intentionally always NULL in M1 -- placeholders for future slices.
#
# Timestamp ownership (deterministic):
#   created_at    broker dispatch INSERT time.
#   started_at    NULL until the supervisor confirms the process actually
#                 started. Non-NULL iff the child PAX process was spawned
#                 successfully (supervisor sets it in the same UPDATE
#                 that records the child pid).
#   finished_at   supervisor terminal UPDATE, or C5 reconciliation on
#                 broker restart.
#   updated_at    every UPDATE.
#
# Lifecycle state model (no implicit transitions):
#   (none)
#     |  INSERT (broker dispatch)
#     v
#   running ---(supervisor: exit 0)----------------> completed
#     |        (supervisor: exit != 0)-------------> errored
#     |        (supervisor: /stop or /kill cancel)-> interrupted
#     |        (supervisor: spawn_failed)----------> interrupted
#     +-------(C5 sentinel reconciliation on broker restart)
#              (reads finished.json)----------------> completed | errored
#              (reads interrupted.json or synth)----> interrupted
#
# Dot-sourced from Start-Broker.ps1; depends on:
#   - $Script:SqliteConn         (open SqliteConnection)
#   - $Script:CooksDir           (workspace Cooks path)
#   - $Script:CookRegistry       (synchronized hashtable; per-cook state)
#   - $Script:WsRegistry         (synchronized hashtable; WS subscribers)
#   - $Script:PaxScriptPath      (bundled PAX path, fixed inside install tree)
#   - $Script:PaxScriptSha256    (cached at broker startup; rehashed per cook)
#   - $Script:PaxScriptVersion   (bundled PAX version, from VERSION.json;
#                                 stamped on every cook row as the version
#                                 that produced the artifacts)
#   - $Script:CookbookVersion    (cookbook semantic version, from VERSION.json;
#                                 stamped into cook-context.json)
#   - $Script:ReleaseChannel     (release channel, from VERSION.json;
#                                 stamped into cook-context.json)
#   - Read-RecipeFile, Get-RecipeRow, Get-RecipeFilePath  (Recipes.ps1)
#   - Test-RecipeAll                                       (RecipeValidator.ps1)
#   - Convert-RecipeToPaxArgv, Get-PaxInvocationPlan       (Adapter.psm1)
#   - Get-UtcNowIso, Write-JsonResponse                    (Start-Broker.ps1)
#   - Get-SqliteConnectionString                           (Start-Broker.ps1)
#   - Start-CookSupervisor, Request-CookCancel             (Cook/Supervisor.ps1)

# Cook IDs come from two sources:
#   - Manual / resume cooks: GUID v4 (36 chars, hyphenated, lowercase)
#     minted by New-CookId below.
#   - Scheduled-task cooks: 26-char Crockford base32 ULID minted by
#     launcher/Invoke-PAXScheduledRecipe.ps1's in-house New-CookId.
#     The wrapper does NOT mint GUIDs because it runs without the
#     broker's process scope; the ULID alphabet matches the recipe-id
#     alphabet so the wrapper carries one ID-generation strategy end
#     to end.
# The pattern below accepts BOTH shapes so the GET / log / stop / kill /
# resume endpoints honor scheduled-task cookIds the V1.S06d reconciler
# imports from <Workspace>\Cooks\<cookId>\ folders. Validation is shape-
# only; the actual lookup against the cooks table is the authority on
# whether a cook exists.
$Script:CookIdPattern      = '^(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9A-HJKMNP-TV-Z]{26})$'
$Script:M1_CookContextSchemaVer = 1

function New-CookId {
    return [guid]::NewGuid().ToString().ToLowerInvariant()
}

# ---------------------------------------------------------------------
# Cook-context block (runtime identity at cook-start)
# ---------------------------------------------------------------------
#
# Get-CookContextBlock returns the bounded runtime-identity structure
# that is frozen into <CookFolder>/cook-context.json at cook-start time.
# This is the cook-side equivalent of the recipe's createdBy block: it
# captures THE RUNTIME THAT EXECUTED THIS COOK, sourced exclusively
# from $Script:* startup state.
#
# Field names mirror the recipe createdBy block where applicable
# (cookbookVersion, bundledPaxVersion, releaseChannel) for cross-
# recognizability.
#
# CONTRACT:
#   - Called ONLY at cook-start (broker dispatch path), BEFORE spawn,
#     so the record exists even if the spawn fails.
#   - NEVER called on UPDATE / reconciliation / load. The on-disk file
#     is the migration-authoritative source.
#   - NEVER infers anything; reads only $Script:* startup state.
#   - Throws if any required source value is missing -- a missing value
#     means the broker startup contract is broken and we must NOT
#     persist an incomplete record.
function Get-CookContextBlock {
    param(
        [Parameter(Mandatory)] [string]$CookId,
        [Parameter(Mandatory)] [string]$RecipeId,
        [Parameter(Mandatory)] [string]$Trigger,
        [Parameter(Mandatory)] [string]$CreatedAt,
        # V1.S03: resume cooks set this to the original interrupted
        # cookId. Manual cooks leave it empty; the key is omitted from
        # the emitted block so legacy readers see no schema drift.
        [string]$ParentCookId = ''
    )
    if ([string]::IsNullOrWhiteSpace($Script:CookbookVersion))  { throw 'Get-CookContextBlock: $Script:CookbookVersion is not set' }
    if ([string]::IsNullOrWhiteSpace($Script:PaxScriptVersion)) { throw 'Get-CookContextBlock: $Script:PaxScriptVersion is not set' }
    if ([string]::IsNullOrWhiteSpace($Script:ReleaseChannel))   { throw 'Get-CookContextBlock: $Script:ReleaseChannel is not set' }
    if ([string]::IsNullOrWhiteSpace($Script:PaxScriptSha256))  { throw 'Get-CookContextBlock: $Script:PaxScriptSha256 is not set' }
    if ([string]::IsNullOrWhiteSpace($Script:PaxScriptPath))    { throw 'Get-CookContextBlock: $Script:PaxScriptPath is not set' }
    $block = [ordered]@{
        schemaVersion = $Script:M1_CookContextSchemaVer
        cookId        = $CookId
        recipeId      = $RecipeId
        createdAt     = $CreatedAt
        trigger       = $Trigger
        createdBy     = [ordered]@{
            cookbookVersion   = $Script:CookbookVersion
            bundledPaxVersion = $Script:PaxScriptVersion
            releaseChannel    = $Script:ReleaseChannel
        }
        bundledPax    = [ordered]@{
            version = $Script:PaxScriptVersion
            sha256  = $Script:PaxScriptSha256
            path    = $Script:PaxScriptPath
        }
        host          = $env:COMPUTERNAME
    }
    if (-not [string]::IsNullOrWhiteSpace($ParentCookId)) {
        $block['parentCookId'] = $ParentCookId
    }
    return $block
}

# ---------------------------------------------------------------------
# Cooks row helpers
# ---------------------------------------------------------------------

function Get-CookRow {
    param([string]$CookId)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
SELECT cook_id, recipe_id, status, exit_code, pid,
       cook_folder, pax_script_path, pax_script_version, trigger,
       started_at, finished_at, duration_seconds,
       error_class, error_message,
       created_at, updated_at,
       summary_path,
       closure_reason, closure_evidence_json, parent_cook_id
FROM cooks WHERE cook_id = $id;
'@
    $p = $cmd.CreateParameter(); $p.ParameterName = '$id'; $p.Value = $CookId; [void]$cmd.Parameters.Add($p)
    $reader = $cmd.ExecuteReader()
    try {
        if (-not $reader.Read()) { return $null }
        # Phase AB: cook_folder is stored workspace-relative for new
        # rows; pre-AB rows hold absolute paths; foreign-prefix rows
        # are preserved as-is. Resolve-CookFolder returns the right
        # absolute path for all three cases.
        return [ordered]@{
            cookId             = $reader.GetString(0)
            recipeId           = if ($reader.IsDBNull(1))  { $null } else { $reader.GetString(1) }
            status             = $reader.GetString(2)
            exitCode           = if ($reader.IsDBNull(3))  { $null } else { [int]$reader.GetValue(3) }
            pid                = if ($reader.IsDBNull(4))  { $null } else { [int]$reader.GetValue(4) }
            cookFolder         = Resolve-CookFolder -Stored $reader.GetString(5)
            paxScriptPath      = $reader.GetString(6)
            paxScriptVersion   = $reader.GetString(7)
            trigger            = $reader.GetString(8)
            startedAt          = if ($reader.IsDBNull(9))  { $null } else { $reader.GetString(9) }
            finishedAt         = if ($reader.IsDBNull(10)) { $null } else { $reader.GetString(10) }
            durationSeconds    = if ($reader.IsDBNull(11)) { $null } else { [double]$reader.GetValue(11) }
            errorClass         = if ($reader.IsDBNull(12)) { $null } else { $reader.GetString(12) }
            errorMessage       = if ($reader.IsDBNull(13)) { $null } else { $reader.GetString(13) }
            createdAt          = $reader.GetString(14)
            updatedAt          = $reader.GetString(15)
            summaryPath        = if ($reader.IsDBNull(16)) { $null } else { $reader.GetString(16) }
            closureReason      = if ($reader.IsDBNull(17)) { $null } else { $reader.GetString(17) }
            closureEvidenceJson = if ($reader.IsDBNull(18)) { $null } else { $reader.GetString(18) }
            parentCookId       = if ($reader.IsDBNull(19)) { $null } else { $reader.GetString(19) }
        }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
}

# V1.S03 -- fetch the raw recipe_snapshot_json for a single cook from
# the cooks table. The snapshot column is the authoritative frozen
# copy of the recipe at the moment of cook-start; resume cooks reuse
# it verbatim so a recipe edited after the parent cook started does
# NOT silently retroactively apply to the resumed run.
function Get-CookRecipeSnapshotJson {
    param([Parameter(Mandatory)] [string]$CookId)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'SELECT recipe_snapshot_json FROM cooks WHERE cook_id = $id;'
    $p = $cmd.CreateParameter(); $p.ParameterName = '$id'; $p.Value = $CookId; [void]$cmd.Parameters.Add($p)
    try {
        $val = $cmd.ExecuteScalar()
        if ($null -eq $val -or [System.DBNull]::Value.Equals($val)) { return $null }
        return [string]$val
    } finally {
        $cmd.Dispose()
    }
}

function Get-CookArtifactsForCook {
    param([string]$CookId)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
SELECT cook_artifact_id, stream, artifact_kind, tier, location,
       size_bytes, row_count, is_append, created_at
FROM cook_artifacts WHERE cook_id = $id
ORDER BY created_at ASC;
'@
    $p = $cmd.CreateParameter(); $p.ParameterName = '$id'; $p.Value = $CookId; [void]$cmd.Parameters.Add($p)
    $reader = $cmd.ExecuteReader()
    $rows = New-Object System.Collections.Generic.List[object]
    try {
        while ($reader.Read()) {
            $rows.Add([ordered]@{
                artifactId   = $reader.GetString(0)
                stream       = $reader.GetString(1)
                artifactKind = $reader.GetString(2)
                tier         = $reader.GetString(3)
                location     = $reader.GetString(4)
                sizeBytes    = if ($reader.IsDBNull(5)) { $null } else { [long]$reader.GetValue(5) }
                rowCount     = if ($reader.IsDBNull(6)) { $null } else { [long]$reader.GetValue(6) }
                isAppend     = [int]$reader.GetValue(7)
                createdAt    = $reader.GetString(8)
            })
        }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
    return $rows.ToArray()
}

function Get-RunningCookIdForRecipe {
    param([string]$RecipeId)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = "SELECT cook_id FROM cooks WHERE recipe_id = `$rid AND status = 'running' LIMIT 1;"
    $p = $cmd.CreateParameter(); $p.ParameterName = '$rid'; $p.Value = $RecipeId; [void]$cmd.Parameters.Add($p)
    $reader = $cmd.ExecuteReader()
    try {
        if ($reader.Read()) { return $reader.GetString(0) } else { return $null }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
}

# Phase AG.C9 -- authoritative snapshot of currently-active cooks.
#
# Returns a structured object the broker uses to refuse runtime-
# disruptive operations (update apply, broker shutdown initiation,
# etc.) when a cook is still running. Doctrine: NEVER silently
# interrupt an active cook to apply convenience. The caller's correct
# response to count>0 is REFUSE the operation, not pre-empt the cook.
#
# Authoritative source: the DB. The in-memory $Script:CookRegistry is
# NOT consulted here -- the DB row carries the cook's started_at, pid,
# recipe_id, and cook_id which are the operator-visible truths. A row
# in 'running' that the registry has forgotten about would still be a
# real running cook (or an orphan reconciled by AG.C5 on startup); in
# either case the truthful response is to surface the row, not hide it.
#
# Returns: hashtable with these keys, ALWAYS present even on error:
#   ok           -- $true if the snapshot was read; $false on probe failure.
#   count        -- integer count of running rows, or $null on probe failure.
#   cooks        -- array of @{ cookId; recipeId; pid; startedAt; ageSeconds;
#                   ageIsAnomalous; ageAnomalyReason }.
#                   Empty array on probe failure. ageSeconds is computed
#                   from started_at when present; otherwise $null.
#   error        -- $null on ok, otherwise a short reason string.
#   detail       -- $null on ok, otherwise the underlying exception message.
#
# Phase AG.C10 -- ageSeconds is now TRUTHFUL (DETECT + REPORT). The
# previous implementation used [Math]::Max(0, ...) which silently
# clamped negative ages to 0 -- that is "evidence smoothing", forbidden
# by the time-semantics doctrine. The current implementation preserves
# the raw signed difference and surfaces an explicit anomaly flag when
# the value is unusual:
#
#   ageAnomalyReason values (frozen 2-value namespace, separate from
#   the broker-level time_anomaly_kind namespace):
#     negative_age   -- nowUtc < started_at. The wall clock has rolled
#                       back since this cook started, or started_at is
#                       in the future. Truthful: the operator should see
#                       this, NOT a smoothed zero.
#     absurdly_old   -- ageSeconds > 7 days. Operationally UNUSUAL for
#                       an active running cook, NOT historically invalid.
#                       Old cooks may be perfectly legitimate evidence;
#                       this flag exists to draw operator attention to a
#                       running row that has been alive a very long time,
#                       not to suggest the data is corrupt.
#
# NEVER throws. Probe failures are truthfully reported as ok=$false
# rather than re-raised: a caller that cannot determine the active-
# cook population must NOT proceed as if there are zero (silent
# interruption); it must surface the uncertainty.
function Get-ActiveCookSnapshot {
    $result = @{
        ok     = $false
        count  = $null
        cooks  = @()
        error  = $null
        detail = $null
    }
    $rows = New-Object System.Collections.Generic.List[object]
    $cmd = $null
    $reader = $null
    # 7 days. Operationally unusual for an active running cook; NOT a
    # claim that the cook is invalid. PAX runs are typically minutes
    # to hours; a week-old running row is worth surfacing.
    $absurdlyOldThresholdSec = 604800
    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = "SELECT cook_id, recipe_id, pid, started_at FROM cooks WHERE status = 'running';"
        $reader = $cmd.ExecuteReader()
        $nowUtc = [datetime]::UtcNow
        while ($reader.Read()) {
            $startedAt = if ($reader.IsDBNull(3)) { $null } else { $reader.GetString(3) }
            $ageSeconds       = $null
            $ageIsAnomalous   = $false
            $ageAnomalyReason = $null
            if ($startedAt) {
                try {
                    $parsed = [datetime]::Parse($startedAt, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
                    # Raw signed difference. NO clamping. NO normalization.
                    # Negative is truthful evidence of wall-clock rollback
                    # since the cook started; the operator must see it.
                    $rawSec = ($nowUtc - $parsed.ToUniversalTime()).TotalSeconds
                    $ageSeconds = [int][Math]::Truncate($rawSec)
                    if ($ageSeconds -lt 0) {
                        $ageIsAnomalous   = $true
                        $ageAnomalyReason = 'negative_age'
                    }
                    elseif ($ageSeconds -gt $absurdlyOldThresholdSec) {
                        $ageIsAnomalous   = $true
                        $ageAnomalyReason = 'absurdly_old'
                    }
                } catch {
                    # started_at parse failure is non-fatal: the row is
                    # still a real running cook. Report age as $null
                    # rather than fabricating one. ageIsAnomalous stays
                    # $false because we are not classifying age -- we
                    # simply could not compute it.
                    $ageSeconds       = $null
                    $ageIsAnomalous   = $false
                    $ageAnomalyReason = $null
                }
            }
            $rows.Add(@{
                cookId           = $reader.GetString(0)
                recipeId         = if ($reader.IsDBNull(1)) { $null } else { $reader.GetString(1) }
                pid              = if ($reader.IsDBNull(2)) { $null } else { [int]$reader.GetValue(2) }
                startedAt        = $startedAt
                ageSeconds       = $ageSeconds
                ageIsAnomalous   = $ageIsAnomalous
                ageAnomalyReason = $ageAnomalyReason
            }) | Out-Null
        }
        $result.ok     = $true
        $result.count  = $rows.Count
        $result.cooks  = $rows.ToArray()
    } catch {
        $result.ok     = $false
        $result.count  = $null
        $result.cooks  = @()
        $result.error  = 'active_cook_snapshot_failed'
        $result.detail = $_.Exception.Message
    } finally {
        if ($reader) { try { $reader.Dispose() } catch {} }
        if ($cmd)    { try { $cmd.Dispose() }    catch {} }
    }
    return $result
}

function Get-CooksFiltered {
    # Filters: optional recipeId, optional status. No row cap (per user
    # preference: never add artificial limits).
    param([string]$RecipeId, [string]$Status)
    $where = New-Object System.Collections.Generic.List[string]
    $params = @{}
    if ($RecipeId) { $where.Add('recipe_id = $rid'); $params['$rid'] = $RecipeId }
    if ($Status)   { $where.Add('status = $s');     $params['$s']   = $Status }
    $sql = @'
SELECT cook_id, recipe_id, status, exit_code, pid,
       started_at, finished_at, duration_seconds,
       trigger, error_class, created_at, updated_at,
       cook_folder, pax_script_version,
       closure_reason, closure_evidence_json, parent_cook_id
FROM cooks
'@
    if ($where.Count -gt 0) { $sql += "`nWHERE " + ($where -join ' AND ') }
    $sql += "`nORDER BY COALESCE(started_at, created_at) DESC;"

    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = $sql
    foreach ($k in $params.Keys) {
        $p = $cmd.CreateParameter(); $p.ParameterName = $k; $p.Value = $params[$k]; [void]$cmd.Parameters.Add($p)
    }
    $reader = $cmd.ExecuteReader()
    $rows = New-Object System.Collections.Generic.List[object]
    try {
        while ($reader.Read()) {
            $rows.Add([ordered]@{
                cookId           = $reader.GetString(0)
                recipeId         = if ($reader.IsDBNull(1))  { $null } else { $reader.GetString(1) }
                status           = $reader.GetString(2)
                exitCode         = if ($reader.IsDBNull(3))  { $null } else { [int]$reader.GetValue(3) }
                pid              = if ($reader.IsDBNull(4))  { $null } else { [int]$reader.GetValue(4) }
                startedAt        = if ($reader.IsDBNull(5))  { $null } else { $reader.GetString(5) }
                finishedAt       = if ($reader.IsDBNull(6))  { $null } else { $reader.GetString(6) }
                durationSeconds  = if ($reader.IsDBNull(7))  { $null } else { [double]$reader.GetValue(7) }
                trigger          = $reader.GetString(8)
                errorClass       = if ($reader.IsDBNull(9))  { $null } else { $reader.GetString(9) }
                createdAt        = $reader.GetString(10)
                updatedAt        = $reader.GetString(11)
                # Phase AB: resolve workspace-relative cook_folder.
                cookFolder       = Resolve-CookFolder -Stored $reader.GetString(12)
                paxScriptVersion = $reader.GetString(13)
                closureReason       = if ($reader.IsDBNull(14)) { $null } else { $reader.GetString(14) }
                closureEvidenceJson = if ($reader.IsDBNull(15)) { $null } else { $reader.GetString(15) }
                parentCookId        = if ($reader.IsDBNull(16)) { $null } else { $reader.GetString(16) }
            })
        }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
    return $rows.ToArray()
}

function Add-CookRow {
    # INSERT a 'running' cook row at start time. The supervisor will
    # UPDATE it on terminal transition. The supervisor also sets
    # started_at when the child process actually spawns; at INSERT time
    # started_at is NULL (so a row with status='running' AND
    # started_at IS NULL means the broker has dispatched a supervisor
    # but the process has not yet been confirmed spawned).
    #
    # V1.S03: parent_cook_id is optional. Manual cooks (trigger='manual')
    # leave it null. Resume cooks (trigger='resume') populate it with
    # the original interrupted cook id; the FK has ON DELETE SET NULL
    # so the lineage column does not block parent-cook deletion.
    param([hashtable]$Row)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
INSERT INTO cooks
    (cook_id, recipe_id, recipe_snapshot_json, command_argv_json, command_argv_redacted,
     pax_script_path, pax_script_version, trigger, cook_folder,
     parent_cook_id,
     status, started_at, created_at, updated_at)
VALUES
    ($cook_id, $recipe_id, $snapshot, $argv, $argv_redacted,
     $pax_path, $pax_ver, $trigger, $cook_folder,
     $parent_cook_id,
     'running', $started_at, $created_at, $updated_at);
'@
    $parentCookId = $null
    if ($Row.ContainsKey('parent_cook_id')) { $parentCookId = $Row.parent_cook_id }
    foreach ($pair in @(
        @('$cook_id',        $Row.cook_id),
        @('$recipe_id',      $Row.recipe_id),
        @('$snapshot',       $Row.recipe_snapshot_json),
        @('$argv',           $Row.command_argv_json),
        @('$argv_redacted',  $Row.command_argv_redacted),
        @('$pax_path',       $Row.pax_script_path),
        @('$pax_ver',        $Row.pax_script_version),
        @('$trigger',        $Row.trigger),
        @('$cook_folder',    $Row.cook_folder),
        @('$parent_cook_id', $parentCookId),
        @('$started_at',     $Row.started_at),
        @('$created_at',     $Row.created_at),
        @('$updated_at',     $Row.updated_at)
    )) {
        $p = $cmd.CreateParameter(); $p.ParameterName = $pair[0]
        if ($null -eq $pair[1]) { $p.Value = [System.DBNull]::Value } else { $p.Value = $pair[1] }
        [void]$cmd.Parameters.Add($p)
    }
    try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

# ---------------------------------------------------------------------
# Sentinel-file readers (best-effort; not all cooks have all sentinels)
# ---------------------------------------------------------------------

function Read-CookSentinels {
    param([string]$CookFolder)
    $names = @('started.json', 'finished.json', 'interrupted.json')
    $out = [ordered]@{}
    foreach ($n in $names) {
        $path = Join-Path $CookFolder $n
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            try {
                $raw = [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))
                $out[$n] = ($raw | ConvertFrom-Json -AsHashtable -Depth 8)
            } catch {
                $out[$n] = @{ _parseError = $_.Exception.Message }
            }
        }
    }
    return $out
}

function Read-CookContext {
    # Best-effort read of <CookFolder>/cook-context.json. Returns the
    # parsed hashtable, $null for legacy cooks predating this slice
    # (file simply doesn't exist), or a small error stub if the file
    # exists but cannot be parsed. This is a passive read -- the file
    # is NEVER rewritten, repaired, or synthesized on load. Absence
    # remains an observable historical signal.
    param([string]$CookFolder)
    $path = Join-Path $CookFolder 'cook-context.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try {
        $raw = [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))
        return ($raw | ConvertFrom-Json -AsHashtable -Depth 8)
    } catch {
        return @{ _parseError = $_.Exception.Message }
    }
}

# ---------------------------------------------------------------------
# Cook-folder artifact readers (filesystem-authoritative, never rewritten)
# ---------------------------------------------------------------------
#
# All three readers below are PASSIVE: they read the frozen on-disk
# files written by Invoke-CookStart and never repair, regenerate, or
# reconstruct missing fields. A null return means "this file is not
# present" and is itself an inspectable signal.

function Read-CookRecipeSnapshot {
    # <CookFolder>/recipe-snapshot.json -- the frozen, point-in-time
    # recipe that drove this cook. The authoritative answer to
    # "what recipe was executed?" -- intentionally independent of the
    # CURRENT recipe row, which may have been edited or deleted since.
    param([string]$CookFolder)
    $path = Join-Path $CookFolder 'recipe-snapshot.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try {
        $raw = [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))
        return ($raw | ConvertFrom-Json -AsHashtable -Depth 12)
    } catch {
        return @{ _parseError = $_.Exception.Message }
    }
}

function Read-CookCommandDoc {
    # Combined view of the two frozen command files written at cook
    # start:
    #   <CookFolder>/command-argv.json -> paxArgv, extraArguments,
    #                                     spawnArgv, paxScriptPath
    #   <CookFolder>/command.txt       -> paxCommand
    # The four fields below all derive from the SAME single
    # Get-PaxInvocationPlan call at cook-start, so they cannot drift
    # from the actual subprocess argv. NEVER reconstruct; if either
    # file is missing, the corresponding fields are null.
    param([string]$CookFolder)
    $out = [ordered]@{
        paxArgv          = $null
        extraArguments   = $null
        spawnArgv        = $null
        paxScriptPath    = $null
        paxCommand       = $null
        projectionSource = 'frozen at cook-start by Get-PaxInvocationPlan'
    }
    $argvPath = Join-Path $CookFolder 'command-argv.json'
    if (Test-Path -LiteralPath $argvPath -PathType Leaf) {
        try {
            $raw = [System.IO.File]::ReadAllText($argvPath, [System.Text.UTF8Encoding]::new($false))
            $doc = $raw | ConvertFrom-Json -AsHashtable -Depth 4
            if ($doc.ContainsKey('paxArgv'))        { $out.paxArgv        = @($doc.paxArgv) }
            if ($doc.ContainsKey('extraArguments')) { $out.extraArguments = [string]$doc.extraArguments }
            if ($doc.ContainsKey('spawnArgv'))      { $out.spawnArgv      = @($doc.spawnArgv) }
            if ($doc.ContainsKey('paxScriptPath'))  { $out.paxScriptPath  = [string]$doc.paxScriptPath }
        } catch {
            $out['_argvParseError'] = $_.Exception.Message
        }
    }
    $cmdPath = Join-Path $CookFolder 'command.txt'
    if (Test-Path -LiteralPath $cmdPath -PathType Leaf) {
        try {
            $out.paxCommand = [System.IO.File]::ReadAllText($cmdPath, [System.Text.UTF8Encoding]::new($false))
        } catch {
            $out['_commandReadError'] = $_.Exception.Message
        }
    }
    return $out
}

function Get-CookContextSummary {
    # Pure projection of cook-context.json into the small subset that
    # the run-list surface needs per row: cookbookVersion,
    # bundledPaxVersion, releaseChannel, host. Returns an [ordered]
    # hashtable with each field either a string or $null. Reads
    # cook-context.json once via Read-CookContext.
    param([string]$CookFolder)
    $out = [ordered]@{
        cookbookVersion   = $null
        bundledPaxVersion = $null
        releaseChannel    = $null
        host              = $null
    }
    $ctx = Read-CookContext -CookFolder $CookFolder
    if (-not $ctx) { return $out }
    if ($ctx.ContainsKey('host')) { $out.host = [string]$ctx.host }
    if ($ctx.ContainsKey('createdBy') -and $ctx.createdBy) {
        $cb = $ctx.createdBy
        if ($cb.ContainsKey('cookbookVersion'))   { $out.cookbookVersion   = [string]$cb.cookbookVersion }
        if ($cb.ContainsKey('bundledPaxVersion')) { $out.bundledPaxVersion = [string]$cb.bundledPaxVersion }
        if ($cb.ContainsKey('releaseChannel'))    { $out.releaseChannel    = [string]$cb.releaseChannel }
    }
    return $out
}

function Get-CookSnapshotName {
    # Extract identity.name from the cook's recipe-snapshot.json.
    # This is the AUTHORITATIVE recipe name for historical runs --
    # the current recipes row may have been renamed or deleted, but
    # the snapshot is frozen. Returns $null when the snapshot is
    # absent or unparseable.
    param([string]$CookFolder)
    $snap = Read-CookRecipeSnapshot -CookFolder $CookFolder
    if (-not $snap) { return $null }
    if ($snap.ContainsKey('_parseError')) { return $null }
    if ($snap.ContainsKey('identity') -and $snap.identity -and $snap.identity.ContainsKey('name')) {
        return [string]$snap.identity.name
    }
    return $null
}

function Get-CookFolderArtifactRefs {
    # Filesystem-authoritative references to the per-cook metadata
    # files. Returns an array of [ordered] hashtables, one per known
    # file name, each with { name, path, exists, sizeBytes,
    # modifiedAt }. NO content is read here -- the UI consumes
    # references; the parsed JSON for the four metadata files is
    # delivered inline in the GET /cooks/<id> body via the dedicated
    # Read-* helpers above. cook.log content is fetched separately
    # via GET /cooks/<id>/log.
    param([string]$CookFolder)
    $names = @(
        'recipe-snapshot.json',
        'cook-context.json',
        'command.txt',
        'command-argv.json',
        'started.json',
        'finished.json',
        'interrupted.json',
        'cook.log'
    )
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($n in $names) {
        $p = Join-Path $CookFolder $n
        $entry = [ordered]@{
            name       = $n
            path       = $p
            exists     = $false
            sizeBytes  = $null
            modifiedAt = $null
        }
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            $entry.exists = $true
            try {
                $info = Get-Item -LiteralPath $p
                $entry.sizeBytes  = [long]$info.Length
                $entry.modifiedAt = $info.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
            } catch {}
        }
        $rows.Add($entry)
    }
    return $rows.ToArray()
}

function Get-CookOutputArtifactRefs {
    # Filesystem-authoritative augmentation of cook_artifacts rows
    # with a fresh existence/size check. Cookbook records the path at
    # cook-finish; this helper re-stats so the UI can show whether
    # the file is still on disk and its current size. NEVER reads or
    # parses the artifact's contents.
    param([object[]]$Artifacts)
    if (-not $Artifacts) { return @() }
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($a in $Artifacts) {
        $loc = [string]$a.location
        $exists = $false; $size = $null; $mtime = $null
        if ($loc -and (Test-Path -LiteralPath $loc -PathType Leaf)) {
            $exists = $true
            try {
                $info = Get-Item -LiteralPath $loc
                $size  = [long]$info.Length
                $mtime = $info.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
            } catch {}
        }
        $rows.Add([ordered]@{
            artifactId          = $a.artifactId
            stream              = $a.stream
            artifactKind        = $a.artifactKind
            tier                = $a.tier
            location            = $loc
            recordedSizeBytes   = $a.sizeBytes
            currentSizeBytes    = $size
            existsOnDisk        = $exists
            modifiedAt          = $mtime
            rowCount            = $a.rowCount
            isAppend            = $a.isAppend
            createdAt           = $a.createdAt
        })
    }
    return $rows.ToArray()
}

# ---------------------------------------------------------------------
# Summary / metrics enrichment (V1.S02)
# ---------------------------------------------------------------------
#
# All four helpers below are PASSIVE and null-tolerant. They surface
# whatever output evidence already exists on disk at GET time. They
# never modify the cook folder, never modify the fact destination,
# never invoke PAX, never fail the request. Missing files, empty
# files, and unparseable files are all valid observed states and
# round-trip to the UI as explicit absent / parse-failed signals.
#
# What files are looked at:
#   <CookFolder>/pax-summary.json            -- forward-compatible
#                                                cook-side summary
#                                                emitted by future
#                                                supervisor work. PAX
#                                                does NOT write this
#                                                today.
#   <CookFolder>/metrics.json                -- forward-compatible
#                                                literal-name metrics
#   <FactDestDir>/metrics.json               -- forward-compatible
#                                                fact-sibling metrics
#   <FactDestDir>/<baseName>_metrics_*.json  -- current PAX emission
#                                                shape from
#                                                -EmitMetricsJson
#
# What is NOT done here:
#   - No write-back to the cook folder
#   - No modification of cook_artifacts at GET time
#   - No invocation of PAX
#   - No recursion into subdirectories
#   - No content interpretation beyond JSON parsing + a defensive
#     property pluck for the small set of universally-named keys
#     surfaced by Get-CookMetricSummary

function Read-CookPaxSummary {
    # Best-effort, null-tolerant read of <CookFolder>/pax-summary.json.
    # Returns:
    #   $null                              file absent
    #   @{ _parseError = '<msg>' }         file exists but unparseable
    #   <hashtable>                        successfully parsed object
    # PAX does NOT emit pax-summary.json today; this reader is
    # forward-compatible for future supervisor-side summary emission.
    param([string]$CookFolder)
    if (-not $CookFolder) { return $null }
    $path = Join-Path $CookFolder 'pax-summary.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try {
        $raw = [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return @{ _parseError = 'empty file' }
        }
        return ($raw | ConvertFrom-Json -AsHashtable -Depth 12)
    } catch {
        return @{ _parseError = $_.Exception.Message }
    }
}

function Read-CookMetrics {
    # Best-effort discovery + null-tolerant read of the cook's metrics
    # JSON. Searches the following paths in priority order; the first
    # existing file wins:
    #   1. <CookFolder>/metrics.json
    #   2. <FactDestDir>/metrics.json
    #   3. <FactDestDir>/<factBaseName>_metrics_*.json
    #      (current PAX emission shape from -EmitMetricsJson; picks
    #       the most recently modified match)
    # Returns:
    #   $null                              no candidate file present
    #   @{ _parseError = '<msg>';
    #      _path       = '<path>' }        file exists but unparseable
    #   <hashtable> with _path             successfully parsed object
    param(
        [string]$CookFolder,
        [object[]]$Artifacts
    )
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($CookFolder) {
        $p1 = Join-Path $CookFolder 'metrics.json'
        if (Test-Path -LiteralPath $p1 -PathType Leaf) { $candidates.Add($p1) }
    }
    if ($Artifacts) {
        foreach ($a in $Artifacts) {
            if ($a.stream -ne 'fact') { continue }
            $loc = [string]$a.location
            if (-not $loc) { continue }
            $factDir = $null
            $baseName = $null
            try {
                $factDir  = [System.IO.Path]::GetDirectoryName($loc)
                $baseName = [System.IO.Path]::GetFileNameWithoutExtension($loc)
            } catch {}
            if (-not $factDir) { continue }
            if (-not (Test-Path -LiteralPath $factDir -PathType Container)) { continue }
            $p2 = Join-Path $factDir 'metrics.json'
            if (Test-Path -LiteralPath $p2 -PathType Leaf) { $candidates.Add($p2) }
            if ($baseName) {
                try {
                    $pattern = $baseName + '_metrics_*.json'
                    $matches = Get-ChildItem -LiteralPath $factDir -File -Filter $pattern -ErrorAction SilentlyContinue
                    foreach ($m in ($matches | Sort-Object LastWriteTimeUtc -Descending)) {
                        $candidates.Add($m.FullName)
                    }
                } catch {}
            }
        }
    }
    if ($candidates.Count -eq 0) { return $null }
    $picked = $candidates[0]
    try {
        $raw = [System.IO.File]::ReadAllText($picked, [System.Text.UTF8Encoding]::new($false))
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return @{ _parseError = 'empty file'; _path = $picked }
        }
        $obj = $raw | ConvertFrom-Json -AsHashtable -Depth 12
        if ($obj -is [hashtable]) {
            $obj['_path'] = $picked
            return $obj
        }
        # Top-level JSON arrays or scalars: wrap so the route shape
        # is always a hashtable.
        return @{ _path = $picked; _root = $obj }
    } catch {
        return @{ _parseError = $_.Exception.Message; _path = $picked }
    }
}

function Get-CookDiscoveredOutputFiles {
    # Filesystem-authoritative listing of output-shaped files in the
    # cook folder AND in the fact destination's parent directory.
    # Conservative: in the fact-dest directory, only files whose name
    # shares the fact's base name OR matches a tight metrics pattern
    # are surfaced. Cook-folder discovery excludes the known
    # supervisor metadata files (those already appear in
    # folderArtifacts).
    #
    # No recursion. No file-content reads. Size + mtime + name only.
    # Returns an array of [ordered] hashtables with stable keys:
    #   { name, path, source, kind, sizeBytes, modifiedAt }
    #   source = 'cookFolder' | 'factDestDir'
    #   kind   = 'summary' | 'metrics' | 'output' | 'log'
    param(
        [string]$CookFolder,
        [object[]]$Artifacts
    )
    $results = New-Object System.Collections.Generic.List[object]
    $seen    = @{}

    # Names already surfaced by Get-CookFolderArtifactRefs. Excluded
    # from the discovered-outputs surface to avoid double-counting.
    $metadataNames = @(
        'recipe-snapshot.json',
        'cook-context.json',
        'command.txt',
        'command-argv.json',
        'started.json',
        'finished.json',
        'interrupted.json',
        'cook.log'
    )
    $outputExts = @('.json', '.csv', '.tsv', '.parquet', '.xlsx', '.txt', '.log')

    if ($CookFolder -and (Test-Path -LiteralPath $CookFolder -PathType Container)) {
        try {
            $entries = Get-ChildItem -LiteralPath $CookFolder -File -Force -ErrorAction Stop
            foreach ($e in $entries) {
                if ($metadataNames -contains $e.Name) { continue }
                $ext = $e.Extension.ToLowerInvariant()
                if ($outputExts -notcontains $ext) { continue }
                if ($seen.ContainsKey($e.FullName)) { continue }
                $seen[$e.FullName] = $true
                $results.Add(([ordered]@{
                    name       = $e.Name
                    path       = $e.FullName
                    source     = 'cookFolder'
                    kind       = (Get-CookFileKind -Name $e.Name)
                    sizeBytes  = [long]$e.Length
                    modifiedAt = $e.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
                }))
            }
        } catch {}
    }

    if ($Artifacts) {
        foreach ($a in $Artifacts) {
            if ($a.stream -ne 'fact') { continue }
            $loc = [string]$a.location
            if (-not $loc) { continue }
            $factDir = $null
            $baseName = $null
            try {
                $factDir  = [System.IO.Path]::GetDirectoryName($loc)
                $baseName = [System.IO.Path]::GetFileNameWithoutExtension($loc)
            } catch {}
            if (-not $factDir) { continue }
            if (-not (Test-Path -LiteralPath $factDir -PathType Container)) { continue }
            $patterns = New-Object System.Collections.Generic.List[string]
            if ($baseName) { $patterns.Add($baseName + '*') }
            $patterns.Add('*metrics*.json')
            $patterns.Add('pax-summary.json')
            try {
                $factSet = @{}
                foreach ($pattern in $patterns) {
                    $matches = Get-ChildItem -LiteralPath $factDir -File -Filter $pattern -ErrorAction SilentlyContinue
                    foreach ($m in $matches) {
                        if (-not $factSet.ContainsKey($m.FullName)) {
                            $factSet[$m.FullName] = $m
                        }
                    }
                }
                foreach ($e in $factSet.Values) {
                    # The fact dest itself is already in cook_artifacts;
                    # don't duplicate it here.
                    if ($e.FullName -eq $loc) { continue }
                    if ($seen.ContainsKey($e.FullName)) { continue }
                    $seen[$e.FullName] = $true
                    $ext = $e.Extension.ToLowerInvariant()
                    if ($outputExts -notcontains $ext) { continue }
                    $results.Add(([ordered]@{
                        name       = $e.Name
                        path       = $e.FullName
                        source     = 'factDestDir'
                        kind       = (Get-CookFileKind -Name $e.Name)
                        sizeBytes  = [long]$e.Length
                        modifiedAt = $e.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
                    }))
                }
            } catch {}
        }
    }

    return $results.ToArray()
}

function Get-CookFileKind {
    # Conservative name-based categorization. No content inspection.
    param([string]$Name)
    if (-not $Name) { return 'output' }
    $lower = $Name.ToLowerInvariant()
    if ($lower -eq 'pax-summary.json')           { return 'summary' }
    if ($lower -eq 'metrics.json')               { return 'metrics' }
    if ($lower -like '*_metrics_*.json')         { return 'metrics' }
    if ($lower -like '*metrics*.json')           { return 'metrics' }
    if ($lower -like '*.log')                    { return 'log' }
    if ($lower -like '*.csv' -or `
        $lower -like '*.tsv' -or `
        $lower -like '*.parquet' -or `
        $lower -like '*.xlsx')                   { return 'output' }
    if ($lower -like '*.json')                   { return 'output' }
    return 'output'
}

function Get-CookMetricSummary {
    # Defensive property pluck of universally-named keys from the
    # parsed pax-summary / metrics objects. Returns an [ordered]
    # hashtable containing ONLY the keys that resolved to a finite
    # numeric value. Missing or unrecognized fields are omitted, NOT
    # set to null. The UI treats absence as 'this metric was not
    # available' rather than 'this metric was zero'.
    #
    # The lookup intentionally probes a small list of common names
    # used by both the current PAX metrics shape and reasonable
    # future summary shapes. No name on this list is required to be
    # present.
    param($Summary, $Metrics)
    $out = [ordered]@{}

    # The probe candidates are tuples of (source-hashtable, dotted-path).
    # Source is whichever object actually parsed; both are probed.
    $rowCountPaths = @(
        'rowsWritten', 'rows', 'totalRows',
        'records', 'recordCount',
        'TotalStructuredRows', 'TotalRecordsFetched',
        'metrics.rowsWritten', 'metrics.TotalStructuredRows',
        'metrics.TotalRecordsFetched', 'metrics.totalRows'
    )
    $outputCountPaths = @(
        'outputFileCount', 'outputCount',
        'metrics.outputFileCount'
    )
    $warningCountPaths = @(
        'warningCount', 'metrics.warningCount'
    )
    $errorCountPaths = @(
        'errorCount', 'metrics.errorCount'
    )
    $durationPaths = @(
        'durationSeconds', 'durationMs', 'duration',
        'elapsedSeconds', 'elapsedMs',
        'metrics.durationSeconds', 'metrics.durationMs',
        'metrics.elapsedSeconds', 'metrics.elapsedMs'
    )

    $sources = New-Object System.Collections.Generic.List[object]
    if ($Summary -and ($Summary -is [hashtable]) -and -not $Summary.ContainsKey('_parseError')) {
        $sources.Add($Summary)
    }
    if ($Metrics -and ($Metrics -is [hashtable]) -and -not $Metrics.ContainsKey('_parseError')) {
        $sources.Add($Metrics)
    }

    foreach ($probe in @(
        @{ key = 'rowCount';      paths = $rowCountPaths },
        @{ key = 'outputCount';   paths = $outputCountPaths },
        @{ key = 'warningCount';  paths = $warningCountPaths },
        @{ key = 'errorCount';    paths = $errorCountPaths }
    )) {
        foreach ($src in $sources) {
            $val = Get-MetricNumeric -Source $src -Paths $probe.paths
            if ($null -ne $val) { $out[$probe.key] = $val; break }
        }
    }

    foreach ($src in $sources) {
        $val = Get-MetricNumeric -Source $src -Paths $durationPaths
        if ($null -ne $val) { $out['durationSeconds'] = [double]$val; break }
    }

    return $out
}

function Get-MetricNumeric {
    # Walks a dotted path through a hashtable graph and returns the
    # value at that path IF it is a finite numeric. Any other shape
    # (null, missing, string, hashtable, NaN, infinity) returns $null.
    param($Source, [string[]]$Paths)
    if (-not $Source) { return $null }
    foreach ($path in $Paths) {
        $cursor = $Source
        $ok     = $true
        foreach ($segment in $path.Split('.')) {
            if (-not $cursor) { $ok = $false; break }
            if (-not ($cursor -is [hashtable])) { $ok = $false; break }
            if (-not $cursor.ContainsKey($segment)) { $ok = $false; break }
            $cursor = $cursor[$segment]
        }
        if (-not $ok) { continue }
        if ($null -eq $cursor) { continue }
        $n = $null
        try { $n = [double]$cursor } catch { continue }
        if ([double]::IsNaN($n))      { continue }
        if ([double]::IsInfinity($n)) { continue }
        # Coerce integers back to int where possible, so the JSON
        # surface for a row count looks like 12345, not 12345.0.
        $intish = $null
        try { $intish = [long]$cursor } catch {}
        if ($null -ne $intish -and [double]$intish -eq $n) { return $intish }
        return $n
    }
    return $null
}

function Get-CookArtifactRollup {
    # SQL aggregate over cook_artifacts joined back to cooks. Returns
    # a hashtable keyed by cook_id whose value is
    # @{ artifactCount = <int>; rowCount = <int-or-null> }.
    # rowCount is the SUM of row_count across the cook's artifact
    # rows, ignoring NULLs; SQLite returns NULL for an all-NULL group
    # which we surface as $null to the UI.
    $out = @{}
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
SELECT cook_id,
       COUNT(*)        AS art_count,
       SUM(row_count)  AS row_total
FROM cook_artifacts
GROUP BY cook_id;
'@
    $reader = $cmd.ExecuteReader()
    try {
        while ($reader.Read()) {
            $cid = $reader.GetString(0)
            $cnt = if ($reader.IsDBNull(1)) { 0 } else { [int]$reader.GetValue(1) }
            $rt  = if ($reader.IsDBNull(2)) { $null } else { [long]$reader.GetValue(2) }
            $out[$cid] = @{ artifactCount = $cnt; rowCount = $rt }
        }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
    return $out
}

# V1.S03 -- derive resumability for a cook row at request time. The
# resumability verdict is NEVER cached in the cooks table: the
# checkpoint file lives outside the DB and can be moved/deleted by
# the operator at any moment, so the verdict must reflect the live
# filesystem state at the moment the operator asks.
#
# A cook is resumable only when ALL of these hold:
#   - status = 'interrupted'
#   - closure_reason is a chef-stop or broker-restart family code
#     (cancel_kill / clean_exit / nonzero_exit / spawn_failed are
#     EXPLICITLY EXCLUDED -- a force-killed cook may have a torn
#     checkpoint, a clean exit has no checkpoint, an errored cook
#     ran to completion of its own accord, and a spawn failure
#     never wrote a checkpoint to begin with)
#   - closure_evidence_json carries a 'checkpointPath' key with a
#     non-empty string value (recorded by the supervisor at terminal
#     write time per V1.S03's discovery block)
#   - the checkpoint file still exists on disk at this exact moment
#
# Return shape:
#   @{ resumable = <bool>; checkpointPath = <string|null>;
#      reason = <code>; humanReason = <short string> }
#
# `reason` is a stable machine-readable code consumed by the UI to
# render the appropriate badge / refusal message. `humanReason` is a
# short operator-facing description.
$Script:ResumableClosureReasons = @('cancel_stop', 'cancel_stop_escalated_kill')

function Test-CookResumable {
    param([Parameter(Mandatory)] [hashtable]$Row)

    $status         = [string]$Row.status
    $closureReason  = if ($Row.ContainsKey('closureReason')) { [string]$Row.closureReason } else { '' }
    $evidenceJson   = if ($Row.ContainsKey('closureEvidenceJson')) { [string]$Row.closureEvidenceJson } else { '' }

    if ($status -ne 'interrupted') {
        return @{
            resumable      = $false
            checkpointPath = $null
            reason         = 'not_interrupted'
            humanReason    = 'Only interrupted cooks can be resumed.'
        }
    }

    # Closure-reason allowlist. cancel_kill is excluded by design
    # because a force-killed cook may have a torn checkpoint; the
    # operator must start a fresh cook in that case.
    $okClosure = $false
    if ($Script:ResumableClosureReasons -contains $closureReason) {
        $okClosure = $true
    } elseif ($closureReason -and $closureReason.StartsWith('broker_restart_')) {
        $okClosure = $true
    }
    if (-not $okClosure) {
        $code = if ($closureReason) { 'closure_reason_excluded' } else { 'no_closure_reason' }
        return @{
            resumable      = $false
            checkpointPath = $null
            reason         = $code
            humanReason    = ("This cook's closure reason ('" + $closureReason + "') is not eligible for resume.")
        }
    }

    if ([string]::IsNullOrWhiteSpace($evidenceJson)) {
        return @{
            resumable      = $false
            checkpointPath = $null
            reason         = 'no_closure_evidence'
            humanReason    = 'No closure evidence was recorded for this cook.'
        }
    }

    $checkpointPath = $null
    try {
        $obj = $evidenceJson | ConvertFrom-Json -AsHashtable -Depth 6
        if ($obj -is [hashtable] -and $obj.ContainsKey('checkpointPath')) {
            $checkpointPath = [string]$obj.checkpointPath
        }
    } catch {
        return @{
            resumable      = $false
            checkpointPath = $null
            reason         = 'closure_evidence_parse_failed'
            humanReason    = 'Closure evidence JSON could not be parsed.'
        }
    }

    if ([string]::IsNullOrWhiteSpace($checkpointPath)) {
        return @{
            resumable      = $false
            checkpointPath = $null
            reason         = 'no_checkpoint_recorded'
            humanReason    = 'No PAX checkpoint path was recorded for this cook.'
        }
    }

    if (-not (Test-Path -LiteralPath $checkpointPath -PathType Leaf)) {
        return @{
            resumable      = $false
            checkpointPath = $checkpointPath
            reason         = 'checkpoint_missing'
            humanReason    = 'The PAX checkpoint file is no longer on disk.'
        }
    }

    return @{
        resumable      = $true
        checkpointPath = $checkpointPath
        reason         = 'resumable'
        humanReason    = 'This cook is resumable from its PAX checkpoint.'
    }
}

# ---------------------------------------------------------------------
# Route handlers
# ---------------------------------------------------------------------

function Invoke-CookStart {
    param($Context, [string]$RecipeId)

    if ($RecipeId -notmatch $Script:RecipeIdPattern) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_recipe_id'; recipeId = $RecipeId }
        return
    }

    # The request body is intentionally ignored per F-3.a: the on-disk
    # recipe file is authoritative. We re-load and re-validate every
    # time so a recipe edited between create and cook is honored.
    $row = Get-RecipeRow -RecipeId $RecipeId
    if (-not $row -or $row.deleted_at) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found'; recipeId = $RecipeId }
        return
    }
    $recipe = $null
    try {
        $recipe = Read-RecipeFile -RecipeId $RecipeId
    } catch {
        # Unparseable JSON on disk. Treat as a validation failure rather
        # than a 500, because the recipe row still exists; the user can
        # repair the file and retry.
        Write-JsonResponse -Context $Context -Status 412 -Body @{
            error    = 'recipe_invalid'
            recipeId = $RecipeId
            errors   = @(@{ path = ''; message = 'on-disk recipe is unparseable JSON: ' + $_.Exception.Message })
        }
        return
    }
    if ($null -eq $recipe) {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'recipe_file_missing'; recipeId = $RecipeId }
        return
    }
    $verdict = Test-RecipeAll -Recipe $recipe
    if (-not $verdict.ok) {
        Write-JsonResponse -Context $Context -Status 412 -Body @{
            error    = 'recipe_invalid'
            recipeId = $RecipeId
            errors   = $verdict.errors
        }
        return
    }

    # Acquisition gate. In external-engine mode the bundled PAX script
    # is not present until the operator drives the acquisition flow;
    # the integrity check below would otherwise return
    # 'pax_script_integrity' instead of the actionable
    # 'acquisitionRequired' that routes the SPA to the engine overlay.
    if (Invoke-AcquisitionGateOrShortCircuit -Context $Context -Endpoint 'POST /api/v1/recipes/<id>/cook') { return }

    $paxPath = $Script:PaxScriptPath
    if ([string]::IsNullOrWhiteSpace($paxPath) -or -not (Test-Path -LiteralPath $paxPath -PathType Leaf)) {
        # The bundled PAX path is computed by Start-Broker.ps1 from its
        # own install-tree location and the launcher + broker startup
        # both refuse to run without it. Reaching this branch means the
        # file disappeared between broker startup and this cook spawn —
        # treat as a server-side integrity failure, not a user config
        # problem.
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error  = 'pax_script_integrity'
            detail = 'The bundled PAX script is no longer present at the canonical install-tree path. Re-run Install-PAXCookbook.ps1.'
        }
        return
    }

    # Per-cook SHA-256 re-verification of the bundled PAX script.
    # The expected hash was cached by Test-BundledPaxIntegrity at broker
    # startup; rehashing the file here is cheap and protects against any
    # tampering between broker start and now.
    try {
        $cookSpawnSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $paxPath -ErrorAction Stop).Hash.ToUpperInvariant()
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'pax_script_integrity'
            detail  = 'Failed to compute SHA-256 of the bundled PAX script.'
            message = $_.Exception.Message
        }
        return
    }
    if ($cookSpawnSha -ne $Script:PaxScriptSha256) {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error  = 'pax_script_integrity'
            detail = 'The bundled PAX script SHA-256 changed since broker startup. Refusing to spawn. Re-run Install-PAXCookbook.ps1 and restart the broker.'
        }
        return
    }

    # Per-recipe concurrency: 409 with active cookId if recipe already
    # has a running cook. No queue, no fairness, no retry — 409 is the
    # entire contract.
    $existing = Get-RunningCookIdForRecipe -RecipeId $RecipeId
    if ($existing) {
        Write-JsonResponse -Context $Context -Status 409 -Body @{
            error  = 'recipe_busy'
            cookId = $existing
        }
        return
    }

    # Phase AF -- per-op fresh re-auth gate (OpClass 'manualCook').
    # Doctrine: broker-Unlocked is NOT operation approval. Every
    # manual cook MUST be preceded by a fresh Windows Hello / PIN
    # verdict regardless of how recently the operator unlocked.
    # The gate fires here -- AFTER 400/404/412/409 short-circuits so
    # the operator is never prompted to verify a request that would
    # have failed validation -- and BEFORE any plan rendering, auth
    # profile resolution, or secret read so verification IS the very
    # last thing standing between the operator and an actual cook
    # spawn.
    $reAuthMsg = "Verify to start a cook run for recipe '" + [string]$row.name + "'."
    $verdict = Invoke-BrokerLockReAuthForOp -OpClass 'manualCook' -Message $reAuthMsg
    if ($verdict -ne 'Verified') {
        $resp = New-BrokerLockReAuthRequiredResponse -OpClass 'manualCook' -VerificationResult $verdict
        Write-JsonResponse -Context $Context -Status $resp.status -Body $resp.body
        return
    }

    # Phase AG.C7 -- pre-spawn disk-space precheck. Refuses the cook
    # outright (HTTP 507 Insufficient Storage) when the workspace
    # drive is below $Script:MinFreeDiskBytesForCook. Inserted HERE so
    # the refusal happens AFTER all cheap validation + re-auth (the
    # operator gets prompted only once they've cleared every other
    # gate) but BEFORE any auth-profile secret is read into broker
    # memory, BEFORE the cook folder is created, BEFORE the cook row
    # is inserted, and BEFORE the supervisor ThreadJob is spawned --
    # so a refused cook leaves NO partial state on disk, NO row in
    # the cooks table, and NO secret in broker memory. Doctrine:
    # NEVER auto-clean / auto-prune / auto-compact to make room.
    # The operator decides what to delete.
    $diskCheck = Test-CookDiskPrecheck -Path $Script:WorkspacePath
    if (-not $diskCheck.ok) {
        Add-RecentError -Message ("Cook refused for recipe '$RecipeId': " + $diskCheck.reason + ' (' + $diskCheck.detail + ')') -Source 'cook_disk_precheck'
        Write-JsonResponse -Context $Context -Status 507 -Body @{
            error         = 'insufficient_disk_space'
            recipeId      = $RecipeId
            reason        = $diskCheck.reason
            detail        = $diskCheck.detail
            freeBytes     = $diskCheck.freeBytes
            requiredBytes = $diskCheck.requiredBytes
            drive         = $diskCheck.driveName
        }
        return
    }

    # Phase AG.C8 -- pre-spawn path-length precheck. Refuses the cook
    # (HTTP 400) when the to-be-created cook folder, plus a reasonable
    # child-filename budget, would exceed the classic Win32 MAX_PATH
    # boundary of 260 characters. This is a HARD-FLOOR truthful
    # refusal, NOT a path-rewriting "auto-shorten". Doctrine: the
    # operator is told the exact pathLength and the budget so they
    # can either move the workspace closer to the drive root or
    # rename it; Cookbook never silently relocates evidence on the
    # operator's behalf. Inserted HERE so the refusal still happens
    # BEFORE auth-profile secret read, cook folder creation, cook
    # row insert, and supervisor spawn -- a refused cook leaves NO
    # partial state on disk, NO row in the cooks table, and NO
    # secret in broker memory.
    # The 96-char reservation matches Get-WorkspacePathDiagnostic's
    # rationale: 71 chars for "\Cooks\<26-base32>\<36-guid>\" + a
    # 25-char ceiling on the longest expected child filename.
    $effWorkspaceLen = if ($Script:WorkspacePath) { [int]$Script:WorkspacePath.Length } else { 0 }
    if (($effWorkspaceLen + 96) -gt 260) {
        Add-RecentError -Message ("Cook refused for recipe '$RecipeId': workspace_path_too_long (pathLength=$effWorkspaceLen + 96 reserved > 260).") -Source 'cook_path_precheck'
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error              = 'workspace_path_too_long'
            recipeId           = $RecipeId
            workspacePathLength = $effWorkspaceLen
            reservedChildBudget = 96
            classicLimit       = 260
            reason             = 'pre_spawn_path_length_exceeds_max_path'
            detail             = ("The configured workspace path is " + $effWorkspaceLen + " characters long. Each cook folder appends ~71 characters for /Cooks/<recipeId>/<cookId>/ plus up to ~25 characters for the longest child filename, which would exceed the classic Win32 MAX_PATH of 260. Cookbook does NOT auto-shorten or relocate workspaces. Move the workspace closer to the drive root, or rename it.")
        }
        return
    }

    # Render the full invocation plan via the pure adapter. This is the
    # SINGLE call that drives every downstream representation of the
    # command for this cook:
    #   - plan.paxCommand     -> <CookFolder>/command.txt          (preview parity)
    #   - plan.paxArgv        -> <CookFolder>/command-argv.json    (structural form)
    #   - plan.spawnArgv      -> cooks.command_argv_json            (DB row; matches the actual ProcessStartInfo.ArgumentList)
    #   - plan.spawnArgv[3]   -> supervisor -CommandExpr            (literal pwsh `-Command` value)
    # The supervisor consumes spawnArgv[3] verbatim and MUST NOT
    # rebuild it. Projection rejection (e.g. removed-switch in
    # extraArguments) surfaces as an exception here and is mapped to a
    # 412 recipe_invalid response.
    #
    # Phase AF: at the manual-cook entry the trigger is always
    # 'local-manual' regardless of what the recipe's stored
    # executionMode field declares. Recipes tagged for hosted /
    # scheduled runtimes are refused here so manual ad-hoc cooks
    # never silently fall through into a runtime they were not
    # validated against. For App* auth modes the auth-profile row
    # is resolved up-front so the adapter can emit -ClientId (and
    # -ClientCertificateThumbprint for cert mode), and the credential
    # secret is read from CredMan into a SecureString for the
    # supervisor's env-var injection.
    $recipeExecMode = ''
    if ($recipe.ContainsKey('executionMode')) {
        $recipeExecMode = [string]$recipe.executionMode
    }
    if ($recipeExecMode -and $recipeExecMode -notin @('local-manual')) {
        Write-JsonResponse -Context $Context -Status 412 -Body @{
            error    = 'recipe_invalid'
            recipeId = $RecipeId
            errors   = @(@{
                path    = '/executionMode'
                keyword = 'manualEntryNotAllowed'
                message = "Manual cook entry is only available for recipes whose executionMode is 'local-manual'. This recipe declares '$recipeExecMode'."
                params  = @{ executionMode = $recipeExecMode }
            })
        }
        return
    }

    $authMode      = ''
    $authProfileId = ''
    if ($recipe.ContainsKey('auth')) {
        if ($recipe.auth.ContainsKey('mode'))          { $authMode      = [string]$recipe.auth.mode }
        if ($recipe.auth.ContainsKey('authProfileId')) { $authProfileId = [string]$recipe.auth.authProfileId }
    }
    $authProfileRow      = $null
    $clientSecretSecure  = $null
    try {
        if ($authMode -eq 'AppRegistrationSecret' -or $authMode -eq 'AppRegistrationCertificate') {
            if ([string]::IsNullOrWhiteSpace($authProfileId)) {
                Write-JsonResponse -Context $Context -Status 412 -Body @{
                    error    = 'recipe_invalid'
                    recipeId = $RecipeId
                    errors   = @(@{
                        path    = '/auth/authProfileId'
                        keyword = 'required'
                        message = "auth.authProfileId is required when auth.mode is '$authMode'"
                        params  = @{ missingProperty = 'authProfileId' }
                    })
                }
                return
            }
            $authProfileRow = Get-AuthProfileRow -AuthProfileId $authProfileId
            if ($null -eq $authProfileRow) {
                Write-JsonResponse -Context $Context -Status 412 -Body @{
                    error    = 'recipe_invalid'
                    recipeId = $RecipeId
                    errors   = @(@{
                        path    = '/auth/authProfileId'
                        keyword = 'profileNotFound'
                        message = "auth profile '$authProfileId' does not exist"
                        params  = @{ authProfileId = $authProfileId }
                    })
                }
                return
            }
            if ([string]$authProfileRow.mode -ne $authMode) {
                Write-JsonResponse -Context $Context -Status 412 -Body @{
                    error    = 'recipe_invalid'
                    recipeId = $RecipeId
                    errors   = @(@{
                        path    = '/auth/authProfileId'
                        keyword = 'profileModeMismatch'
                        message = ("auth profile '$authProfileId' is mode '" + [string]$authProfileRow.mode + "' but recipe.auth.mode is '$authMode'")
                        params  = @{ recipeMode = $authMode; profileMode = [string]$authProfileRow.mode }
                    })
                }
                return
            }
            if ($authMode -eq 'AppRegistrationSecret') {
                if (-not (Test-AuthProfileSecretPresent -AuthProfileId $authProfileId)) {
                    Write-JsonResponse -Context $Context -Status 412 -Body @{
                        error    = 'recipe_invalid'
                        recipeId = $RecipeId
                        errors   = @(@{
                            path    = '/auth/authProfileId'
                            keyword = 'secretNotBound'
                            message = "auth profile '$authProfileId' has no client secret bound in Windows Credential Manager"
                            params  = @{ authProfileId = $authProfileId }
                        })
                    }
                    return
                }
                $clientSecretSecure = Read-AuthProfileSecret -AuthProfileId $authProfileId
                if ($null -eq $clientSecretSecure) {
                    Write-JsonResponse -Context $Context -Status 500 -Body @{
                        error    = 'credman_read_failed'
                        recipeId = $RecipeId
                        message  = "Failed to read the bound secret for auth profile '$authProfileId' from Windows Credential Manager."
                    }
                    return
                }
            }
        }

        try {
            $plan = Get-PaxInvocationPlan -Recipe $recipe -PaxScriptPath $paxPath -AuthProfile $authProfileRow -ExecutionMode 'local-manual'
        } catch {
            Write-JsonResponse -Context $Context -Status 412 -Body @{
                error    = 'recipe_invalid'
                recipeId = $RecipeId
                errors   = @(@{ path = '/advanced/extraArguments'; message = $_.Exception.Message })
            }
            return
        }
    } catch {
        # Any pre-plan failure not already mapped: dispose the secret
        # SecureString defensively and return 500.
        if ($null -ne $clientSecretSecure) {
            try { $clientSecretSecure.Dispose() } catch {}
            $clientSecretSecure = $null
        }
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error    = 'cook_start_internal'
            recipeId = $RecipeId
            message  = $_.Exception.Message
        }
        return
    }

    $factPath = ''
    if ($recipe.ContainsKey('destinations') -and $recipe.destinations.ContainsKey('fact') `
            -and $recipe.destinations.fact.ContainsKey('path')) {
        $factPath = [string]$recipe.destinations.fact.path
    }

    # Generate cookId + cook folder layout.
    $cookId   = New-CookId
    $now      = Get-UtcNowIso
    $folder   = Join-Path (Join-Path $Script:CooksDir $RecipeId) $cookId
    try {
        $null = New-Item -ItemType Directory -Path $folder -Force
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error    = 'cook_folder_create_failed'
            cookId   = $cookId
            folder   = $folder
            message  = $_.Exception.Message
        }
        return
    }

    # Authoritative recipe snapshot for reproducibility (per F-3.a)
    # AND authoritative runtime-identity record (cook-context.json) for
    # the cook-execution-record hardening contract. Both are written
    # BEFORE spawn so they survive spawn failures. Both are frozen --
    # never rewritten on update or reconciliation.
    #
    # Phase AB: all five initial cook-folder files are written via
    # atomic .tmp + File.Move(overwrite=true). Pre-AB used direct
    # WriteAllText; that was only bounded-safe because the cook row
    # INSERT happens AFTER this block, so a partial folder never had a
    # matching DB row. Atomic writes harden the same property
    # uniformly with the supervisor's sentinel writes and keep the
    # write discipline intentional rather than incidental.
    try {
        $snapPath = Join-Path $folder 'recipe-snapshot.json'
        $snapJson = $recipe | ConvertTo-Json -Depth 12
        Write-AtomicUtf8NoBom -Path $snapPath -Content $snapJson

        $ctxBlock = Get-CookContextBlock -CookId $cookId -RecipeId $RecipeId -Trigger 'manual' -CreatedAt $now
        $ctxPath  = Join-Path $folder 'cook-context.json'
        $ctxJson  = $ctxBlock | ConvertTo-Json -Depth 6
        Write-AtomicUtf8NoBom -Path $ctxPath -Content $ctxJson

        $cmdPath  = Join-Path $folder 'command.txt'
        Write-AtomicUtf8NoBom -Path $cmdPath -Content $plan.paxCommand

        # Structured projection record. Frozen at cook-start, never
        # rewritten. Authoritative answer to "what argv was projected
        # from this recipe?" -- all four fields come from the same
        # Get-PaxInvocationPlan call as command.txt, the DB row, and
        # the supervisor's -CommandExpr, so the four sinks cannot drift.
        $argvDocPath = Join-Path $folder 'command-argv.json'
        $argvDoc = [ordered]@{
            paxArgv        = $plan.paxArgv
            extraArguments = $plan.extraArguments
            spawnArgv      = $plan.spawnArgv
            paxScriptPath  = $plan.paxScriptPath
        }
        Write-AtomicUtf8NoBom -Path $argvDocPath -Content ($argvDoc | ConvertTo-Json -Depth 4)
        # Create an empty cook.log so the supervisor's append open is a
        # no-content append rather than create-on-write.
        $logPath = Join-Path $folder 'cook.log'
        if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
            Write-AtomicUtf8NoBom -Path $logPath -Content ''
        }
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'cook_init_files_failed'
            cookId  = $cookId
            message = $_.Exception.Message
        }
        return
    }

    # cooks.command_argv_json stores plan.spawnArgv -- the literal
    # 4-element ProcessStartInfo.ArgumentList that the supervisor will
    # pass to pwsh.exe. This matches started.json.command (also from
    # plan.spawnArgv) and is the actual subprocess argv on disk.
    # M1 has no secrets in argv, so command_argv_redacted == command_argv_json.
    $argvJson = $plan.spawnArgv | ConvertTo-Json -Compress -Depth 3

    # Phase AB: cook_folder is stored workspace-relative so the
    # workspace is genuinely portable. The supervisor still receives
    # the absolute path because it does real filesystem I/O. New cook
    # folders always sit under $Script:CooksDir so the relative form
    # is guaranteed to resolve.
    $relativeFolder = ConvertTo-WorkspaceRelativeCookFolder -Absolute $folder
    if (-not $relativeFolder) {
        # Defensive: should be impossible because $folder was just
        # built from $Script:CooksDir, which sits under $Script:WorkspacePath.
        # Falling back to the absolute path keeps the cook recoverable
        # rather than wedging on a doctrinal contradiction.
        $relativeFolder = $folder
    }

    try {
        Add-CookRow -Row @{
            cook_id               = $cookId
            recipe_id             = $RecipeId
            recipe_snapshot_json  = ($recipe | ConvertTo-Json -Depth 12 -Compress)
            command_argv_json     = $argvJson
            command_argv_redacted = $argvJson
            pax_script_path       = $paxPath
            pax_script_version    = $Script:PaxScriptVersion
            trigger               = 'manual'
            cook_folder           = $relativeFolder
            started_at            = $null     # supervisor sets on actual spawn
            created_at            = $now
            updated_at            = $now
        }
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'cook_row_insert_failed'
            cookId  = $cookId
            message = $_.Exception.Message
        }
        return
    }

    # Per-cook state for the supervisor + cancel signals from /stop /kill.
    $state = [hashtable]::Synchronized(@{
        recipe_id   = $RecipeId
        cook_folder = $folder
        pid         = $null
        started_at  = $null
        cancel_mode = 'none'
    })
    $Script:CookRegistry[$cookId] = $state

    try {
        $pwshPath = if ($PSHOME) { Join-Path $PSHOME 'pwsh.exe' } else { 'pwsh' }
        if (-not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) { $pwshPath = 'pwsh' }

        # Phase AF -- forward the SecureString secret reference and
        # the (forced) 'local-manual' executionMode through to the
        # supervisor. The supervisor zero-frees the BSTR copy after
        # $proc.Start().
        #
        # SecureString lifetime: we MUST NOT call Dispose() on
        # $clientSecretSecure once Start-ThreadJob has captured it
        # by reference -- the supervisor ThreadJob runspace marshals
        # the SecureString to a BSTR asynchronously, and racing the
        # parent's Dispose() against that marshaling would scrub the
        # SecureString before the supervisor reads it. Instead we
        # let the parent-side reference fall out of scope naturally;
        # the SecureString's own finalizer + GC then clear the
        # encrypted bytes once both the parent and the supervisor
        # have released their references.
        Start-CookSupervisor `
            -CookId               $cookId `
            -RecipeId             $RecipeId `
            -CommandExpr          $plan.spawnArgv[3] `
            -CookFolder           $folder `
            -PwshPath             $pwshPath `
            -PaxScriptPath        $paxPath `
            -FactPath             $factPath `
            -ConnectionString     (Get-SqliteConnectionString) `
            -HostName             $env:COMPUTERNAME `
            -CookState            $state `
            -ExecutionMode        'local-manual' `
            -ClientSecretSecure   $clientSecretSecure
    } catch {
        $Script:CookRegistry.Remove($cookId)
        # The ThreadJob may or may not have started. We CANNOT
        # safely Dispose() the SecureString here -- if Start-ThreadJob
        # had already queued the runspace, the supervisor may still
        # be racing to marshal the secret. Let GC reclaim it.
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'supervisor_spawn_failed'
            cookId  = $cookId
            message = $_.Exception.Message
        }
        return
    }

    Write-JsonResponse -Context $Context -Status 201 -Body @{
        cookId     = $cookId
        recipeId   = $RecipeId
        cookFolder = $folder
    }
    # Do NOT Dispose() $clientSecretSecure here -- see the lifetime
    # comment above the Start-CookSupervisor call. The SecureString
    # falls out of scope on function return and is reclaimed by GC
    # after the supervisor releases its own reference.
}

function Invoke-CookStop {
    param($Context, [string]$CookId)
    if ($CookId -notmatch $Script:CookIdPattern) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_cook_id'; cookId = $CookId }
        return
    }
    if (-not $Script:CookRegistry.ContainsKey($CookId)) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'cook_not_active'; cookId = $CookId }
        return
    }
    [void](Request-CookCancel -CookId $CookId -Mode 'stop')
    Write-JsonResponse -Context $Context -Status 202 -Body @{ cookId = $CookId; cancel = 'stop' }
}

function Invoke-CookKill {
    param($Context, [string]$CookId)
    if ($CookId -notmatch $Script:CookIdPattern) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_cook_id'; cookId = $CookId }
        return
    }
    if (-not $Script:CookRegistry.ContainsKey($CookId)) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'cook_not_active'; cookId = $CookId }
        return
    }
    [void](Request-CookCancel -CookId $CookId -Mode 'kill')
    Write-JsonResponse -Context $Context -Status 202 -Body @{ cookId = $CookId; cancel = 'kill' }
}

function Invoke-CookResume {
    # V1.S03 -- POST /api/v1/cooks/<cookId>/resume
    #
    # Resume contract (verbatim from V1.S03 spec):
    #   - A resume is a NEW cook with its own cookId, its own cook
    #     folder, and a row in the cooks table whose parent_cook_id
    #     points back at the original interrupted cook. The parent
    #     row is NEVER mutated, NEVER reused, NEVER repointed.
    #   - The new cook's recipe_snapshot_json is a verbatim copy of
    #     the parent's snapshot (the recipe at the moment of original
    #     cook-start). Any subsequent edits to the recipe on disk do
    #     NOT retroactively apply.
    #   - The argv projection comes from Get-PaxResumeInvocationPlan
    #     (narrow argv: -Resume <path> -Force plus optional auth
    #     overrides), NOT from Get-PaxInvocationPlan.
    #   - All conservative manual-cook gates fire in the SAME ORDER
    #     as Invoke-CookStart: PAX SHA-256 recheck, per-recipe
    #     concurrency, re-auth (OpClass='manualCook'), disk precheck,
    #     path-length precheck. Auth profile resolution and secret
    #     read mirror Invoke-CookStart exactly.
    #   - The recipe validator (Test-RecipeAll) is INTENTIONALLY NOT
    #     re-run: the snapshot is authoritative; running the current
    #     validator against a snapshot from an older Cookbook would
    #     create a meaningless "schema-drifted" refusal.
    param($Context, [string]$CookId)

    if ($CookId -notmatch $Script:CookIdPattern) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_cook_id'; cookId = $CookId }
        return
    }
    $parentRow = Get-CookRow -CookId $CookId
    if (-not $parentRow) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found'; cookId = $CookId }
        return
    }

    $verdictResume = Test-CookResumable -Row $parentRow
    if (-not $verdictResume.resumable) {
        # 410 Gone is reserved for "checkpoint file vanished from disk".
        # All other refusals (wrong status, wrong closure_reason,
        # closure-evidence parse failures, no recorded checkpoint
        # path) collapse onto 409 Conflict with the machine-readable
        # reason as the discriminator.
        $status = if ($verdictResume.reason -eq 'checkpoint_missing') { 410 } else { 409 }
        Write-JsonResponse -Context $Context -Status $status -Body @{
            error          = 'not_resumable'
            cookId         = $CookId
            reason         = $verdictResume.reason
            humanReason    = $verdictResume.humanReason
            checkpointPath = $verdictResume.checkpointPath
        }
        return
    }
    $checkpointPath = [string]$verdictResume.checkpointPath
    $parentRecipeId = [string]$parentRow.recipeId

    # The parent's snapshot column is the authoritative source. We do
    # NOT read recipe-snapshot.json from the parent cook folder --
    # the operator may have deleted that folder; the DB column
    # survives folder cleanup.
    $snapshotJson = Get-CookRecipeSnapshotJson -CookId $CookId
    if ([string]::IsNullOrWhiteSpace($snapshotJson)) {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error    = 'cook_resume_internal'
            cookId   = $CookId
            message  = 'Parent cook has no recipe_snapshot_json. The cook row is malformed and cannot be resumed.'
        }
        return
    }
    $snapshot = $null
    try {
        $snapshot = $snapshotJson | ConvertFrom-Json -AsHashtable -Depth 12
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'cook_resume_internal'
            cookId  = $CookId
            message = 'Parent cook recipe_snapshot_json is unparseable: ' + $_.Exception.Message
        }
        return
    }
    if ($null -eq $snapshot -or -not ($snapshot -is [hashtable])) {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'cook_resume_internal'
            cookId  = $CookId
            message = 'Parent cook recipe_snapshot_json did not parse to a JSON object.'
        }
        return
    }

    # The snapshot's executionMode must be 'local-manual' for the same
    # reason Invoke-CookStart enforces it: hosted/scheduled runtimes
    # have separate validation paths. Snapshots taken from a recipe
    # that has since been moved off-manual are NOT silently resumed.
    $recipeExecMode = ''
    if ($snapshot.ContainsKey('executionMode')) { $recipeExecMode = [string]$snapshot.executionMode }
    if ($recipeExecMode -and $recipeExecMode -notin @('local-manual')) {
        Write-JsonResponse -Context $Context -Status 412 -Body @{
            error    = 'recipe_invalid'
            cookId   = $CookId
            recipeId = $parentRecipeId
            errors   = @(@{
                path    = '/executionMode'
                keyword = 'manualEntryNotAllowed'
                message = "Manual cook resume is only available for snapshots whose executionMode is 'local-manual'. This snapshot declares '$recipeExecMode'."
                params  = @{ executionMode = $recipeExecMode }
            })
        }
        return
    }

    # Acquisition gate. Mirrors Invoke-CookStart: refuses with
    # 409 acquisitionRequired in external-engine mode before the
    # PAX integrity recheck would surface the less informative
    # pax_script_integrity error.
    if (Invoke-AcquisitionGateOrShortCircuit -Context $Context -Endpoint 'POST /api/v1/cooks/<id>/resume') { return }

    # PAX integrity recheck (same code path as Invoke-CookStart).
    $paxPath = $Script:PaxScriptPath
    if ([string]::IsNullOrWhiteSpace($paxPath) -or -not (Test-Path -LiteralPath $paxPath -PathType Leaf)) {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error  = 'pax_script_integrity'
            detail = 'The bundled PAX script is no longer present at the canonical install-tree path. Re-run Install-PAXCookbook.ps1.'
        }
        return
    }
    try {
        $cookSpawnSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $paxPath -ErrorAction Stop).Hash.ToUpperInvariant()
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'pax_script_integrity'
            detail  = 'Failed to compute SHA-256 of the bundled PAX script.'
            message = $_.Exception.Message
        }
        return
    }
    if ($cookSpawnSha -ne $Script:PaxScriptSha256) {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error  = 'pax_script_integrity'
            detail = 'The bundled PAX script SHA-256 changed since broker startup. Refusing to spawn. Re-run Install-PAXCookbook.ps1 and restart the broker.'
        }
        return
    }

    # Per-recipe concurrency: a recipe with a currently-running cook
    # cannot accept a resume. Same rule as a fresh manual cook. The
    # parent's recipe_id is the discriminator.
    $existing = Get-RunningCookIdForRecipe -RecipeId $parentRecipeId
    if ($existing) {
        Write-JsonResponse -Context $Context -Status 409 -Body @{
            error  = 'recipe_busy'
            cookId = $existing
        }
        return
    }

    # Per-op fresh re-auth gate (same OpClass as a fresh manual cook).
    $recipeName = ''
    if ($snapshot.ContainsKey('identity') -and $snapshot.identity -is [hashtable] -and $snapshot.identity.ContainsKey('name')) {
        $recipeName = [string]$snapshot.identity.name
    }
    if (-not $recipeName) { $recipeName = $parentRecipeId }
    $reAuthMsg = "Verify to resume cook '$CookId' for recipe '$recipeName'."
    $reAuthVerdict = Invoke-BrokerLockReAuthForOp -OpClass 'manualCook' -Message $reAuthMsg
    if ($reAuthVerdict -ne 'Verified') {
        $resp = New-BrokerLockReAuthRequiredResponse -OpClass 'manualCook' -VerificationResult $reAuthVerdict
        Write-JsonResponse -Context $Context -Status $resp.status -Body $resp.body
        return
    }

    # Disk + path-length prechecks (same as Invoke-CookStart).
    $diskCheck = Test-CookDiskPrecheck -Path $Script:WorkspacePath
    if (-not $diskCheck.ok) {
        Add-RecentError -Message ("Cook resume refused for parent '$CookId': " + $diskCheck.reason + ' (' + $diskCheck.detail + ')') -Source 'cook_disk_precheck'
        Write-JsonResponse -Context $Context -Status 507 -Body @{
            error         = 'insufficient_disk_space'
            cookId        = $CookId
            recipeId      = $parentRecipeId
            reason        = $diskCheck.reason
            detail        = $diskCheck.detail
            freeBytes     = $diskCheck.freeBytes
            requiredBytes = $diskCheck.requiredBytes
            drive         = $diskCheck.driveName
        }
        return
    }
    $effWorkspaceLen = if ($Script:WorkspacePath) { [int]$Script:WorkspacePath.Length } else { 0 }
    if (($effWorkspaceLen + 96) -gt 260) {
        Add-RecentError -Message ("Cook resume refused for parent '$CookId': workspace_path_too_long (pathLength=$effWorkspaceLen + 96 reserved > 260).") -Source 'cook_path_precheck'
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error               = 'workspace_path_too_long'
            cookId              = $CookId
            recipeId            = $parentRecipeId
            workspacePathLength = $effWorkspaceLen
            reservedChildBudget = 96
            classicLimit        = 260
            reason              = 'pre_spawn_path_length_exceeds_max_path'
            detail              = ("The configured workspace path is " + $effWorkspaceLen + " characters long. Each cook folder appends ~71 characters for /Cooks/<recipeId>/<cookId>/ plus up to ~25 characters for the longest child filename, which would exceed the classic Win32 MAX_PATH of 260. Cookbook does NOT auto-shorten or relocate workspaces. Move the workspace closer to the drive root, or rename it.")
        }
        return
    }

    # Re-test checkpoint presence under the gate. Test-CookResumable
    # ran before re-auth and disk checks; the operator could in
    # principle have moved/deleted the checkpoint during the prompt.
    if (-not (Test-Path -LiteralPath $checkpointPath -PathType Leaf)) {
        Write-JsonResponse -Context $Context -Status 410 -Body @{
            error          = 'not_resumable'
            cookId         = $CookId
            reason         = 'checkpoint_missing'
            humanReason    = 'The PAX checkpoint file is no longer on disk.'
            checkpointPath = $checkpointPath
        }
        return
    }

    # Auth resolution from the SNAPSHOT (same rules as Invoke-CookStart
    # but the recipe data source is the frozen snapshot, not a fresh
    # disk read). Auth profile rows are NOT snapshotted; we resolve
    # them live so a rotated secret is picked up.
    $authMode      = ''
    $authProfileId = ''
    $authProfileRow = $null
    $clientSecretSecure = $null
    if ($snapshot.ContainsKey('auth')) {
        if ($snapshot.auth.ContainsKey('mode'))          { $authMode      = [string]$snapshot.auth.mode }
        if ($snapshot.auth.ContainsKey('authProfileId')) { $authProfileId = [string]$snapshot.auth.authProfileId }
    }
    try {
        if ($authMode -eq 'AppRegistrationSecret' -or $authMode -eq 'AppRegistrationCertificate') {
            if ([string]::IsNullOrWhiteSpace($authProfileId)) {
                Write-JsonResponse -Context $Context -Status 412 -Body @{
                    error    = 'recipe_invalid'
                    cookId   = $CookId
                    recipeId = $parentRecipeId
                    errors   = @(@{
                        path    = '/auth/authProfileId'
                        keyword = 'required'
                        message = "auth.authProfileId is required when auth.mode is '$authMode'"
                        params  = @{ missingProperty = 'authProfileId' }
                    })
                }
                return
            }
            $authProfileRow = Get-AuthProfileRow -AuthProfileId $authProfileId
            if ($null -eq $authProfileRow) {
                Write-JsonResponse -Context $Context -Status 412 -Body @{
                    error    = 'recipe_invalid'
                    cookId   = $CookId
                    recipeId = $parentRecipeId
                    errors   = @(@{
                        path    = '/auth/authProfileId'
                        keyword = 'profileNotFound'
                        message = "auth profile '$authProfileId' does not exist"
                        params  = @{ authProfileId = $authProfileId }
                    })
                }
                return
            }
            if ([string]$authProfileRow.mode -ne $authMode) {
                Write-JsonResponse -Context $Context -Status 412 -Body @{
                    error    = 'recipe_invalid'
                    cookId   = $CookId
                    recipeId = $parentRecipeId
                    errors   = @(@{
                        path    = '/auth/authProfileId'
                        keyword = 'profileModeMismatch'
                        message = ("auth profile '$authProfileId' is mode '" + [string]$authProfileRow.mode + "' but snapshot.auth.mode is '$authMode'")
                        params  = @{ snapshotMode = $authMode; profileMode = [string]$authProfileRow.mode }
                    })
                }
                return
            }
            if ($authMode -eq 'AppRegistrationSecret') {
                if (-not (Test-AuthProfileSecretPresent -AuthProfileId $authProfileId)) {
                    Write-JsonResponse -Context $Context -Status 412 -Body @{
                        error    = 'recipe_invalid'
                        cookId   = $CookId
                        recipeId = $parentRecipeId
                        errors   = @(@{
                            path    = '/auth/authProfileId'
                            keyword = 'secretNotBound'
                            message = "auth profile '$authProfileId' has no client secret bound in Windows Credential Manager"
                            params  = @{ authProfileId = $authProfileId }
                        })
                    }
                    return
                }
                $clientSecretSecure = Read-AuthProfileSecret -AuthProfileId $authProfileId
                if ($null -eq $clientSecretSecure) {
                    Write-JsonResponse -Context $Context -Status 500 -Body @{
                        error    = 'credman_read_failed'
                        cookId   = $CookId
                        recipeId = $parentRecipeId
                        message  = "Failed to read the bound secret for auth profile '$authProfileId' from Windows Credential Manager."
                    }
                    return
                }
            }
        }

        # Resume-specific argv projection (-Resume <path> -Force plus
        # auth overrides only; NO processing params, NO extraArguments).
        try {
            $plan = Get-PaxResumeInvocationPlan -CheckpointPath $checkpointPath -PaxScriptPath $paxPath -AuthMode $authMode -TenantId $(if ($snapshot.ContainsKey('auth') -and $snapshot.auth.ContainsKey('tenantId')) { [string]$snapshot.auth.tenantId } else { '' }) -AuthProfile $authProfileRow -ExecutionMode 'local-manual'
        } catch {
            Write-JsonResponse -Context $Context -Status 412 -Body @{
                error    = 'recipe_invalid'
                cookId   = $CookId
                recipeId = $parentRecipeId
                errors   = @(@{ path = '/resume'; message = $_.Exception.Message })
            }
            return
        }
    } catch {
        if ($null -ne $clientSecretSecure) {
            try { $clientSecretSecure.Dispose() } catch {}
            $clientSecretSecure = $null
        }
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error    = 'cook_resume_internal'
            cookId   = $CookId
            recipeId = $parentRecipeId
            message  = $_.Exception.Message
        }
        return
    }

    $factPath = ''
    if ($snapshot.ContainsKey('destinations') -and $snapshot.destinations.ContainsKey('fact') `
            -and $snapshot.destinations.fact.ContainsKey('path')) {
        $factPath = [string]$snapshot.destinations.fact.path
    }

    # New cookId + new folder for the resume cook. The original
    # interrupted cook's folder is left untouched.
    $newCookId = New-CookId
    $now       = Get-UtcNowIso
    $folder    = Join-Path (Join-Path $Script:CooksDir $parentRecipeId) $newCookId
    try {
        $null = New-Item -ItemType Directory -Path $folder -Force
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error    = 'cook_folder_create_failed'
            cookId   = $newCookId
            folder   = $folder
            message  = $_.Exception.Message
        }
        return
    }

    try {
        # recipe-snapshot.json: verbatim copy of the parent's DB
        # column. The parent's snapshot is authoritative for the
        # resumed run.
        $snapPath = Join-Path $folder 'recipe-snapshot.json'
        Write-AtomicUtf8NoBom -Path $snapPath -Content $snapshotJson

        $ctxBlock = Get-CookContextBlock -CookId $newCookId -RecipeId $parentRecipeId -Trigger 'resume' -CreatedAt $now -ParentCookId $CookId
        $ctxPath  = Join-Path $folder 'cook-context.json'
        $ctxJson  = $ctxBlock | ConvertTo-Json -Depth 6
        Write-AtomicUtf8NoBom -Path $ctxPath -Content $ctxJson

        $cmdPath  = Join-Path $folder 'command.txt'
        Write-AtomicUtf8NoBom -Path $cmdPath -Content $plan.paxCommand

        # Structured projection record. Frozen at resume-cook-start.
        $argvDocPath = Join-Path $folder 'command-argv.json'
        $argvDoc = [ordered]@{
            paxArgv        = $plan.paxArgv
            extraArguments = $plan.extraArguments
            spawnArgv      = $plan.spawnArgv
            paxScriptPath  = $plan.paxScriptPath
            isResume       = $true
            checkpointPath = $plan.checkpointPath
            parentCookId   = $CookId
        }
        Write-AtomicUtf8NoBom -Path $argvDocPath -Content ($argvDoc | ConvertTo-Json -Depth 4)

        $logPath = Join-Path $folder 'cook.log'
        if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
            Write-AtomicUtf8NoBom -Path $logPath -Content ''
        }
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'cook_init_files_failed'
            cookId  = $newCookId
            message = $_.Exception.Message
        }
        return
    }

    $argvJson = $plan.spawnArgv | ConvertTo-Json -Compress -Depth 3

    $relativeFolder = ConvertTo-WorkspaceRelativeCookFolder -Absolute $folder
    if (-not $relativeFolder) { $relativeFolder = $folder }

    try {
        Add-CookRow -Row @{
            cook_id               = $newCookId
            recipe_id             = $parentRecipeId
            recipe_snapshot_json  = $snapshotJson
            command_argv_json     = $argvJson
            command_argv_redacted = $argvJson
            pax_script_path       = $paxPath
            pax_script_version    = $Script:PaxScriptVersion
            trigger               = 'resume'
            cook_folder           = $relativeFolder
            parent_cook_id        = $CookId
            started_at            = $null
            created_at            = $now
            updated_at            = $now
        }
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'cook_row_insert_failed'
            cookId  = $newCookId
            message = $_.Exception.Message
        }
        return
    }

    $state = [hashtable]::Synchronized(@{
        recipe_id   = $parentRecipeId
        cook_folder = $folder
        pid         = $null
        started_at  = $null
        cancel_mode = 'none'
    })
    $Script:CookRegistry[$newCookId] = $state

    try {
        $pwshPath = if ($PSHOME) { Join-Path $PSHOME 'pwsh.exe' } else { 'pwsh' }
        if (-not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) { $pwshPath = 'pwsh' }

        # SecureString lifetime: identical contract to Invoke-CookStart.
        # The parent runspace MUST NOT Dispose() $clientSecretSecure
        # after Start-ThreadJob captures it; the supervisor marshals
        # to BSTR asynchronously and we let GC reclaim the reference.
        Start-CookSupervisor `
            -CookId               $newCookId `
            -RecipeId             $parentRecipeId `
            -CommandExpr          $plan.spawnArgv[3] `
            -CookFolder           $folder `
            -PwshPath             $pwshPath `
            -PaxScriptPath        $paxPath `
            -FactPath             $factPath `
            -ConnectionString     (Get-SqliteConnectionString) `
            -HostName             $env:COMPUTERNAME `
            -CookState            $state `
            -ExecutionMode        'local-manual' `
            -ClientSecretSecure   $clientSecretSecure
    } catch {
        $Script:CookRegistry.Remove($newCookId)
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'supervisor_spawn_failed'
            cookId  = $newCookId
            message = $_.Exception.Message
        }
        return
    }

    Write-JsonResponse -Context $Context -Status 201 -Body @{
        cookId       = $newCookId
        parentCookId = $CookId
        recipeId     = $parentRecipeId
        cookFolder   = $folder
    }
    # Do NOT Dispose() $clientSecretSecure here -- see Invoke-CookStart
    # for the lifetime rationale.
}

function Invoke-CooksList {
    param($Context)
    # V1.S06d -- import any wrapper-folder evidence the OS-spawned
    # scheduled-task wrapper has dropped since the previous call.
    # Bounded so the cap on folders per call protects request
    # latency; folders beyond the cap import on the next call.
    # Reconciliation errors are absorbed into Add-RecentError so a
    # transient SQLite hiccup does not 500 the runs list.
    try { [void](Invoke-ScheduledTaskReconcile -Bounded) }
    catch { Add-RecentError ('GET /api/v1/cooks scheduled-task reconcile failed: ' + $_.Exception.Message) }

    $qs = $Context.Request.QueryString
    $rid = $qs['recipeId']
    $st  = $qs['status']
    $rows = Get-CooksFiltered -RecipeId $rid -Status $st

    # V1.S02: a single SQL aggregate over cook_artifacts gives us
    # per-cook artifactCount + rowCount in one round trip. The rollup
    # is best-effort -- if SQLite returns NULL for a cook (no artifact
    # rows yet), the corresponding fields surface as nulls.
    $artifactRollup = @{}
    try { $artifactRollup = Get-CookArtifactRollup } catch {}

    # Enrich each row with the per-cook persisted identity fields the
    # operator needs to scan a run list: recipeName (from the FROZEN
    # recipe-snapshot.json -- never from the current recipes row),
    # cookbookVersion / bundledPaxVersion / releaseChannel / host
    # (from cook-context.json). Per-row cost: at most two small JSON
    # reads. Files may be absent for legacy cooks; the corresponding
    # fields are null and surface as em-dashes in the UI.
    $enriched = New-Object System.Collections.Generic.List[object]
    foreach ($r in $rows) {
        $folder    = [string]$r.cookFolder
        $name      = $null
        $summary   = $null
        if ($folder -and (Test-Path -LiteralPath $folder -PathType Container)) {
            try { $name    = Get-CookSnapshotName -CookFolder $folder } catch {}
            try { $summary = Get-CookContextSummary -CookFolder $folder } catch {}
        }
        $row = [ordered]@{}
        foreach ($k in $r.Keys) { $row[$k] = $r[$k] }
        $row['recipeName']        = $name
        $row['cookbookVersion']   = if ($summary) { $summary.cookbookVersion }   else { $null }
        $row['bundledPaxVersion'] = if ($summary) { $summary.bundledPaxVersion } else { $null }
        $row['releaseChannel']    = if ($summary) { $summary.releaseChannel }    else { $null }
        $row['host']              = if ($summary) { $summary.host }              else { $null }
        # V1.S02: cheap per-row aggregate join.
        $rollup = $artifactRollup[$row['cookId']]
        if ($rollup) {
            $row['artifactCount'] = [int]$rollup.artifactCount
            $row['rowCount']      = if ($null -eq $rollup.rowCount) { $null } else { [long]$rollup.rowCount }
        } else {
            $row['artifactCount'] = 0
            $row['rowCount']      = $null
        }
        # V1.S03: derive per-row resumability so the cooks list can
        # render a "resumable" marker next to the interrupted status
        # without a follow-up detail fetch. The verdict reflects the
        # live filesystem state at this moment -- the checkpoint
        # file is stat'd inside Test-CookResumable.
        $verdictResume = $null
        try { $verdictResume = Test-CookResumable -Row $row } catch {}
        if ($verdictResume) {
            $row['resumable']    = [bool]$verdictResume.resumable
            $row['resumeReason'] = [string]$verdictResume.reason
        } else {
            $row['resumable']    = $false
            $row['resumeReason'] = 'resumability_check_failed'
        }
        $enriched.Add($row)
    }
    Write-JsonResponse -Context $Context -Status 200 -Body @{ cooks = $enriched.ToArray() }
}

function Invoke-CookGet {
    param($Context, [string]$CookId)
    if ($CookId -notmatch $Script:CookIdPattern) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_cook_id'; cookId = $CookId }
        return
    }
    $row = Get-CookRow -CookId $CookId
    if (-not $row) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found'; cookId = $CookId }
        return
    }
    $sentinels = Read-CookSentinels -CookFolder $row.cookFolder
    $context_  = Read-CookContext   -CookFolder $row.cookFolder
    $artifacts = Get-CookArtifactsForCook -CookId $CookId
    # Surface the cook's frozen identity / argv / provenance evidence
    # to the operator. Every field below comes from on-disk files
    # written at cook-start (recipe-snapshot.json, command-argv.json,
    # command.txt, cook-context.json) or from the cook_artifacts row
    # stat'd against the filesystem now. NOTHING below recomputes argv
    # from the current recipe.
    $snapshot     = Read-CookRecipeSnapshot -CookFolder $row.cookFolder
    $commandDoc   = Read-CookCommandDoc     -CookFolder $row.cookFolder
    $recipeName   = $null
    if ($snapshot -and -not $snapshot.ContainsKey('_parseError') `
            -and $snapshot.ContainsKey('identity') -and $snapshot.identity `
            -and $snapshot.identity.ContainsKey('name')) {
        $recipeName = [string]$snapshot.identity.name
    }
    $folderArtifacts = Get-CookFolderArtifactRefs  -CookFolder $row.cookFolder
    $outputs         = Get-CookOutputArtifactRefs  -Artifacts  $artifacts

    # V1.S02: best-effort, null-tolerant summary / metrics enrichment.
    # Each call returns either $null (file absent), a small parse-error
    # stub, or the parsed JSON. The cook detail GET never fails on a
    # malformed summary/metrics file -- absence and parse failure are
    # both surfaced verbatim to the UI as observable states.
    $paxSummary       = $null
    $metricsObj       = $null
    $discoveredFiles  = @()
    $metricSummary    = [ordered]@{}
    try { $paxSummary      = Read-CookPaxSummary           -CookFolder $row.cookFolder } catch {}
    try { $metricsObj      = Read-CookMetrics              -CookFolder $row.cookFolder -Artifacts $artifacts } catch {}
    try { $discoveredFiles = Get-CookDiscoveredOutputFiles -CookFolder $row.cookFolder -Artifacts $artifacts } catch {}
    try { $metricSummary   = Get-CookMetricSummary         -Summary    $paxSummary    -Metrics  $metricsObj } catch {}

    # V1.S03: surface the cook's resumability verdict at request
    # time. The verdict reflects the live filesystem state (the
    # checkpoint file is stat'd here); a cook reported as resumable
    # may transition to non-resumable seconds later if the operator
    # deletes the checkpoint.
    $verdictResume = $null
    try { $verdictResume = Test-CookResumable -Row $row } catch {}
    $resumable      = $false
    $resumeReason   = 'resumability_check_failed'
    $checkpointPath = $null
    if ($verdictResume) {
        $resumable      = [bool]$verdictResume.resumable
        $resumeReason   = [string]$verdictResume.reason
        $checkpointPath = $verdictResume.checkpointPath
    }

    # V1.S06d -- when this is a scheduled-task cook, surface the
    # three wrapper envelope files verbatim so the cook-detail UI
    # can render a "Scheduled run" panel without making the broker
    # re-derive or reshape any wrapper data. Returns $null fields
    # for cooks that were not scheduled (manual / resume), which
    # the UI uses to suppress the panel entirely.
    $wrapperEnvelopesOut = $null
    try {
        if ([string]$row.trigger -eq 'scheduled' -and $row.cookFolder `
                -and (Test-Path -LiteralPath $row.cookFolder -PathType Container)) {
            $env = Get-ScheduledTaskWrapperEnvelopes -CookFolder $row.cookFolder
            $wrapperEnvelopesOut = [ordered]@{
                started        = $env.started
                finished       = $env.finished
                refused        = $env.refused
                startedExists  = [bool]$env.startedExists
                finishedExists = [bool]$env.finishedExists
                refusedExists  = [bool]$env.refusedExists
            }
        }
    } catch {
        Add-RecentError ('cook-detail wrapper-envelope read failed for ' + $CookId + ': ' + $_.Exception.Message)
    }

    Write-JsonResponse -Context $Context -Status 200 -Body @{
        cook              = $row
        recipeName        = $recipeName
        context           = $context_
        command           = $commandDoc
        recipeSnapshot    = $snapshot
        sentinels         = $sentinels
        artifacts         = $artifacts
        outputs           = $outputs
        folderArtifacts   = $folderArtifacts
        paxSummary        = $paxSummary
        metrics           = $metricsObj
        discoveredOutputs = $discoveredFiles
        metricSummary     = $metricSummary
        resumable         = $resumable
        resumeReason      = $resumeReason
        checkpointPath    = $checkpointPath
        parentCookId      = $row.parentCookId
        closureReason     = $row.closureReason
        wrapperEnvelopes  = $wrapperEnvelopesOut
    }
}

function Invoke-CookLog {
    # GET /api/v1/cooks/<cookId>/log
    #
    # Whole-file read of <CookFolder>/cook.log. Returns text/plain;
    # charset=utf-8 with Cache-Control: no-store. There is intentionally
    # NO Range support, NO cursor / byte-offset semantics, and NO
    # incremental / delta protocol. Operationally simple by design.
    #
    # File-sharing: the supervisor opens cook.log with FileMode.Append,
    # FileAccess.Write, FileShare.Read. This reader therefore opens with
    # FileAccess.Read + FileShare.ReadWrite so the supervisor's existing
    # writer handle remains compatible while a request is in flight. A
    # partially-flushed last line may be observed for a running cook —
    # this is acceptable and not corrected.
    param($Context, [string]$CookId)
    if ($CookId -notmatch $Script:CookIdPattern) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_cook_id'; cookId = $CookId }
        return
    }
    $row = Get-CookRow -CookId $CookId
    if (-not $row) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found'; cookId = $CookId }
        return
    }
    $logPath = Join-Path $row.cookFolder 'cook.log'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'log_unavailable'; cookId = $CookId }
        return
    }

    $bytes = $null
    $fs    = $null
    try {
        $fs = [System.IO.File]::Open(
            $logPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite)
        $ms = [System.IO.MemoryStream]::new()
        try {
            $fs.CopyTo($ms)
            $bytes = $ms.ToArray()
        } finally {
            $ms.Dispose()
        }
    } catch {
        # Treat any other I/O failure (concurrent delete, ACL change,
        # etc.) as log_unavailable. The cook row still exists; the file
        # just isn't readable right now.
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'log_unavailable'; cookId = $CookId }
        return
    } finally {
        if ($fs) { try { $fs.Dispose() } catch {} }
    }

    $Context.Response.StatusCode      = 200
    $Context.Response.ContentType     = 'text/plain; charset=utf-8'
    $Context.Response.ContentLength64 = [long]$bytes.LongLength
    $Context.Response.Headers['Cache-Control'] = 'no-store'
    try {
        $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    } finally {
        try { $Context.Response.OutputStream.Close() } catch {}
        try { $Context.Response.Close() } catch {}
    }
}

# ---------------------------------------------------------------------
# V1.S04 -- L3 readiness probes
# ---------------------------------------------------------------------
#
# Advisory pre-flight diagnostics for Cook and Resume. Readiness is
# NEVER a guarantee. A green readiness result does not promise a
# successful cook; a red readiness result does not prove a cook would
# have failed. Readiness probes the facts the broker can check
# WITHOUT spawning PAX, mutating any state, decrypting secrets, or
# making tenant-wide API calls. It is a sanity surface, not a contract.
#
# Doctrine:
#  - Read-only. No DB writes, no file writes, no Add-RecentError
#    side effects, no broker-state mutation.
#  - Secret-safe. The auth check uses Test-AuthProfileSecretPresent
#    (presence-only); secret bytes are never read on this path.
#  - Honest gaps. Network/Graph reachability stays 'not_checked':
#    real reachability requires the same OAuth credentials a cook
#    would use, and that surface remains PAX's responsibility.
#  - PAX-deferent. The broker never re-implements PAX's runtime
#    validators (header compatibility, dataset existence, M365
#    licensing, role-eligibility, etc.).
#  - Schema-stable. No new SQLite columns, no new on-disk artifacts.
#    The result is purely a response payload.
#
# Result shape:
#   @{
#     recipeId        = '<26-char base32 ULID>'
#     resumeCookId    = '<36-char GUID>' or ''
#     generatedAtUtc  = ISO-8601 string
#     status          = 'ok' | 'warning' | 'blocked'
#     summary         = @{ blocked=int; warning=int; ok=int; notChecked=int }
#     checks          = @( @{
#                            id          = '<stable id>'
#                            label       = '<short human label>'
#                            scope       = 'local'|'pax'|'recipe'|'destination'|
#                                          'auth'|'network'|'resume'
#                            severity    = 'blocker' | 'warning' | 'info'
#                            status      = 'ok' | 'warning' | 'blocked' | 'not_checked'
#                            detail      = '<one-line plain-English>'
#                            evidence    = @{ ... } (secret-free)
#                            remediation = '<plain-English>' or ''
#                          }, ... )
#   }

function Test-CookReadiness {
    # Computes the readiness result. Pure function on the request and
    # current broker state; no HTTP knowledge. Returns the hashtable
    # described above. The caller (Invoke-CookReadiness) is the only
    # site that wraps this in an HTTP envelope; the same function is
    # safe to call from a smoke test that just wants the structured
    # data.
    [CmdletBinding()]
    param(
        [Parameter()] [AllowEmptyString()] [string]$RecipeId     = '',
        [Parameter()] [AllowEmptyString()] [string]$ResumeCookId = ''
    )

    $generated = Get-UtcNowIso
    $checks    = New-Object System.Collections.Generic.List[hashtable]
    $tally     = @{ blocked = 0; warning = 0; ok = 0; not_checked = 0 }

    # Local push helper. PowerShell inner-function scope inherits
    # $checks and $tally by dynamic scoping; we MUTATE them via
    # .Add() and property-set, never reassign, so the closure-style
    # access is safe.
    function Add-Check {
        param(
            [Parameter(Mandatory)] [string]$Id,
            [Parameter(Mandatory)] [string]$Label,
            [Parameter(Mandatory)] [string]$Scope,
            [Parameter(Mandatory)] [string]$Severity,
            [Parameter(Mandatory)] [string]$Status,
            [Parameter()] [AllowEmptyString()] [string]$Detail = '',
            [Parameter()] [hashtable]$Evidence,
            [Parameter()] [AllowEmptyString()] [string]$Remediation = ''
        )
        $ev = if ($Evidence) { $Evidence } else { @{} }
        [void]$checks.Add(@{
            id          = $Id
            label       = $Label
            scope       = $Scope
            severity    = $Severity
            status      = $Status
            detail      = $Detail
            evidence    = $ev
            remediation = $Remediation
        })
        switch ($Status) {
            'blocked'     { $tally.blocked     = $tally.blocked     + 1 }
            'warning'     { $tally.warning     = $tally.warning     + 1 }
            'ok'          { $tally.ok          = $tally.ok          + 1 }
            'not_checked' { $tally.not_checked = $tally.not_checked + 1 }
        }
    }

    # -----------------------------------------------------------------
    # Resolve the recipe under inspection. Three legal entry shapes:
    #   1. RecipeId only           -> stored recipe path
    #   2. ResumeCookId only       -> resume path; recipe is the
    #                                 cook's frozen snapshot
    #   3. Both                    -> resume path (ResumeCookId wins);
    #                                 caller-supplied RecipeId is
    #                                 validated against the cook's
    #                                 row to catch tab-mix-up errors
    # -----------------------------------------------------------------
    $recipe              = $null
    $resolvedRecipeId    = ''
    $resumeContext       = $null   # @{ row=...; resumable=...; checkpointPath=...; humanReason=... }

    if ($ResumeCookId) {
        if ($ResumeCookId -notmatch $Script:CookIdPattern) {
            Add-Check -Id 'resume.cook_id_format' -Label 'Cook id format' `
                -Scope 'resume' -Severity 'blocker' -Status 'blocked' `
                -Detail "cookId '$ResumeCookId' is not a valid GUID." `
                -Evidence @{ cookId = $ResumeCookId } `
                -Remediation 'Reload the cook page to refresh the cook id.'
            return @{
                recipeId       = ''
                resumeCookId   = $ResumeCookId
                generatedAtUtc = $generated
                status         = 'blocked'
                summary        = @{
                    blocked    = $tally.blocked
                    warning    = $tally.warning
                    ok         = $tally.ok
                    notChecked = $tally.not_checked
                }
                checks         = $checks.ToArray()
            }
        }
        $row = Get-CookRow -CookId $ResumeCookId
        if ($null -eq $row) {
            Add-Check -Id 'resume.cook_present' -Label 'Cook record present' `
                -Scope 'resume' -Severity 'blocker' -Status 'blocked' `
                -Detail "No cook record exists for cookId '$ResumeCookId'." `
                -Evidence @{ cookId = $ResumeCookId } `
                -Remediation 'Refresh the cooks list; the cook may have been removed.'
            return @{
                recipeId       = ''
                resumeCookId   = $ResumeCookId
                generatedAtUtc = $generated
                status         = 'blocked'
                summary        = @{
                    blocked    = $tally.blocked
                    warning    = $tally.warning
                    ok         = $tally.ok
                    notChecked = $tally.not_checked
                }
                checks         = $checks.ToArray()
            }
        }
        Add-Check -Id 'resume.cook_present' -Label 'Cook record present' `
            -Scope 'resume' -Severity 'blocker' -Status 'ok' `
            -Detail 'The cook record was loaded from the catalog.' `
            -Evidence @{ cookId = $row.cookId; status = $row.status }
        $resolvedRecipeId = if ($row.recipeId) { [string]$row.recipeId } else { '' }
        # Tab-mix-up guard: if the caller supplied RecipeId AND it
        # disagrees with the cook row, surface a warning. We still
        # use the row's recipeId as the authoritative answer.
        if ($RecipeId -and $resolvedRecipeId -and ($RecipeId -ne $resolvedRecipeId)) {
            Add-Check -Id 'resume.recipe_id_match' -Label 'Recipe id matches cook' `
                -Scope 'resume' -Severity 'warning' -Status 'warning' `
                -Detail "Supplied recipeId '$RecipeId' does not match the cook row's recipeId '$resolvedRecipeId'. Using the cook row's value." `
                -Evidence @{ suppliedRecipeId = $RecipeId; cookRecipeId = $resolvedRecipeId } `
                -Remediation 'Reload the cook page; the recipe-id from the cook row is authoritative.'
        }
        # Resumability classification mirrors the resume route.
        $rm = Test-CookResumable -Row $row
        $cpStatus = if ($rm.resumable) { 'ok' } else { 'blocked' }
        Add-Check -Id 'resume.checkpoint_present' -Label 'Checkpoint present' `
            -Scope 'resume' -Severity 'blocker' -Status $cpStatus `
            -Detail ([string]$rm.humanReason) `
            -Evidence @{
                resumable      = [bool]$rm.resumable
                reason         = [string]$rm.reason
                checkpointPath = if ($rm.checkpointPath) { [string]$rm.checkpointPath } else { '' }
            } `
            -Remediation $(if ($rm.resumable) { '' } else { 'Start a fresh cook; this run is not resumable.' })
        $resumeContext = @{ row = $row; resumable = [bool]$rm.resumable; checkpointPath = $rm.checkpointPath }
        # Load the snapshot. The resume route uses the on-disk snapshot
        # under the cook folder as the authoritative recipe for resume,
        # NOT the (possibly edited) current recipe row.
        $snap = $null
        if ($row.cookFolder) {
            $snap = Read-CookRecipeSnapshot -CookFolder $row.cookFolder
        }
        if ($null -eq $snap -or $snap.ContainsKey('_parseError')) {
            $detail = if ($snap -and $snap.ContainsKey('_parseError')) {
                "recipe-snapshot.json could not be parsed: $($snap._parseError)"
            } else {
                'recipe-snapshot.json is missing from the cook folder.'
            }
            Add-Check -Id 'recipe.snapshot_loadable' -Label 'Recipe snapshot loadable' `
                -Scope 'recipe' -Severity 'blocker' -Status 'blocked' -Detail $detail `
                -Evidence @{ cookFolder = [string]$row.cookFolder } `
                -Remediation 'The cook folder appears damaged; resume is not possible.'
            return @{
                recipeId       = $resolvedRecipeId
                resumeCookId   = $ResumeCookId
                generatedAtUtc = $generated
                status         = 'blocked'
                summary        = @{
                    blocked    = $tally.blocked
                    warning    = $tally.warning
                    ok         = $tally.ok
                    notChecked = $tally.not_checked
                }
                checks         = $checks.ToArray()
            }
        }
        Add-Check -Id 'recipe.snapshot_loadable' -Label 'Recipe snapshot loadable' `
            -Scope 'recipe' -Severity 'blocker' -Status 'ok' `
            -Detail 'Recipe snapshot was parsed from the cook folder.' `
            -Evidence @{ cookFolder = [string]$row.cookFolder }
        $recipe = $snap
    }
    elseif ($RecipeId) {
        if ($RecipeId -notmatch $Script:RecipeIdPattern) {
            Add-Check -Id 'recipe.id_format' -Label 'Recipe id format' `
                -Scope 'recipe' -Severity 'blocker' -Status 'blocked' `
                -Detail "recipeId '$RecipeId' is not a valid 26-character base32 ULID." `
                -Evidence @{ recipeId = $RecipeId } `
                -Remediation 'Reload the editor to refresh the recipe id.'
            return @{
                recipeId       = $RecipeId
                resumeCookId   = ''
                generatedAtUtc = $generated
                status         = 'blocked'
                summary        = @{
                    blocked    = $tally.blocked
                    warning    = $tally.warning
                    ok         = $tally.ok
                    notChecked = $tally.not_checked
                }
                checks         = $checks.ToArray()
            }
        }
        $resolvedRecipeId = $RecipeId
        $row = Get-RecipeRow -RecipeId $RecipeId
        if (-not $row -or $row.deleted_at) {
            Add-Check -Id 'recipe.row_present' -Label 'Recipe record present' `
                -Scope 'recipe' -Severity 'blocker' -Status 'blocked' `
                -Detail "No recipe record exists for recipeId '$RecipeId'." `
                -Evidence @{ recipeId = $RecipeId } `
                -Remediation 'Save the recipe first, or refresh the recipe list.'
            return @{
                recipeId       = $RecipeId
                resumeCookId   = ''
                generatedAtUtc = $generated
                status         = 'blocked'
                summary        = @{
                    blocked    = $tally.blocked
                    warning    = $tally.warning
                    ok         = $tally.ok
                    notChecked = $tally.not_checked
                }
                checks         = $checks.ToArray()
            }
        }
        Add-Check -Id 'recipe.row_present' -Label 'Recipe record present' `
            -Scope 'recipe' -Severity 'blocker' -Status 'ok' `
            -Detail 'The recipe record was loaded from the catalog.' `
            -Evidence @{ recipeId = $RecipeId }
        $loaded = Read-RecipeFile -RecipeId $RecipeId
        $loadStatus = 'ok'
        $loadDetail = 'Recipe file parsed successfully.'
        $loadRemediation = ''
        switch ($loaded.status) {
            'ok' { }
            'missing' {
                $loadStatus      = 'blocked'
                $loadDetail      = 'Recipe file is missing from disk.'
                $loadRemediation = 'Re-save the recipe; the catalog row is stale relative to the filesystem.'
            }
            'malformed' {
                $loadStatus      = 'blocked'
                $loadDetail      = "Recipe file is not valid JSON: $([string]$loaded.detail)"
                $loadRemediation = 'Re-save the recipe from the editor; the on-disk file is corrupt.'
            }
            'unsupported_schema_version' {
                $loadStatus      = 'blocked'
                $loadDetail      = "Recipe file declares an unsupported schema version ($([string]$loaded.detail))."
                $loadRemediation = 'Re-create the recipe; this broker cannot read recipes written by a different schema version.'
            }
            default {
                $loadStatus      = 'blocked'
                $loadDetail      = "Recipe loader returned unknown status '$([string]$loaded.status)'."
            }
        }
        Add-Check -Id 'recipe.loadable' -Label 'Recipe file loadable' `
            -Scope 'recipe' -Severity 'blocker' -Status $loadStatus `
            -Detail $loadDetail -Evidence @{ loaderStatus = [string]$loaded.status } `
            -Remediation $loadRemediation
        if ($loadStatus -ne 'ok') {
            return @{
                recipeId       = $RecipeId
                resumeCookId   = ''
                generatedAtUtc = $generated
                status         = 'blocked'
                summary        = @{
                    blocked    = $tally.blocked
                    warning    = $tally.warning
                    ok         = $tally.ok
                    notChecked = $tally.not_checked
                }
                checks         = $checks.ToArray()
            }
        }
        $recipe = $loaded.recipe
    }
    else {
        Add-Check -Id 'request.input' -Label 'Request input' `
            -Scope 'recipe' -Severity 'blocker' -Status 'blocked' `
            -Detail 'Neither recipeId nor cookId was supplied.' `
            -Evidence @{} `
            -Remediation 'Send {"recipeId":"..."} or {"cookId":"..."} as the request body.'
        return @{
            recipeId       = ''
            resumeCookId   = ''
            generatedAtUtc = $generated
            status         = 'blocked'
            summary        = @{
                blocked    = $tally.blocked
                warning    = $tally.warning
                ok         = $tally.ok
                notChecked = $tally.not_checked
            }
            checks         = $checks.ToArray()
        }
    }

    # -----------------------------------------------------------------
    # Local runtime checks
    # -----------------------------------------------------------------
    $psv = $PSVersionTable.PSVersion
    $psvOk = ($psv.Major -gt 7) -or ($psv.Major -eq 7 -and $psv.Minor -ge 4)
    Add-Check -Id 'local.powershell_version' -Label 'PowerShell version' `
        -Scope 'local' -Severity 'info' `
        -Status $(if ($psvOk) { 'ok' } else { 'warning' }) `
        -Detail $(if ($psvOk) {
            "Broker is running on PowerShell $($psv.ToString())."
        } else {
            "Broker is running on PowerShell $($psv.ToString()); 7.4 or newer is recommended."
        }) `
        -Evidence @{ version = $psv.ToString() } `
        -Remediation $(if ($psvOk) { '' } else { 'Upgrade to PowerShell 7.4 or newer.' })

    $wsPath = if ($Script:WorkspacePath) { [string]$Script:WorkspacePath } else { '' }
    $wsExists = $wsPath -and (Test-Path -LiteralPath $wsPath -PathType Container)
    Add-Check -Id 'local.workspace_present' -Label 'Workspace folder present' `
        -Scope 'local' -Severity 'blocker' `
        -Status $(if ($wsExists) { 'ok' } else { 'blocked' }) `
        -Detail $(if ($wsExists) {
            "Workspace folder exists at '$wsPath'."
        } else {
            "Workspace folder '$wsPath' does not exist or is not a directory."
        }) `
        -Evidence @{ workspacePath = $wsPath } `
        -Remediation $(if ($wsExists) { '' } else { 'Restart the launcher with a valid workspace path.' })

    # Path classification + classic 260 budget. exceedsClassicLimit is
    # the only hard blocker here -- UNC / reparse / removable raise
    # warnings but are not refused at start.
    $wsDiag = Get-WorkspacePathDiagnostic -DisplayPath $wsPath
    $pathBlocked = [bool]$wsDiag.exceedsClassicLimit
    $pathWarn    = (-not $pathBlocked) -and ($wsDiag.warnings.Count -gt 0)
    $pathStatus  = if ($pathBlocked) { 'blocked' } elseif ($pathWarn) { 'warning' } else { 'ok' }
    $pathDetail  =
        if ($pathBlocked) {
            "Workspace path length ($($wsDiag.pathLength) chars) plus the 96-char cook-folder reserve exceeds the classic Win32 MAX_PATH of 260; cooks under this workspace are refused at start."
        } elseif ($pathWarn) {
            'Workspace path is unusual (UNC, reparse point, or removable drive). Cooks may still succeed but durability is best-effort.'
        } else {
            "Workspace path is supported (length=$($wsDiag.pathLength), driveType=$($wsDiag.driveType))."
        }
    Add-Check -Id 'local.workspace_path_supported' -Label 'Workspace path supported' `
        -Scope 'local' -Severity $(if ($pathBlocked) { 'blocker' } else { 'warning' }) `
        -Status $pathStatus -Detail $pathDetail `
        -Evidence @{
            pathLength          = [int]$wsDiag.pathLength
            exceedsClassicLimit = [bool]$wsDiag.exceedsClassicLimit
            isUnc               = [bool]$wsDiag.isUnc
            isReparsePoint      = [bool]$wsDiag.isReparsePoint
            driveType           = [string]$wsDiag.driveType
            warnings            = @($wsDiag.warnings)
        } `
        -Remediation $(if ($pathBlocked) { 'Move the workspace closer to the drive root, or use a shorter folder name.' } else { '' })

    # Disk free precheck against the same threshold the cook-start
    # path uses. Test-CookDiskPrecheck never throws.
    $diskRes = Test-CookDiskPrecheck -Path $wsPath
    $diskStatus = if ($diskRes.ok) { 'ok' } else { 'blocked' }
    Add-Check -Id 'local.workspace_disk_free' -Label 'Workspace free disk space' `
        -Scope 'local' -Severity 'blocker' -Status $diskStatus `
        -Detail $(if ($diskRes.ok) {
            "Drive '$($diskRes.driveName)' has $($diskRes.freeBytes) bytes free; the cook precheck floor is $($diskRes.requiredBytes) bytes."
        } else {
            "Drive precheck failed ($($diskRes.reason)): $($diskRes.detail)"
        }) `
        -Evidence @{
            ok            = [bool]$diskRes.ok
            reason        = [string]$diskRes.reason
            freeBytes     = [long]$diskRes.freeBytes
            requiredBytes = [long]$diskRes.requiredBytes
            driveName     = [string]$diskRes.driveName
        } `
        -Remediation $(if ($diskRes.ok) { '' } else { 'Free disk space on the workspace volume, then re-check readiness.' })

    # -----------------------------------------------------------------
    # PAX script checks (path + integrity). These two are identical
    # to the gates Invoke-CookStart uses immediately before spawn.
    # -----------------------------------------------------------------
    $paxPath = if ($Script:PaxScriptPath) { [string]$Script:PaxScriptPath } else { '' }
    $paxExists = $paxPath -and (Test-Path -LiteralPath $paxPath -PathType Leaf)
    Add-Check -Id 'pax.script_present' -Label 'PAX script present' `
        -Scope 'pax' -Severity 'blocker' `
        -Status $(if ($paxExists) { 'ok' } else { 'blocked' }) `
        -Detail $(if ($paxExists) {
            "Bundled PAX script is on disk at '$paxPath'."
        } else {
            "Bundled PAX script not found at '$paxPath'."
        }) `
        -Evidence @{ paxScriptPath = $paxPath } `
        -Remediation $(if ($paxExists) { '' } else { 'Reinstall Cookbook; the bundled PAX script is missing from the install tree.' })

    $paxHashOk     = $false
    $observedHash  = ''
    $expectedHash  = if ($Script:PaxScriptSha256) { [string]$Script:PaxScriptSha256 } else { '' }
    if ($paxExists -and $expectedHash) {
        try {
            $observedHash = (Get-FileHash -LiteralPath $paxPath -Algorithm SHA256).Hash
            $paxHashOk = ($observedHash -eq $expectedHash)
        } catch {
            $observedHash = ''
            $paxHashOk = $false
        }
    }
    $paxIntegrityStatus = if (-not $paxExists) {
        'blocked'
    } elseif ($paxHashOk) {
        'ok'
    } else {
        'blocked'
    }
    Add-Check -Id 'pax.script_integrity' -Label 'PAX script integrity' `
        -Scope 'pax' -Severity 'blocker' -Status $paxIntegrityStatus `
        -Detail $(if ($paxIntegrityStatus -eq 'ok') {
            'PAX script SHA-256 matches the expected value pinned at broker startup.'
        } elseif (-not $paxExists) {
            'PAX script is missing; integrity cannot be checked.'
        } else {
            'PAX script SHA-256 does not match the expected value.'
        }) `
        -Evidence @{
            expectedSha256 = $expectedHash
            observedSha256 = $observedHash
        } `
        -Remediation $(if ($paxIntegrityStatus -eq 'ok') { '' } else { 'Reinstall Cookbook to restore the bundled PAX script.' })

    # -----------------------------------------------------------------
    # Recipe-level checks (only when we successfully loaded a recipe).
    # -----------------------------------------------------------------
    # In resume mode the snapshot is frozen and the resume route does
    # not re-run Test-RecipeAll. We still report a not_checked entry
    # for visibility.
    if ($null -eq $recipe) {
        # Defensive; earlier branches always return when $recipe is
        # null. Keep the function total.
        return @{
            recipeId       = $resolvedRecipeId
            resumeCookId   = $ResumeCookId
            generatedAtUtc = $generated
            status         = 'blocked'
            summary        = @{
                blocked    = $tally.blocked
                warning    = $tally.warning
                ok         = $tally.ok
                notChecked = $tally.not_checked
            }
            checks         = $checks.ToArray()
        }
    }
    if ($ResumeCookId) {
        Add-Check -Id 'recipe.schema_valid' -Label 'Recipe schema (snapshot)' `
            -Scope 'recipe' -Severity 'info' -Status 'not_checked' `
            -Detail 'Resume uses the frozen recipe snapshot; the schema gate runs only on the live recipe at cook start.' `
            -Evidence @{}
    } else {
        $verdict = Test-RecipeAll -Recipe $recipe
        if ($verdict.ok) {
            Add-Check -Id 'recipe.schema_valid' -Label 'Recipe schema valid' `
                -Scope 'recipe' -Severity 'blocker' -Status 'ok' `
                -Detail 'Recipe passes all schema and cross-field validators.' `
                -Evidence @{}
        } else {
            $firstMsg = ''
            if ($verdict.errors -and $verdict.errors.Count -gt 0 -and $verdict.errors[0].message) {
                $firstMsg = [string]$verdict.errors[0].message
            }
            Add-Check -Id 'recipe.schema_valid' -Label 'Recipe schema valid' `
                -Scope 'recipe' -Severity 'blocker' -Status 'blocked' `
                -Detail ("Recipe has $($verdict.errors.Count) validation error(s)" + $(if ($firstMsg) { "; first: $firstMsg" } else { '.' })) `
                -Evidence @{ errorCount = [int]$verdict.errors.Count } `
                -Remediation 'Open the recipe in the editor; per-field errors will be highlighted.'
        }
    }

    # Execution mode. Only local-manual cooks are eligible for the
    # manual Cook button in M1.
    $execMode = ''
    if ($recipe.ContainsKey('executionMode')) { $execMode = [string]$recipe.executionMode }
    $execOk = ($execMode -eq 'local-manual')
    Add-Check -Id 'recipe.execution_mode' -Label 'Execution mode eligible' `
        -Scope 'recipe' -Severity 'blocker' `
        -Status $(if ($execOk) { 'ok' } else { 'blocked' }) `
        -Detail $(if ($execOk) {
            "Recipe executionMode is 'local-manual'."
        } else {
            "Recipe executionMode is '$execMode'; only 'local-manual' is eligible for manual cooks."
        }) `
        -Evidence @{ executionMode = $execMode } `
        -Remediation $(if ($execOk) { '' } else { "Change the recipe's executionMode to 'local-manual' to use manual Cook." })

    # Auth profile binding. Mirrors Invoke-CookStart's gates; only the
    # AppRegistration variants surface an auth profile -- the other
    # modes have nothing to check here.
    $authMode      = ''
    $authProfileId = ''
    if ($recipe.ContainsKey('auth')) {
        if ($recipe.auth.ContainsKey('mode'))          { $authMode      = [string]$recipe.auth.mode }
        if ($recipe.auth.ContainsKey('authProfileId')) { $authProfileId = [string]$recipe.auth.authProfileId }
    }
    if ($authMode -eq 'AppRegistrationSecret' -or $authMode -eq 'AppRegistrationCertificate') {
        if ([string]::IsNullOrWhiteSpace($authProfileId)) {
            Add-Check -Id 'auth.profile_present' -Label 'Auth profile present' `
                -Scope 'auth' -Severity 'blocker' -Status 'blocked' `
                -Detail "Recipe auth.mode is '$authMode' but no authProfileId is set." `
                -Evidence @{ authMode = $authMode } `
                -Remediation 'Select an auth profile in the editor.'
        } else {
            $apRow = Get-AuthProfileRow -AuthProfileId $authProfileId
            if ($null -eq $apRow) {
                Add-Check -Id 'auth.profile_present' -Label 'Auth profile present' `
                    -Scope 'auth' -Severity 'blocker' -Status 'blocked' `
                    -Detail "Auth profile '$authProfileId' does not exist in the catalog." `
                    -Evidence @{ authProfileId = $authProfileId } `
                    -Remediation 'Open the auth profiles page and create or re-bind a profile.'
            } else {
                Add-Check -Id 'auth.profile_present' -Label 'Auth profile present' `
                    -Scope 'auth' -Severity 'blocker' -Status 'ok' `
                    -Detail 'Auth profile was loaded from the catalog.' `
                    -Evidence @{ authProfileId = $authProfileId }
                $profileMode = [string]$apRow.mode
                $modeOk = ($profileMode -eq $authMode)
                Add-Check -Id 'auth.profile_mode_matches' -Label 'Auth profile mode matches recipe' `
                    -Scope 'auth' -Severity 'blocker' `
                    -Status $(if ($modeOk) { 'ok' } else { 'blocked' }) `
                    -Detail $(if ($modeOk) {
                        "Auth profile mode is '$profileMode'."
                    } else {
                        "Auth profile mode is '$profileMode' but recipe.auth.mode is '$authMode'."
                    }) `
                    -Evidence @{ recipeMode = $authMode; profileMode = $profileMode } `
                    -Remediation $(if ($modeOk) { '' } else { 'Choose an auth profile whose mode matches the recipe, or change the recipe auth mode.' })
                if ($authMode -eq 'AppRegistrationSecret') {
                    $hasSecret = $false
                    try { $hasSecret = [bool](Test-AuthProfileSecretPresent -AuthProfileId $authProfileId) } catch { $hasSecret = $false }
                    Add-Check -Id 'auth.secret_present' -Label 'Client secret present' `
                        -Scope 'auth' -Severity 'blocker' `
                        -Status $(if ($hasSecret) { 'ok' } else { 'blocked' }) `
                        -Detail $(if ($hasSecret) {
                            'Windows Credential Manager has a stored secret for this auth profile.'
                        } else {
                            'No stored client secret was found in Windows Credential Manager for this auth profile.'
                        }) `
                        -Evidence @{ authProfileId = $authProfileId } `
                        -Remediation $(if ($hasSecret) { '' } else { 'Open the auth profile and re-bind the client secret.' })
                } else {
                    Add-Check -Id 'auth.secret_present' -Label 'Client credential present' `
                        -Scope 'auth' -Severity 'info' -Status 'not_checked' `
                        -Detail 'Certificate-mode profiles use the Windows certificate store at cook time; presence is verified by PAX, not by readiness.' `
                        -Evidence @{ authProfileId = $authProfileId; authMode = $authMode }
                }
            }
        }
    } else {
        Add-Check -Id 'auth.profile_present' -Label 'Auth profile present' `
            -Scope 'auth' -Severity 'info' -Status 'not_checked' `
            -Detail "Recipe auth.mode is '$authMode'; no app-registration profile is required." `
            -Evidence @{ authMode = $authMode }
    }

    # Destination checks. The schema already rejected OneLake / Fabric
    # prefixes via Test-RecipeOutputPathTier; the parent-exists probe
    # here is purely advisory.
    $factPath = ''
    if ($recipe.ContainsKey('destinations') -and $recipe.destinations.ContainsKey('fact') -and $recipe.destinations.fact.ContainsKey('path')) {
        $factPath = [string]$recipe.destinations.fact.path
    }
    if ($factPath) {
        $isAbsolute = $false
        try { $isAbsolute = [System.IO.Path]::IsPathRooted($factPath) } catch { $isAbsolute = $false }
        if ($isAbsolute) {
            $parentDir = ''
            try { $parentDir = [System.IO.Path]::GetDirectoryName($factPath) } catch { $parentDir = '' }
            $parentExists = $parentDir -and (Test-Path -LiteralPath $parentDir -PathType Container)
            Add-Check -Id 'destination.parent_exists' -Label 'Destination parent folder exists' `
                -Scope 'destination' -Severity 'warning' `
                -Status $(if ($parentExists) { 'ok' } else { 'warning' }) `
                -Detail $(if ($parentExists) {
                    "Destination parent folder '$parentDir' exists."
                } else {
                    "Destination parent folder '$parentDir' does not exist; PAX will attempt to create it at cook time."
                }) `
                -Evidence @{ factPath = $factPath; parentDir = $parentDir } `
                -Remediation $(if ($parentExists) { '' } else { 'Create the parent folder ahead of time, or let PAX create it at cook time.' })
        } else {
            Add-Check -Id 'destination.parent_exists' -Label 'Destination parent folder exists' `
                -Scope 'destination' -Severity 'info' -Status 'not_checked' `
                -Detail 'Destination path is relative; parent existence depends on the cook-time working directory and is not checked here.' `
                -Evidence @{ factPath = $factPath }
        }
    }

    # Append behavior. Only checkable for absolute paths in append mode.
    $appendBehavior = ''
    $appendFile     = ''
    if ($recipe.ContainsKey('destinations') -and $recipe.destinations.ContainsKey('fact')) {
        $fact = $recipe.destinations.fact
        if ($fact.ContainsKey('appendBehavior')) { $appendBehavior = [string]$fact.appendBehavior }
        if ($fact.ContainsKey('appendFile'))     { $appendFile     = [string]$fact.appendFile }
    }
    if ($appendBehavior -eq 'append') {
        if ($appendFile) {
            $appendAbs = $false
            try { $appendAbs = [System.IO.Path]::IsPathRooted($appendFile) } catch { $appendAbs = $false }
            if ($appendAbs) {
                $appendThere = Test-Path -LiteralPath $appendFile -PathType Leaf
                Add-Check -Id 'destination.append_target_exists' -Label 'Append target exists' `
                    -Scope 'destination' -Severity 'warning' `
                    -Status $(if ($appendThere) { 'ok' } else { 'warning' }) `
                    -Detail $(if ($appendThere) {
                        "Append target '$appendFile' exists on disk."
                    } else {
                        "Append target '$appendFile' does not exist; PAX will create it (header compatibility is checked at cook time)."
                    }) `
                    -Evidence @{ appendFile = $appendFile } `
                    -Remediation $(if ($appendThere) { '' } else { '' })
            } else {
                Add-Check -Id 'destination.append_target_exists' -Label 'Append target exists' `
                    -Scope 'destination' -Severity 'info' -Status 'not_checked' `
                    -Detail 'Append target is a relative path; existence depends on the cook-time working directory and is not checked here.' `
                    -Evidence @{ appendFile = $appendFile }
            }
        }
        Add-Check -Id 'destination.append_header_compat' -Label 'Append header compatibility' `
            -Scope 'destination' -Severity 'info' -Status 'not_checked' `
            -Detail 'PAX validates append-target header compatibility at cook time. The broker does not pre-read the file.' `
            -Evidence @{}
    }

    # Command projection. Mirror the Invoke-CookStart gate that calls
    # Get-PaxInvocationPlan with the resolved auth profile row. Surfaces
    # projection-time failures (e.g. removed-switch trailer) as a
    # blocker, exactly as cook-start would.
    $authProfileRowForPlan = $null
    if ($authMode -eq 'AppRegistrationSecret' -or $authMode -eq 'AppRegistrationCertificate') {
        if ($authProfileId) {
            try { $authProfileRowForPlan = Get-AuthProfileRow -AuthProfileId $authProfileId } catch { $authProfileRowForPlan = $null }
        }
    }
    try {
        $plan = Get-PaxInvocationPlan -Recipe $recipe -PaxScriptPath $Script:PaxScriptPath -AuthProfile $authProfileRowForPlan -ExecutionMode $execMode
        Add-Check -Id 'recipe.command_projection' -Label 'PAX command projection' `
            -Scope 'recipe' -Severity 'blocker' -Status 'ok' `
            -Detail 'PAX invocation plan was successfully projected from the recipe.' `
            -Evidence @{ argvTokenCount = if ($plan -and $plan.paxArgv) { [int]@($plan.paxArgv).Count } else { 0 } }
    } catch {
        Add-Check -Id 'recipe.command_projection' -Label 'PAX command projection' `
            -Scope 'recipe' -Severity 'blocker' -Status 'blocked' `
            -Detail ('PAX invocation plan could not be projected: ' + [string]$_.Exception.Message) `
            -Evidence @{} `
            -Remediation 'Open the recipe in the editor; the offending field (usually advanced.extraArguments) will be highlighted.'
    }

    # Network / reachability. These are intentionally not_checked.
    # A truthful probe would need OAuth credentials and would burn a
    # tenant request before the cook even runs; that is exactly what
    # the slice doctrine forbids.
    Add-Check -Id 'network.graph_reachability' -Label 'Microsoft Graph reachability' `
        -Scope 'network' -Severity 'info' -Status 'not_checked' `
        -Detail 'Graph reachability is not pre-flighted: it would require the same OAuth credentials a cook would use, and we do not burn a token before the cook attempt. PAX surfaces auth failures at cook time.' `
        -Evidence @{}
    Add-Check -Id 'network.purview_reachability' -Label 'Purview API reachability' `
        -Scope 'network' -Severity 'info' -Status 'not_checked' `
        -Detail 'Purview reachability is not pre-flighted; PAX surfaces transport-layer failures at cook time with full evidence.' `
        -Evidence @{}

    # -----------------------------------------------------------------
    # Overall status reduction. Blockers dominate warnings; warnings
    # dominate ok. Not-checked is informational and never affects the
    # overall verdict.
    # -----------------------------------------------------------------
    $overall =
        if ($tally.blocked -gt 0)      { 'blocked' }
        elseif ($tally.warning -gt 0)  { 'warning' }
        else                           { 'ok' }

    return @{
        recipeId       = $resolvedRecipeId
        resumeCookId   = $ResumeCookId
        generatedAtUtc = $generated
        status         = $overall
        summary        = @{
            blocked    = $tally.blocked
            warning    = $tally.warning
            ok         = $tally.ok
            notChecked = $tally.not_checked
        }
        checks         = $checks.ToArray()
    }
}

function Invoke-CookReadiness {
    # HTTP handler for POST /api/v1/cooks/readiness.
    #
    # Request body: { "recipeId": "..." } OR { "cookId": "..." }.
    # Always returns 200 with the readiness payload (including when
    # status='blocked' -- the caller decides what to do). 400 is
    # returned only on transport-shape problems (non-JSON body, empty
    # body). The endpoint is read-only and does NOT spawn PAX, mutate
    # any catalog row, or write any file on disk.
    param($Context)
    $body = Read-RequestJson -Context $Context
    if ($null -eq $body) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }
    $rid = ''
    $cid = ''
    if ($body.ContainsKey('recipeId') -and $null -ne $body.recipeId) { $rid = [string]$body.recipeId }
    if ($body.ContainsKey('cookId')   -and $null -ne $body.cookId)   { $cid = [string]$body.cookId }
    $result = Test-CookReadiness -RecipeId $rid -ResumeCookId $cid
    Write-JsonResponse -Context $Context -Status 200 -Body $result
}

# ---------------------------------------------------------------------
# Route dispatch entry point
# ---------------------------------------------------------------------

function Invoke-CooksRoute {
    # Returns $true if the request was consumed by this handler.
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    # POST /api/v1/recipes/<id>/cook  (recipeId is ULID, 26 chars)
    if ($path -match '^/api/v1/recipes/([0-9A-HJKMNP-TV-Z]{26})/cook$') {
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-CookStart -Context $Context -RecipeId $matches[1]
        return $true
    }

    # /api/v1/cooks
    if ($path -eq '/api/v1/cooks') {
        if ($method -ne 'GET') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-CooksList -Context $Context
        return $true
    }

    # /api/v1/cooks/<cookId>/stop
    if ($path -match '^/api/v1/cooks/([^/]+)/stop$') {
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-CookStop -Context $Context -CookId $matches[1]
        return $true
    }

    # /api/v1/cooks/<cookId>/kill
    if ($path -match '^/api/v1/cooks/([^/]+)/kill$') {
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-CookKill -Context $Context -CookId $matches[1]
        return $true
    }

    # /api/v1/cooks/<cookId>/resume   (V1.S03)
    if ($path -match '^/api/v1/cooks/([^/]+)/resume$') {
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-CookResume -Context $Context -CookId $matches[1]
        return $true
    }

    # /api/v1/cooks/<cookId>/log
    if ($path -match '^/api/v1/cooks/([^/]+)/log$') {
        if ($method -ne 'GET') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-CookLog -Context $Context -CookId $matches[1]
        return $true
    }

    # /api/v1/cooks/readiness   (V1.S04) -- MUST precede the catch-all
    # /api/v1/cooks/<cookId> matcher; 'readiness' is a literal segment,
    # not a cookId.
    if ($path -eq '/api/v1/cooks/readiness') {
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-CookReadiness -Context $Context
        return $true
    }

    # /api/v1/cooks/<cookId>
    if ($path -match '^/api/v1/cooks/([^/]+)$') {
        if ($method -ne 'GET') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-CookGet -Context $Context -CookId $matches[1]
        return $true
    }

    return $false
}
