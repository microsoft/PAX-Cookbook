#requires -Version 7.4

# =====================================================================
# Set-ScheduledCook.ps1
#
# Phase M3.1 -- minimal operator-facing scheduled cook configuration.
#
# The single operator entry point for configuring (or removing) the
# bundled PAX Cookbook scheduled task. It is the only operator-facing
# script that knows how to author the strict scheduler.json sentinel.
#
# What this script does (and ONLY what this script does):
#
#   -RecipeId <ULID> -DailyTime <HH:mm>
#       1. Validate the recipe ULID shape.
#       2. Validate the daily fire time as 24-hour HH:mm.
#       3. Confirm the recipe row exists in the workspace's
#          recipes table. Refuses if the recipe is missing.
#       4. Write/overwrite <Workspace>\scheduler.json atomically
#          in the exact 3-property shape required by the broker
#          classifier and the launcher:
#              { "enabled": true,
#                "recipe_id": "<26-char ULID>",
#                "daily_time": "HH:mm" }
#       5. Delegate to app/install/Register-PAXScheduler.ps1
#          -Register to (re-)register the single Windows scheduled
#          task. Re-registering replaces the existing task in
#          place; that is the 'update' path.
#
#   -Remove
#       1. Delegate to app/install/Register-PAXScheduler.ps1
#          -Unregister to remove the Windows scheduled task. No-op
#          if the task does not exist.
#       2. Delete <Workspace>\scheduler.json if present.
#
# Hard rules (enforced by smoke_m3_1.ps1):
#
#   - No retries. Validate, write, register; exit.
#   - No multiple schedules. ONE task, ONE fire time, ONE recipe.
#   - No weekly / weekend / cron logic. Daily HH:mm only.
#   - No catch-up / replay / missed-run handling.
#   - No lifecycle tracking. The sentinel records configuration,
#     not state.
#   - No *-ScheduledTask cmdlets here. Task registration lives in
#     app/install/Register-PAXScheduler.ps1; this script only
#     invokes that surface.
#   - No browser/UI changes. No broker API changes. No new HTTP
#     route. No new SQLite table. No new /health key. No new
#     exit code outside the small operator-visible map below.
#
# Exit code map (small, operator-visible):
#
#    0  Success (configured + registered, or removed).
#    2  Workspace path does not exist or is not a directory.
#    3  Recipe ULID shape is invalid (not 26-char Crockford).
#    4  Daily time string is not 24-hour HH:mm.
#    5  Recipe row not found in the workspace's recipes table.
#    6  Delegated registration / unregistration call failed
#       (the inner script's exit code is surfaced verbatim).
# =====================================================================

[CmdletBinding(DefaultParameterSetName='Configure')]
param(
    [Parameter(Mandatory=$true)]
    [string]$WorkspacePath,

    [Parameter(Mandatory=$true, ParameterSetName='Configure')]
    [string]$RecipeId,

    [Parameter(Mandatory=$true, ParameterSetName='Configure')]
    [string]$DailyTime,

    [Parameter(Mandatory=$true, ParameterSetName='Remove')]
    [switch]$Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-RegisterScriptPath {
    $launcherDir = Split-Path -Parent $PSCommandPath
    $repoRoot    = Split-Path -Parent $launcherDir
    $registerPath = Join-Path -Path $repoRoot -ChildPath (Join-Path 'app' (Join-Path 'install' 'Register-PAXScheduler.ps1'))
    if (-not (Test-Path -LiteralPath $registerPath -PathType Leaf)) {
        throw ('Cannot locate Register-PAXScheduler.ps1 at expected path: ' + $registerPath)
    }
    return $registerPath
}

function Resolve-PwshExe {
    $exe = (Get-Process -Id $PID).Path
    if ($exe -and (Test-Path -LiteralPath $exe -PathType Leaf)) { return $exe }
    $cmd = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'Cannot locate pwsh.exe.'
}

function Invoke-RegisterScript {
    param([string[]]$ArgList)
    $exe = Resolve-PwshExe
    # Pipe to Out-Host so the child's stdout reaches the console
    # without polluting this function's return pipeline. Without
    # this, the returned $LASTEXITCODE would be wrapped with any
    # text the child wrote, breaking the integer comparison.
    & $exe -NoProfile -NoLogo -ExecutionPolicy Bypass -File @ArgList | Out-Host
    return $LASTEXITCODE
}

function Test-RecipeIdInWorkspace {
    param(
        [string]$WorkspaceFull,
        [string]$RecipeId
    )
    # Look up the recipe row in the workspace's recipes table.
    # The workspace database is the same one the broker opens; we
    # use the same path convention (Runtime/cookbook.sqlite by
    # default) and SQLite read-only access. If the broker is
    # currently running, WAL mode allows concurrent readers.
    $dbCandidates = @(
        (Join-Path -Path (Join-Path -Path $WorkspaceFull -ChildPath 'Runtime') -ChildPath 'cookbook.sqlite'),
        (Join-Path -Path $WorkspaceFull -ChildPath 'cookbook.sqlite')
    )
    $dbPath = $null
    foreach ($c in $dbCandidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { $dbPath = $c; break }
    }
    if (-not $dbPath) {
        # No DB on disk -- treat as 'recipe not found' rather than
        # crashing, so the operator sees a clear exit 5 instead of
        # a stack trace.
        return $false
    }
    try {
        Add-Type -AssemblyName 'System.Data.SQLite' -ErrorAction SilentlyContinue
    } catch {}
    $sqliteType = [Type]::GetType('System.Data.SQLite.SQLiteConnection, System.Data.SQLite', $false)
    if (-not $sqliteType) {
        # Fall back to Microsoft.Data.Sqlite if vendored alongside.
        $sqliteType = [Type]::GetType('Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite', $false)
    }
    if (-not $sqliteType) {
        # No SQLite ADO.NET provider available in this process.
        # Treat as 'cannot verify' which we surface as 'not found'
        # so the operator never gets a half-configured task that
        # would fail at fire time.
        return $false
    }
    $connStr = 'Data Source=' + $dbPath + ';Mode=ReadOnly;Cache=Shared;'
    $conn = [Activator]::CreateInstance($sqliteType, @($connStr))
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = 'SELECT 1 FROM recipes WHERE recipe_id = @rid LIMIT 1'
        $p = $cmd.CreateParameter(); $p.ParameterName = '@rid'; $p.Value = $RecipeId
        [void]$cmd.Parameters.Add($p)
        $r = $cmd.ExecuteScalar()
        return ($null -ne $r)
    } finally {
        try { $conn.Close() } catch {}
        try { $conn.Dispose() } catch {}
    }
}

