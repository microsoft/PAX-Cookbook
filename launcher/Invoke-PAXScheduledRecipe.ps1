#requires -Version 7.4

# =====================================================================
# Invoke-PAXScheduledRecipe.ps1
#
# V1.S06c -- the bundled scheduler WRAPPER. Fired by Windows Task
# Scheduler for any task registered via
# app/install/Register-PAXScheduledRecipe.ps1.
#
# This launcher does NOT depend on the broker. It does not POST,
# does not import broker route code, and does not touch the
# Windows Task Scheduler. It spawns the bundled PAX engine
# DIRECTLY as a child process, with the recipe's client secret
# delivered through the child's environment block exactly the
# same way the broker's Cook/Supervisor.ps1 does it.
#
# This is the doctrine the chef confirmed in V1.S06b:
#   PAX is the execution engine.
#   Windows Task Scheduler is the scheduler.
#   Cookbook is the authoring, registration, wrapper, and history
#   / review shell.
# The wrapper is the only bridge between the OS-side trigger and
# the bundled engine. When the wrapper is running, the broker may
# be entirely offline; the operator's session may be locked; the
# UI may not even exist. The wrapper's contract is to fire the
# bundled engine if and only if the recipe is still in the exact
# state the operator approved at registration time, and to leave
# enough on-disk evidence for the broker to reconcile after the
# fact.
#
# Inputs (mandatory argv passed by Task Scheduler):
#   -WorkspacePath <path to the cookbook workspace>
#   -RecipeId <26-char Crockford base32 ULID>
#   -ScheduledTaskId <26-char Crockford base32 ULID>
#
# On-disk evidence written under <WorkspacePath>\Cooks\<cookId>\:
#   wrapper-started.json    -- envelope written BEFORE PAX spawn.
#   wrapper-finished.json   -- envelope written AFTER PAX exit
#                              (success OR failure).
#   wrapper-refused.json    -- envelope written when the wrapper
#                              refuses to spawn PAX (stale
#                              projection, missing scheduled_tasks
#                              row, etc.). Mutually exclusive with
#                              wrapper-finished.json.
#   cook.log                -- PAX stdout, line-buffered.
#   cook.err.log            -- PAX stderr, line-buffered.
#
# Exit codes (Windows Task Scheduler logs these in the "Last Run
# Result" column so they need to be self-explanatory):
#   0    PAX exited zero. Cook succeeded.
#   1-29 PAX exited non-zero. Forwarded verbatim from the engine.
#   30   Wrapper refused. Reasons captured in wrapper-refused.json.
#        Stale projection, missing scheduled_tasks row, recipe
#        unreadable, auth profile missing, secret missing.
#   31   Wrapper internal error. Reasons captured in
#        wrapper-finished.json (when the cook folder exists) or
#        stderr (when the failure happened before the folder was
#        created).
#   32   Wrapper refused: PAX engine acquisition is required. The
#        VERSION.json acquisitionPolicy is 'external' and the
#        install-state.json paxAcquisition block does not record
#        an activated engine. wrapper-refused.json carries
#        reason='acquisition_required' with the probed state. The
#        operator must complete the engine-acquisition flow in the
#        broker SPA before this scheduled task can fire. Distinct
#        exit code (vs 30) so Task Scheduler's Last Run Result
#        column distinguishes acquisition-required refusals from
#        other wrapper refusals without opening the cook folder.
#
# The wrapper does NOT write to the cooks or cook_artifacts
# tables; the broker owns those. The wrapper writes file-system
# sentinels that the broker's discovery sweep (V1.S06d) imports
# into the cooks table when the operator next opens the editor.
# This split keeps the wrapper's SQLite footprint to a single
# read-only connection that opens only long enough to fetch the
# scheduled_tasks row.
# =====================================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$WorkspacePath,

    [Parameter(Mandatory=$true)]
    [string]$RecipeId,

    [Parameter(Mandatory=$true)]
    [string]$ScheduledTaskId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------

$Script:RecipeIdPattern            = '^[0-9A-HJKMNP-TV-Z]{26}$'
$Script:ScheduledTaskExecutionMode = 'local-scheduled'
$Script:WrapperExitOK                  = 0
$Script:WrapperExitRefused             = 30
$Script:WrapperExitInternal            = 31
$Script:WrapperExitAcquisitionRequired = 32

# ---------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------

# The launcher lives at <repoRoot>\launcher\Invoke-PAXScheduledRecipe.ps1.
# The bundled engine + libraries live under <repoRoot>\app\. The
# wrapper is invoked by Task Scheduler with a known WorkingDirectory
# set to the launcher's parent folder, but we deliberately re-derive
# every path from $PSCommandPath so the wrapper works regardless of
# whether the cwd was preserved.
$Script:LauncherRoot = Split-Path -Parent $PSCommandPath
$Script:RepoRoot     = Split-Path -Parent $Script:LauncherRoot
$Script:AppRoot      = Join-Path $Script:RepoRoot 'app'
$Script:SqliteDir    = Join-Path $Script:AppRoot 'lib\sqlite'
$Script:PaxScriptPath = Join-Path $Script:AppRoot 'resources\pax\PAX_Purview_Audit_Log_Processor.ps1'
$Script:AdapterPath  = Join-Path $Script:AppRoot 'broker\Pax\Adapter.psm1'
$Script:CredManagerPath = Join-Path $Script:AppRoot 'broker\Auth\CredentialManager.ps1'
$Script:VersionFile  = Join-Path $Script:AppRoot 'VERSION.json'

# ---------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------

function Get-UtcNowIso {
    return ([DateTime]::UtcNow.ToString('o'))
}

function New-CookId {
    # 26-char Crockford base32 ULID. Same alphabet the recipes use.
    $alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'.ToCharArray()
    $sb = New-Object System.Text.StringBuilder
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $buf = New-Object byte[] 26
        $rng.GetBytes($buf)
        foreach ($b in $buf) { [void]$sb.Append($alphabet[[int]($b -band 0x1f)]) }
    } finally { $rng.Dispose() }
    return $sb.ToString()
}

function Write-WrapperJson {
    # Writes a JSON sentinel atomically into the cook folder. The
    # broker's discovery sweeper reads these so the format must stay
    # stable across wrapper releases.
    param(
        [Parameter(Mandatory)][string]$CookFolder,
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)] $Body
    )
    $path = Join-Path $CookFolder $FileName
    $tmp  = $path + '.tmp'
    $json = ($Body | ConvertTo-Json -Depth 12)
    [System.IO.File]::WriteAllText($tmp, $json, [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tmp -Destination $path -Force
}

function Write-WrapperRefusal {
    # Emits wrapper-refused.json with a stable shape and exits 30.
    # The cook folder is created BEFORE this is called so the
    # operator can inspect the recipe snapshot frozen at refusal time.
    param(
        [Parameter(Mandatory)][string]$CookFolder,
        [Parameter(Mandatory)][string]$Reason,
        [hashtable]$Detail = @{}
    )
    $now = Get-UtcNowIso
    $body = [ordered]@{
        kind             = 'wrapper-refused'
        recipeId         = $RecipeId
        scheduledTaskId  = $ScheduledTaskId
        cookId           = $Script:CurrentCookId
        refusedAt        = $now
        reason           = $Reason
        detail           = $Detail
        workspacePath    = $WorkspacePath
        wrapperHost      = [System.Net.Dns]::GetHostName()
        wrapperUser      = ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
    }
    try { Write-WrapperJson -CookFolder $CookFolder -FileName 'wrapper-refused.json' -Body $body } catch { }
    [Console]::Error.WriteLine('[Wrapper] refused: ' + $Reason)
    exit $Script:WrapperExitRefused
}

function Write-WrapperInternalError {
    # Used for "wrapper internal" failures (loadable but unrecoverable).
    # Distinct from refusal: refusal means "the recipe / schedule
    # state is wrong"; internal means "the wrapper itself failed".
    param(
        [string]$CookFolder = $null,
        [Parameter(Mandatory)][string]$Reason,
        [hashtable]$Detail = @{}
    )
    if ($CookFolder -and (Test-Path -LiteralPath $CookFolder -PathType Container)) {
        try {
            Write-WrapperJson -CookFolder $CookFolder -FileName 'wrapper-finished.json' -Body ([ordered]@{
                kind            = 'wrapper-internal-error'
                recipeId        = $RecipeId
                scheduledTaskId = $ScheduledTaskId
                cookId          = $Script:CurrentCookId
                finishedAt      = (Get-UtcNowIso)
                paxExitCode     = $null
                wrapperOutcome  = 'wrapper_internal'
                wrapperReason   = $Reason
                wrapperDetail   = $Detail
            })
        } catch { }
    }
    [Console]::Error.WriteLine('[Wrapper] internal error: ' + $Reason)
    exit $Script:WrapperExitInternal
}

function Test-PaxEngineAcquiredLocally {
    # Pure filesystem probe of PAX engine acquisition state. Does
    # NOT call the broker over HTTP: the launcher's broker-
    # independence doctrine (file header) requires the wrapper to
    # spawn PAX without any running broker. The probe mirrors the
    # decision tree in Engine\Acquisition.psm1::Get-PaxAcquisitionState
    # so the wrapper agrees with the broker on whether the engine
    # is acquired:
    #   1. Resolve acquisitionPolicy from <appRoot>\VERSION.json.
    #      Missing field, missing file, or any parse failure
    #      collapses onto 'embedded' (legacy bundled behavior).
    #   2. policy = 'embedded' -> acquired (the bundled engine
    #      ships in the install tree; integrity is asserted by
    #      Test-Path of $Script:PaxScriptPath at spawn time, NOT
    #      here -- this probe only owns the acquisition decision).
    #   3. policy = 'external' -> read
    #      %LOCALAPPDATA%\PAXCookbook\install-state.json,
    #      extract the paxAcquisition block, and require:
    #         pending != true
    #         activatedAtUtc is set
    #         $Script:PaxScriptPath exists on disk
    #      All three must hold to report acquired = $true.
    # Returns:
    #   @{ acquired = <bool>; policy = '...'; state = '...';
    #      reason = <string|null>;
    #      installStatePath = <path|null>;
    #      lastAttemptError = <string|null> }
    [CmdletBinding()] param()

    $policy = 'embedded'
    try {
        if (Test-Path -LiteralPath $Script:VersionFile -PathType Leaf) {
            $vr = [System.IO.File]::ReadAllText($Script:VersionFile, [System.Text.UTF8Encoding]::new($false))
            $vd = $vr | ConvertFrom-Json -Depth 16 -ErrorAction Stop
            if ($null -ne $vd -and $vd.PSObject.Properties.Match('paxScript').Count -ne 0) {
                $px = $vd.paxScript
                if ($null -ne $px -and $px.PSObject.Properties.Match('acquisitionPolicy').Count -ne 0) {
                    $cand = [string]$px.acquisitionPolicy
                    if ($cand -eq 'external' -or $cand -eq 'embedded') { $policy = $cand }
                }
            }
        }
    } catch { $policy = 'embedded' }

    if ($policy -eq 'embedded') {
        return @{
            acquired         = $true
            policy           = $policy
            state            = 'embedded-legacy'
            reason           = $null
            installStatePath = $null
            lastAttemptError = $null
        }
    }

    $stateFile = $null
    try {
        $local = $env:LOCALAPPDATA
        if ([string]::IsNullOrWhiteSpace($local)) {
            $local = [Environment]::GetFolderPath('LocalApplicationData')
        }
        if (-not [string]::IsNullOrWhiteSpace($local)) {
            $stateFile = Join-Path (Join-Path $local 'PAXCookbook') 'install-state.json'
        }
    } catch { $stateFile = $null }

    if (-not $stateFile -or -not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
        return @{
            acquired         = $false
            policy           = $policy
            state            = 'acquisition_pending'
            reason           = 'install_state_missing'
            installStatePath = $stateFile
            lastAttemptError = $null
        }
    }

    try {
        $sr = [System.IO.File]::ReadAllText($stateFile, [System.Text.UTF8Encoding]::new($false))
        $sd = $sr | ConvertFrom-Json -AsHashtable -Depth 12 -ErrorAction Stop
    } catch {
        return @{
            acquired         = $false
            policy           = $policy
            state            = 'acquisition_pending'
            reason           = 'install_state_unreadable'
            installStatePath = $stateFile
            lastAttemptError = $null
        }
    }
    if ($null -eq $sd -or -not ($sd -is [System.Collections.IDictionary]) -or -not $sd.Contains('paxAcquisition')) {
        return @{
            acquired         = $false
            policy           = $policy
            state            = 'acquisition_pending'
            reason           = 'install_state_block_missing'
            installStatePath = $stateFile
            lastAttemptError = $null
        }
    }
    $blk = $sd['paxAcquisition']
    if ($null -eq $blk -or -not ($blk -is [System.Collections.IDictionary])) {
        return @{
            acquired         = $false
            policy           = $policy
            state            = 'acquisition_pending'
            reason           = 'install_state_block_malformed'
            installStatePath = $stateFile
            lastAttemptError = $null
        }
    }

    $pendingFlag    = $false
    if ($blk.Contains('pending') -and $null -ne $blk['pending']) { $pendingFlag = [bool]$blk['pending'] }
    $hasActivated   = $blk.Contains('activatedAtUtc') -and -not [string]::IsNullOrWhiteSpace([string]$blk['activatedAtUtc'])
    $hasLastError   = $blk.Contains('lastAttemptError') -and -not [string]::IsNullOrWhiteSpace([string]$blk['lastAttemptError'])
    $scriptPresent  = (-not [string]::IsNullOrWhiteSpace($Script:PaxScriptPath)) -and (Test-Path -LiteralPath $Script:PaxScriptPath -PathType Leaf)

    if (-not $pendingFlag -and $hasActivated -and $scriptPresent) {
        return @{
            acquired         = $true
            policy           = $policy
            state            = 'acquired'
            reason           = $null
            installStatePath = $stateFile
            lastAttemptError = $null
        }
    }
    if ($pendingFlag -and $hasLastError) {
        return @{
            acquired         = $false
            policy           = $policy
            state            = 'failed'
            reason           = 'last_attempt_failed'
            installStatePath = $stateFile
            lastAttemptError = [string]$blk['lastAttemptError']
        }
    }
    return @{
        acquired         = $false
        policy           = $policy
        state            = 'acquisition_pending'
        reason           = if ($scriptPresent) { 'not_yet_activated' } else { 'canonical_script_missing' }
        installStatePath = $stateFile
        lastAttemptError = if ($hasLastError) { [string]$blk['lastAttemptError'] } else { $null }
    }
}

function Write-WrapperAcquisitionRefusal {
    # Dedicated refusal writer for the acquisition gate so the cook
    # folder carries a wrapper-refused.json whose detail block is
    # rich enough for the broker's discovery sweep + the SPA's
    # cook-history view to surface the actionable "acquire the
    # engine" CTA without re-probing install-state.json.
    param(
        [Parameter(Mandatory)][string]$CookFolder,
        [Parameter(Mandatory)][hashtable]$Probe
    )
    $now = Get-UtcNowIso
    $detail = [ordered]@{
        error            = 'acquisition_required'
        state            = $Probe.state
        policy           = $Probe.policy
        atUtc            = $now
        endpoint         = 'wrapper:Invoke-PAXScheduledRecipe.ps1'
        probeReason      = $Probe.reason
        installStatePath = $Probe.installStatePath
        lastAttemptError = $Probe.lastAttemptError
    }
    $body = [ordered]@{
        kind            = 'wrapper-refused'
        recipeId        = $RecipeId
        scheduledTaskId = $ScheduledTaskId
        cookId          = $Script:CurrentCookId
        refusedAt       = $now
        reason          = 'acquisition_required'
        detail          = $detail
        workspacePath   = $WorkspacePath
        wrapperHost     = [System.Net.Dns]::GetHostName()
        wrapperUser     = ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
    }
    try { Write-WrapperJson -CookFolder $CookFolder -FileName 'wrapper-refused.json' -Body $body } catch { }
    [Console]::Error.WriteLine('[Wrapper] refused: acquisition_required (state=' + [string]$Probe.state + ')')
    exit $Script:WrapperExitAcquisitionRequired
}

# ---------------------------------------------------------------------
# SQLite read-only access
# ---------------------------------------------------------------------

function Initialize-SqliteRuntime {
    # Mirrors the broker's Initialize-SqliteRuntime. Same DLL load
    # order; same provider init. Re-issuing Init() inside the same
    # AppDomain is safe (SQLitePCLRaw guards against double-init).
    if ($env:PATH -notlike ($Script:SqliteDir + '*')) {
        $env:PATH = $Script:SqliteDir + ';' + $env:PATH
    }
    $assemblies = @(
        'SQLitePCLRaw.core.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll',
        'SQLitePCLRaw.batteries_v2.dll',
        'Microsoft.Data.Sqlite.dll'
    )
    foreach ($name in $assemblies) {
        $path = Join-Path $Script:SqliteDir $name
        Add-Type -Path $path
    }
    [SQLitePCL.Batteries_V2]::Init()
}

function Open-SqliteConnectionReadOnly {
    # Opens cookbook.sqlite in read-only WAL mode. The wrapper does
    # not write to SQLite (decision: avoid contention with a running
    # broker); all state changes the wrapper makes are file-system
    # only, and the broker's V1.S06d sweeper imports them later.
    param([Parameter(Mandatory)][string]$DatabaseFile)
    $csb = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
    $csb.DataSource = $DatabaseFile
    $csb.Mode       = [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadOnly
    $csb.Pooling    = $false
    $conn = [Microsoft.Data.Sqlite.SqliteConnection]::new($csb.ConnectionString)
    $conn.Open()
    foreach ($pragma in @(
        'PRAGMA busy_timeout=5000;',
        'PRAGMA foreign_keys=ON;'
    )) {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $pragma
        [void]$cmd.ExecuteNonQuery()
        $cmd.Dispose()
    }
    return $conn
}

function Get-ScheduledTaskRow {
    # Reads the scheduled_tasks row for the given recipe. Returns
    # $null if absent. We intentionally read by recipe_id (which is
    # UNIQUE in the schema) rather than scheduled_task_id so the
    # wrapper still works if the task was re-registered with a fresh
    # scheduledTaskId since the last fire (the OS-side task name and
    # the new row are kept in sync by the registrar).
    param(
        [Microsoft.Data.Sqlite.SqliteConnection]$Connection,
        [Parameter(Mandatory)][string]$RecipeIdArg
    )
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @'
SELECT scheduled_task_id, recipe_id, windows_task_name,
       recipe_projection_hash, pax_script_version, status
FROM scheduled_tasks
WHERE recipe_id = $rid;
'@
    [void]$cmd.Parameters.AddWithValue('$rid', $RecipeIdArg)
    $reader = $cmd.ExecuteReader()
    try {
        if ($reader.Read()) {
            return [ordered]@{
                scheduledTaskId      = [string]$reader.GetValue(0)
                recipeId             = [string]$reader.GetValue(1)
                windowsTaskName      = [string]$reader.GetValue(2)
                recipeProjectionHash = [string]$reader.GetValue(3)
                paxScriptVersion     = [string]$reader.GetValue(4)
                status               = [string]$reader.GetValue(5)
            }
        }
        return $null
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
}

function Get-AuthProfileRow {
    # Reads the auth_profiles row by id. Wrapper-local copy so we do
    # not have to dot-source Routes\AuthProfiles.ps1 (which would pull
    # in the entire broker stack). Returns $null if absent.
    param(
        [Microsoft.Data.Sqlite.SqliteConnection]$Connection,
        [Parameter(Mandatory)][string]$AuthProfileId
    )
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @'
SELECT auth_profile_id, name, mode, tenant_id, client_id,
       cert_thumbprint, cred_man_target, created_at, updated_at
FROM auth_profiles
WHERE auth_profile_id = $apid;
'@
    [void]$cmd.Parameters.AddWithValue('$apid', $AuthProfileId)
    $reader = $cmd.ExecuteReader()
    try {
        if ($reader.Read()) {
            return [ordered]@{
                auth_profile_id  = [string]$reader.GetValue(0)
                name             = [string]$reader.GetValue(1)
                mode             = [string]$reader.GetValue(2)
                tenantId         = if ($reader.IsDBNull(3)) { $null } else { [string]$reader.GetValue(3) }
                clientId         = if ($reader.IsDBNull(4)) { $null } else { [string]$reader.GetValue(4) }
                certThumbprint   = if ($reader.IsDBNull(5)) { $null } else { [string]$reader.GetValue(5) }
                cred_man_target  = if ($reader.IsDBNull(6)) { $null } else { [string]$reader.GetValue(6) }
                created_at       = [string]$reader.GetValue(7)
                updated_at       = [string]$reader.GetValue(8)
            }
        }
        return $null
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
}

# ---------------------------------------------------------------------
# Recipe loading
# ---------------------------------------------------------------------

function Read-RecipeFromDisk {
    # Loads a recipe by reading <WorkspacePath>\Recipes\<recipeId>\recipe.json.
    # Returns @{ ok=<bool>; recipe=<hashtable or $null>; reason=<string or $null> }.
    # The wrapper does NOT run Test-RecipeAll: the projection-hash
    # comparison is the authoritative gate. A drifted recipe will
    # fail the hash check; a malformed recipe will fail JSON parse;
    # an absent recipe will fail Test-Path.
    param([Parameter(Mandatory)][string]$RecipeIdArg)
    $recipePath = Join-Path (Join-Path $WorkspacePath 'Recipes') (Join-Path $RecipeIdArg 'recipe.json')
    if (-not (Test-Path -LiteralPath $recipePath -PathType Leaf)) {
        return @{ ok = $false; recipe = $null; reason = 'recipe_file_missing: ' + $recipePath }
    }
    try {
        $raw  = [System.IO.File]::ReadAllText($recipePath, [System.Text.UTF8Encoding]::new($false))
        # -DateKind String mirrors Routes\Recipes.ps1::Read-RecipeFile.
        # Without it, PowerShell 7.5+ auto-coerces ISO 8601 timestamps
        # to [DateTime], which would change the string representation
        # of any argv-bound timestamp field and produce a different
        # projection hash than the broker stored at registration time
        # (= every fire becomes a spurious stale refusal). -Depth 12
        # matches the broker's parse depth.
        $hash = $raw | ConvertFrom-Json -AsHashtable -Depth 12 -DateKind String -ErrorAction Stop
        return @{ ok = $true; recipe = $hash; reason = $null }
    } catch {
        return @{ ok = $false; recipe = $null; reason = 'recipe_file_malformed: ' + $_.Exception.Message }
    }
}

function Get-BundledPaxScriptVersion {
    # Reads the bundled PAX engine's declared version from
    # <appRoot>/VERSION.json under paxScript.version. The broker uses
    # this EXACT source at startup (Test-BundledPaxIntegrity in
    # Start-Broker.ps1), and the projection hash that the broker
    # stored at registration time was computed with whatever value
    # was in VERSION.json then. Re-reading the engine's
    # $ScriptVersion declaration would risk producing a different
    # value if VERSION.json and the engine drift apart, which would
    # turn every wrapper invocation into a stale refusal. Single
    # source of truth: VERSION.json.
    if (-not (Test-Path -LiteralPath $Script:VersionFile -PathType Leaf)) {
        return $null
    }
    try {
        $raw = [System.IO.File]::ReadAllText($Script:VersionFile, [System.Text.UTF8Encoding]::new($false))
        $doc = $raw | ConvertFrom-Json -Depth 16 -ErrorAction Stop
        if ($doc.PSObject.Properties.Match('paxScript').Count -eq 0) { return $null }
        $v = [string]$doc.paxScript.version
        if ([string]::IsNullOrWhiteSpace($v)) { return $null }
        return $v
    } catch {
        return $null
    }
}

# ---------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------

$Script:CurrentCookId = $null

try {
    # ---- 1. Argument validation -------------------------------------
    if ([string]::IsNullOrWhiteSpace($WorkspacePath)) {
        [Console]::Error.WriteLine('[Wrapper] internal_error: workspace_path_empty')
        exit $Script:WrapperExitInternal
    }
    $workspaceFull = $null
    try { $workspaceFull = [System.IO.Path]::GetFullPath($WorkspacePath) } catch { $workspaceFull = $null }
    if (-not $workspaceFull -or -not (Test-Path -LiteralPath $workspaceFull -PathType Container)) {
        [Console]::Error.WriteLine('[Wrapper] internal_error: workspace_path_missing: ' + $WorkspacePath)
        exit $Script:WrapperExitInternal
    }
    $WorkspacePath = $workspaceFull

    if ($RecipeId -notmatch $Script:RecipeIdPattern) {
        [Console]::Error.WriteLine('[Wrapper] internal_error: invalid_recipe_id: ' + $RecipeId)
        exit $Script:WrapperExitInternal
    }
    if ($ScheduledTaskId -notmatch $Script:RecipeIdPattern) {
        [Console]::Error.WriteLine('[Wrapper] internal_error: invalid_scheduled_task_id: ' + $ScheduledTaskId)
        exit $Script:WrapperExitInternal
    }

    # ---- 2. Cook folder ---------------------------------------------
    # We create the cook folder UP FRONT so refusal envelopes have a
    # home. The folder name is the cookId so the broker's V1.S06d
    # sweeper can index it directly.
    $Script:CurrentCookId = New-CookId
    $cooksDir = Join-Path $WorkspacePath 'Cooks'
    if (-not (Test-Path -LiteralPath $cooksDir -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $cooksDir -Force)
    }
    $cookFolder = Join-Path $cooksDir $Script:CurrentCookId
    [void](New-Item -ItemType Directory -Path $cookFolder -Force)

    # ---- 2b. Acquisition gate (Phase 13 Stage 4) --------------------
    # Refuse the fire BEFORE any SQLite open / recipe read / secret
    # read when policy='external' and the engine is not yet
    # acquired. Pure filesystem probe -- the launcher's broker-
    # independence doctrine forbids an HTTP call here. Exit code 32
    # is distinct from 30 so Task Scheduler's Last Run Result column
    # surfaces "acquisition required" without the operator needing
    # to open the cook folder.
    $acqProbe = Test-PaxEngineAcquiredLocally
    if (-not $acqProbe.acquired) {
        Write-WrapperAcquisitionRefusal -CookFolder $cookFolder -Probe $acqProbe
    }

    # ---- 3. Database open --------------------------------------------
    $dbFile = Join-Path (Join-Path $WorkspacePath 'Database') 'cookbook.sqlite'
    if (-not (Test-Path -LiteralPath $dbFile -PathType Leaf)) {
        Write-WrapperRefusal -CookFolder $cookFolder -Reason 'database_missing' -Detail @{ databaseFile = $dbFile }
    }
    Initialize-SqliteRuntime
    $conn = $null
    $taskRow = $null
    $authProfile = $null
    try {
        $conn = Open-SqliteConnectionReadOnly -DatabaseFile $dbFile
        $taskRow = Get-ScheduledTaskRow -Connection $conn -RecipeIdArg $RecipeId
        if (-not $taskRow) {
            $conn.Close()
            Write-WrapperRefusal -CookFolder $cookFolder -Reason 'scheduled_task_not_registered' -Detail @{ recipeId = $RecipeId }
        }
        if ([string]$taskRow.status -ne 'active') {
            $conn.Close()
            Write-WrapperRefusal -CookFolder $cookFolder -Reason 'scheduled_task_inactive' -Detail @{ recipeId = $RecipeId; status = $taskRow.status }
        }
        if ($taskRow.scheduledTaskId -ne $ScheduledTaskId) {
            # The OS-side task fires with the scheduledTaskId baked
            # in at registration time. If the stored row carries a
            # different scheduledTaskId, the operator unregistered +
            # re-registered between this trigger arming and firing.
            # Refuse: the new registration's wrapper will fire next.
            $conn.Close()
            Write-WrapperRefusal -CookFolder $cookFolder -Reason 'scheduled_task_id_mismatch' -Detail @{
                triggerScheduledTaskId = $ScheduledTaskId
                storedScheduledTaskId  = $taskRow.scheduledTaskId
            }
        }
    } catch {
        if ($conn) { try { $conn.Close() } catch { } }
        Write-WrapperInternalError -CookFolder $cookFolder -Reason ('database_read_failed: ' + $_.Exception.Message)
    }

    # ---- 4. Recipe + auth profile ------------------------------------
    $loaded = Read-RecipeFromDisk -RecipeIdArg $RecipeId
    if (-not $loaded.ok) {
        try { $conn.Close() } catch { }
        Write-WrapperRefusal -CookFolder $cookFolder -Reason 'recipe_unreadable' -Detail @{ recipeId = $RecipeId; reason = $loaded.reason }
    }
    $recipe = $loaded.recipe

    if (-not $recipe.ContainsKey('auth') -or -not $recipe.auth.ContainsKey('authProfileId') -or [string]::IsNullOrWhiteSpace([string]$recipe.auth.authProfileId)) {
        try { $conn.Close() } catch { }
        Write-WrapperRefusal -CookFolder $cookFolder -Reason 'recipe_auth_profile_missing' -Detail @{ recipeId = $RecipeId }
    }
    $apid = [string]$recipe.auth.authProfileId

    try {
        $authProfile = Get-AuthProfileRow -Connection $conn -AuthProfileId $apid
    } catch {
        try { $conn.Close() } catch { }
        Write-WrapperInternalError -CookFolder $cookFolder -Reason ('auth_profile_read_failed: ' + $_.Exception.Message)
    } finally {
        try { $conn.Close() } catch { }
    }
    if (-not $authProfile) {
        Write-WrapperRefusal -CookFolder $cookFolder -Reason 'auth_profile_not_found' -Detail @{ authProfileId = $apid }
    }

    $authMode = [string]$authProfile.mode
    if ($authMode -notin @('AppRegistrationSecret', 'AppRegistrationCertificate')) {
        Write-WrapperRefusal -CookFolder $cookFolder -Reason 'auth_mode_unsupported_for_schedule' -Detail @{ authMode = $authMode }
    }

    # ---- 5. Projection-hash recompute --------------------------------
    # Import the broker's Adapter module so we use the IDENTICAL
    # projection logic the broker used at registration time. Any
    # divergence between the two paths would surface as a stale
    # refusal even when nothing actually changed; importing the same
    # source is the only correct way to keep them in lockstep.
    if (-not (Test-Path -LiteralPath $Script:AdapterPath -PathType Leaf)) {
        Write-WrapperInternalError -CookFolder $cookFolder -Reason ('adapter_module_missing: ' + $Script:AdapterPath)
    }
    Import-Module -Name $Script:AdapterPath -Force -ErrorAction Stop
    $paxScriptVersion = Get-BundledPaxScriptVersion
    if ([string]::IsNullOrWhiteSpace($paxScriptVersion)) {
        Write-WrapperInternalError -CookFolder $cookFolder -Reason 'pax_script_version_unreadable'
    }

    $currentHash = $null
    try {
        $currentHash = Get-RecipeProjectionHash `
                            -Recipe $recipe `
                            -PaxScriptPath $Script:PaxScriptPath `
                            -AuthProfile $authProfile `
                            -ExecutionMode $Script:ScheduledTaskExecutionMode `
                            -PaxScriptVersion $paxScriptVersion
    } catch {
        Write-WrapperInternalError -CookFolder $cookFolder -Reason ('projection_hash_failed: ' + $_.Exception.Message)
    }
    if ($currentHash -ne $taskRow.recipeProjectionHash) {
        Write-WrapperRefusal -CookFolder $cookFolder -Reason 'refused_stale_projection' -Detail @{
            storedProjectionHash    = $taskRow.recipeProjectionHash
            currentProjectionHash   = $currentHash
            storedPaxScriptVersion  = $taskRow.paxScriptVersion
            currentPaxScriptVersion = $paxScriptVersion
        }
    }

    # ---- 6. Auth secret read (AppRegistrationSecret) -----------------
    # Only AppRegistrationSecret requires the wrapper to read a
    # plaintext secret from CredMan and place it on the child's env
    # block as GRAPH_CLIENT_SECRET. AppRegistrationCertificate uses
    # a thumbprint that PAX dereferences against the user's local
    # cert store directly; no secret flows through the wrapper.
    if (-not (Test-Path -LiteralPath $Script:CredManagerPath -PathType Leaf)) {
        Write-WrapperInternalError -CookFolder $cookFolder -Reason ('credential_manager_missing: ' + $Script:CredManagerPath)
    }
    . $Script:CredManagerPath
    $clientSecretSecure = $null
    if ($authMode -eq 'AppRegistrationSecret') {
        try {
            $clientSecretSecure = Read-AuthProfileSecret -AuthProfileId $apid
        } catch {
            Write-WrapperRefusal -CookFolder $cookFolder -Reason 'auth_profile_secret_missing' -Detail @{
                authProfileId = $apid
                detail        = $_.Exception.Message
            }
        }
        if ($null -eq $clientSecretSecure -or $clientSecretSecure -isnot [System.Security.SecureString]) {
            Write-WrapperRefusal -CookFolder $cookFolder -Reason 'auth_profile_secret_missing' -Detail @{ authProfileId = $apid }
        }
    }

    # ---- 7. PAX invocation plan --------------------------------------
    $plan = $null
    try {
        $plan = Get-PaxInvocationPlan `
                    -Recipe $recipe `
                    -PaxScriptPath $Script:PaxScriptPath `
                    -AuthProfile $authProfile `
                    -ExecutionMode $Script:ScheduledTaskExecutionMode
    } catch {
        Write-WrapperInternalError -CookFolder $cookFolder -Reason ('projection_plan_failed: ' + $_.Exception.Message)
    }

    # ---- 8. wrapper-started envelope (BEFORE spawn) ------------------
    $startedAt = Get-UtcNowIso
    Write-WrapperJson -CookFolder $cookFolder -FileName 'wrapper-started.json' -Body ([ordered]@{
        kind                  = 'wrapper-started'
        recipeId              = $RecipeId
        scheduledTaskId       = $ScheduledTaskId
        cookId                = $Script:CurrentCookId
        executionMode         = $Script:ScheduledTaskExecutionMode
        startedAt             = $startedAt
        wrapperHost           = [System.Net.Dns]::GetHostName()
        wrapperUser           = ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
        paxScriptPath         = $Script:PaxScriptPath
        paxScriptVersion      = $paxScriptVersion
        recipeProjectionHash  = $currentHash
        recipeName            = if ($recipe.ContainsKey('name')) { [string]$recipe.name } else { $null }
        authProfileId         = $apid
        authMode              = $authMode
        spawnArgv             = @($plan.spawnArgv)
        windowsTaskName       = $taskRow.windowsTaskName
    })

    # ---- 9. Spawn PAX ------------------------------------------------
    # Locate pwsh. The launcher itself is running under pwsh, so the
    # current process's executable path is the right interpreter to
    # spawn the engine with.
    $pwshPath = (Get-Process -Id $PID).Path
    if (-not $pwshPath -or -not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) {
        $cmd = Get-Command pwsh.exe -ErrorAction SilentlyContinue
        if ($cmd) { $pwshPath = $cmd.Source }
    }
    if (-not $pwshPath -or -not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) {
        if ($clientSecretSecure) { try { $clientSecretSecure.Dispose() } catch { } }
        Write-WrapperInternalError -CookFolder $cookFolder -Reason 'pwsh_not_found'
    }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName               = $pwshPath
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.CreateNoWindow         = $true
    $psi.WorkingDirectory       = $cookFolder
    foreach ($a in $plan.spawnArgv) { [void]$psi.ArgumentList.Add([string]$a) }

    # Phase AG -- secret-isolation invariants (same as broker
    # supervisor):
    #   1. Wrapper process MUST NOT call SetEnvironmentVariable for
    #      GRAPH_CLIENT_SECRET.
    #   2. The secret reaches the child ONLY via
    #      $psi.EnvironmentVariables AND UseShellExecute=false.
    #   3. The BSTR is ZeroFreed and the env entry removed from
    #      psi.EnvironmentVariables immediately after $proc.Start()
    #      returns, success or failure.
    $secretBSTR   = [IntPtr]::Zero
    $secretEnvSet = $false
    $spawnErr     = $null
    if ($clientSecretSecure -is [System.Security.SecureString]) {
        try {
            $secretBSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($clientSecretSecure)
            $plaintextSecret = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretBSTR)
            $psi.EnvironmentVariables['GRAPH_CLIENT_SECRET'] = $plaintextSecret
            $secretEnvSet = $true
            $plaintextSecret = $null
        } catch {
            $spawnErr = 'client_secret_marshal_failed: ' + $_.Exception.Message
        }
    }

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    $proc.EnableRaisingEvents = $true

    $outFs = $null
    $errFs = $null
    $outTask = $null
    $errTask = $null
    $startedSuccessfully = $false
    if (-not $spawnErr) {
        try {
            [void]$proc.Start()
            $startedSuccessfully = $true
            $outFs = [System.IO.File]::Create((Join-Path $cookFolder 'cook.log'))
            $errFs = [System.IO.File]::Create((Join-Path $cookFolder 'cook.err.log'))
            # CopyToAsync runs both stream pumps concurrently without
            # PowerShell event-handler indirection. Both tasks
            # complete when the child closes its end of the pipe,
            # which happens at process exit.
            $outTask = $proc.StandardOutput.BaseStream.CopyToAsync($outFs)
            $errTask = $proc.StandardError.BaseStream.CopyToAsync($errFs)
        } catch {
            $spawnErr = $_.Exception.Message
        }
    }

    # Zero the secret as soon as Start() returns (or fails). Mirrors
    # the supervisor's Phase AG zeroing pattern.
    if ($secretBSTR -ne [IntPtr]::Zero) {
        try { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretBSTR) } catch { }
        $secretBSTR = [IntPtr]::Zero
    }
    if ($secretEnvSet) {
        try {
            if ($psi.EnvironmentVariables.ContainsKey('GRAPH_CLIENT_SECRET')) {
                [void]$psi.EnvironmentVariables.Remove('GRAPH_CLIENT_SECRET')
            }
        } catch { }
        $secretEnvSet = $false
    }
    if ($clientSecretSecure) {
        try { $clientSecretSecure.Dispose() } catch { }
        $clientSecretSecure = $null
    }

    if ($spawnErr) {
        # Spawn failed. Dispose any open streams, write the
        # wrapper-finished envelope with outcome=spawn_failed, and
        # exit 31.
        if ($outFs) { try { $outFs.Dispose() } catch { } }
        if ($errFs) { try { $errFs.Dispose() } catch { } }
        Write-WrapperJson -CookFolder $cookFolder -FileName 'wrapper-finished.json' -Body ([ordered]@{
            kind             = 'wrapper-finished'
            recipeId         = $RecipeId
            scheduledTaskId  = $ScheduledTaskId
            cookId           = $Script:CurrentCookId
            startedAt        = $startedAt
            finishedAt       = (Get-UtcNowIso)
            paxExitCode      = $null
            wrapperOutcome   = 'spawn_failed'
            wrapperReason    = $spawnErr
            durationMs       = $null
            pidValue         = $null
        })
        [Console]::Error.WriteLine('[Wrapper] spawn_failed: ' + $spawnErr)
        exit $Script:WrapperExitInternal
    }

    # ---- 10. Wait for PAX exit ---------------------------------------
    $childPid = $proc.Id
    $proc.WaitForExit()
    $paxExitCode = [int]$proc.ExitCode
    # Allow pending async stream reads to flush. Each task completes
    # when the child closes its end of the pipe; an upper bound of
    # 30 seconds is generous (the OS closes pipes essentially
    # synchronously when the process exits).
    foreach ($t in @($outTask, $errTask)) {
        if ($null -ne $t) {
            try { [void]$t.Wait(30000) } catch { }
        }
    }
    if ($outFs) { try { $outFs.Flush(); $outFs.Dispose() } catch { } }
    if ($errFs) { try { $errFs.Flush(); $errFs.Dispose() } catch { } }
    try { $proc.Dispose() } catch { }

    # ---- 11. wrapper-finished envelope -------------------------------
    $finishedAt = Get-UtcNowIso
    $startedDt  = [DateTime]::Parse($startedAt,  [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
    $finishedDt = [DateTime]::Parse($finishedAt, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
    $durationMs = [int](($finishedDt - $startedDt).TotalMilliseconds)

    $outcome = if ($paxExitCode -eq 0) { 'pax_ok' } else { 'pax_nonzero' }
    Write-WrapperJson -CookFolder $cookFolder -FileName 'wrapper-finished.json' -Body ([ordered]@{
        kind             = 'wrapper-finished'
        recipeId         = $RecipeId
        scheduledTaskId  = $ScheduledTaskId
        cookId           = $Script:CurrentCookId
        startedAt        = $startedAt
        finishedAt       = $finishedAt
        paxExitCode      = $paxExitCode
        wrapperOutcome   = $outcome
        wrapperReason    = $null
        durationMs       = $durationMs
        pidValue         = $childPid
    })

    if ($paxExitCode -eq 0) {
        exit $Script:WrapperExitOK
    }
    # Forward PAX's non-zero exit verbatim. Operator sees the same
    # code in Task Scheduler's "Last Run Result" column that they
    # would see at the engine's command line.
    exit $paxExitCode

} catch {
    # Catch-all for unanticipated wrapper failures. The cook folder
    # may or may not exist depending on where we failed.
    $cf = if ($Script:CurrentCookId) {
        $candidate = Join-Path (Join-Path $WorkspacePath 'Cooks') $Script:CurrentCookId
        if (Test-Path -LiteralPath $candidate -PathType Container) { $candidate } else { $null }
    } else { $null }
    Write-WrapperInternalError -CookFolder $cf -Reason ('unhandled_exception: ' + $_.Exception.Message) -Detail @{
        stackTrace = [string]$_.ScriptStackTrace
    }
}