# 1. Workspace.
$workspaceFull = [System.IO.Path]::GetFullPath($WorkspacePath)
if (-not (Test-Path -LiteralPath $workspaceFull -PathType Container)) {
    Write-Host ('Workspace path does not exist: ' + $workspaceFull) -ForegroundColor Red
    exit 2
}

$sentinelPath = Join-Path -Path $workspaceFull -ChildPath 'scheduler.json'
$registerPath = Resolve-RegisterScriptPath

if ($Remove.IsPresent) {
    # 2a. Unregister first (so a partial failure leaves no stale
    # task pointing at a sentinel we are about to delete).
    $rc = Invoke-RegisterScript -ArgList @($registerPath, '-WorkspacePath', $workspaceFull, '-Unregister')
    if ($rc -ne 0) {
        Write-Host ('Register-PAXScheduler.ps1 -Unregister exited ' + $rc) -ForegroundColor Red
        exit 6
    }
    if (Test-Path -LiteralPath $sentinelPath -PathType Leaf) {
        Remove-Item -LiteralPath $sentinelPath -Force
        Write-Host ('Removed sentinel: ' + $sentinelPath) -ForegroundColor Green
    } else {
        Write-Host ('No sentinel to remove: ' + $sentinelPath) -ForegroundColor Yellow
    }
    exit 0
}

# 2b. Configure path: validate, write sentinel, delegate register.

# Recipe ULID shape.
if ($RecipeId -notmatch '^[0-9A-HJKMNP-TV-Z]{26}$') {
    Write-Host ('RecipeId is not a 26-char Crockford ULID: ' + $RecipeId) -ForegroundColor Red
    exit 3
}

# Daily time format (24-hour HH:mm).
if ($DailyTime -notmatch '^([01][0-9]|2[0-3]):[0-5][0-9]$') {
    Write-Host ('DailyTime is not 24-hour HH:mm: ' + $DailyTime) -ForegroundColor Red
    exit 4
}

# Recipe existence.
if (-not (Test-RecipeIdInWorkspace -WorkspaceFull $workspaceFull -RecipeId $RecipeId)) {
    Write-Host ('Recipe not found in workspace recipes table: ' + $RecipeId) -ForegroundColor Red
    exit 5
}

# Build the strict 3-property sentinel object. Property order in
# the emitted JSON is the order of insertion into the ordered
# dictionary; the classifier sorts names so emit order is purely
# cosmetic. The classifier rejects any wider shape.
$sentinelObj = [ordered]@{
    enabled    = $true
    recipe_id  = $RecipeId
    daily_time = $DailyTime
}
$json = ($sentinelObj | ConvertTo-Json -Compress)

# Atomic write: .tmp + Move.
$sentinelTmp = $sentinelPath + '.tmp'
[System.IO.File]::WriteAllText($sentinelTmp, $json, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::Move($sentinelTmp, $sentinelPath, $true)
Write-Host ('Wrote sentinel: ' + $sentinelPath) -ForegroundColor Green

# Delegate registration.
$rc = Invoke-RegisterScript -ArgList @($registerPath, '-WorkspacePath', $workspaceFull, '-Register')
if ($rc -ne 0) {
    Write-Host ('Register-PAXScheduler.ps1 -Register exited ' + $rc) -ForegroundColor Red
    exit 6
}
exit 0
