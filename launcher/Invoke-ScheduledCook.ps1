#requires -Version 7.4

# =====================================================================
# Invoke-ScheduledCook.ps1
#
# Phase AI.C9.1 -- scheduler runtime, entry slice (launcher half).
#
# The bundled scheduler launcher. Fired by the Windows scheduled
# task registered via app/install/Register-PAXScheduler.ps1. Reads
# the validated scheduler.json sentinel in $WorkspacePath, looks up
# the broker's loopback port and session token from the workspace
# Runtime/ directory, and POSTs to the existing broker route
# /api/v1/recipes/<ULID>/cook with the AI.C9.1 origin header
#
#     X-PAX-Origin: scheduler
#
# attached. Auth, CSRF, and origin headers are byte-identical to a
# manual browser POST -- the broker cannot tell them apart on the
# cook execution path. The only difference is the observation row
# the broker writes when X-PAX-Origin: scheduler is present.
#
# Hard rules:
#
#   - No retry. ONE POST; exit code = mapped HTTP status / error.
#   - No catch-up. If the broker isn't running, exit 21 and stop.
#   - No replay. No queueing, no on-disk pending list.
#   - No resume. No state carried across runs.
#   - No fallback. If sentinel doesn't classify or token sidecar
#     is absent, exit non-zero and stop.
#   - No *-ScheduledTask cmdlets. The launcher does not touch the
#     task registry; only the install script does.
#   - No background jobs, no timers, no events.
#
# Exit code map (small, observation-only; the broker side never
# reads these; they exist so Task Scheduler's run history is
# self-explanatory to an operator inspecting the task):
#
#   0   POST succeeded with HTTP 2xx.
#  10   $WorkspacePath does not exist or is not a directory.
#  11   scheduler.json missing or classifies as malformed.
#  12   workspace.lock missing -- broker is not running.
#  13   workspace.lock is malformed.
#  14   broker.token sidecar missing or empty -- broker either
#       isn't running or is too old to expose the AI.C9.1 sidecar.
#  20   POST returned a non-2xx HTTP status.
#  21   POST could not connect (broker not listening on the port
#       recorded in workspace.lock).
#  22   POST raised an unrecognized exception.
# =====================================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$WorkspacePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SchedulerSentinelClassificationLocal {
    # Structurally identical to broker's Get-SchedulerSentinelClassification
    # and the install script's local mirror. Smoke pins all three.
    param([Parameter(Mandatory=$true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return 'scheduler_absent'
    }
    try {
        $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        if ($null -eq $raw -or $raw.Trim().Length -eq 0) {
            return 'scheduler_malformed_optin'
        }
        $obj = $raw | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $obj) { return 'scheduler_malformed_optin' }
        $props = @($obj.PSObject.Properties | Where-Object { $_.MemberType -eq 'NoteProperty' })
        if ($props.Count -ne 3) { return 'scheduler_malformed_optin' }
        $names = @($props | ForEach-Object { $_.Name } | Sort-Object)
        if ($names[0] -ne 'daily_time' -or $names[1] -ne 'enabled' -or $names[2] -ne 'recipe_id') {
            return 'scheduler_malformed_optin'
        }
        $enabledVal = ($props | Where-Object { $_.Name -eq 'enabled' }).Value
        if ($enabledVal -isnot [bool] -or $enabledVal -ne $true) {
            return 'scheduler_malformed_optin'
        }
        $recipeVal = ($props | Where-Object { $_.Name -eq 'recipe_id' }).Value
        if ($recipeVal -isnot [string]) { return 'scheduler_malformed_optin' }
        if ($recipeVal -notmatch '^[0-9A-HJKMNP-TV-Z]{26}$') {
            return 'scheduler_malformed_optin'
        }
        $dailyVal = ($props | Where-Object { $_.Name -eq 'daily_time' }).Value
        if ($dailyVal -isnot [string]) { return 'scheduler_malformed_optin' }
        if ($dailyVal -notmatch '^([01][0-9]|2[0-3]):[0-5][0-9]$') {
            return 'scheduler_malformed_optin'
        }
        return 'scheduler_detected'
    } catch {
        return 'scheduler_malformed_optin'
    }
}

# 1. Workspace.
$workspaceFull = [System.IO.Path]::GetFullPath($WorkspacePath)
if (-not (Test-Path -LiteralPath $workspaceFull -PathType Container)) {
    Write-Host ('Workspace path does not exist: ' + $workspaceFull) -ForegroundColor Red
    exit 10
}

# 2. Sentinel + recipe.
$sentinelPath = Join-Path -Path $workspaceFull -ChildPath 'scheduler.json'
$classification = Get-SchedulerSentinelClassificationLocal -Path $sentinelPath
if ($classification -ne 'scheduler_detected') {
    Write-Host ('scheduler.json does not classify as scheduler_detected (got: ' + $classification + ') at ' + $sentinelPath) -ForegroundColor Red
    exit 11
}
$sentinel = Get-Content -LiteralPath $sentinelPath -Raw | ConvertFrom-Json
$recipeId = [string]$sentinel.recipe_id

# 3. Broker port from workspace.lock.
$lockPath = Join-Path -Path (Join-Path -Path $workspaceFull -ChildPath 'Runtime') -ChildPath 'workspace.lock'
if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
    Write-Host ('Broker workspace.lock missing -- broker is not running: ' + $lockPath) -ForegroundColor Red
    exit 12
}
$lock = $null
try {
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
} catch {
    Write-Host ('workspace.lock is malformed: ' + $_.Exception.Message) -ForegroundColor Red
    exit 13
}
if ($null -eq $lock -or -not $lock.brokerPort -or [int]$lock.brokerPort -le 0) {
    Write-Host 'workspace.lock has no brokerPort.' -ForegroundColor Red
    exit 13
}
$brokerPort = [int]$lock.brokerPort

# 4. Session token from broker.token sidecar.
$tokenPath = Join-Path -Path (Join-Path -Path $workspaceFull -ChildPath 'Runtime') -ChildPath 'broker.token'
if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) {
    Write-Host ('broker.token sidecar missing -- broker not running, or installed broker predates AI.C9.1: ' + $tokenPath) -ForegroundColor Red
    exit 14
}
$token = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
if (-not $token -or $token.Length -eq 0) {
    Write-Host 'broker.token sidecar is empty.' -ForegroundColor Red
    exit 14
}

# 5. POST to the existing cook-trigger route. Headers are byte-
# identical to a manual browser POST, plus the AI.C9.1 origin tag.
$uri = 'http://127.0.0.1:' + $brokerPort + '/api/v1/recipes/' + $recipeId + '/cook'
$headers = @{
    'Authorization'      = 'Bearer ' + $token
    'X-Cookbook-Request' = '1'
    'X-PAX-Origin'       = 'scheduler'
    'Content-Type'       = 'application/json; charset=utf-8'
}

try {
    $resp = Invoke-WebRequest -Uri $uri -Method Post -Headers $headers -Body '{}' -UseBasicParsing -ErrorAction Stop
    $status = [int]$resp.StatusCode
    if ($status -ge 200 -and $status -lt 300) {
        Write-Host ('Scheduled cook triggered: HTTP ' + $status + ' for recipe ' + $recipeId) -ForegroundColor Green
        exit 0
    }
    Write-Host ('Scheduled cook trigger returned non-2xx: HTTP ' + $status) -ForegroundColor Red
    exit 20
} catch [System.Net.WebException] {
    Write-Host ('Scheduled cook POST failed (WebException): ' + $_.Exception.Message) -ForegroundColor Red
    exit 21
} catch {
    $msg = $_.Exception.Message
    if ($msg -match 'actively refused|No connection could be made|Unable to connect') {
        Write-Host ('Broker not reachable on port ' + $brokerPort + ': ' + $msg) -ForegroundColor Red
        exit 21
    }
    if ($_.Exception.Response) {
        $status = [int]$_.Exception.Response.StatusCode
        Write-Host ('Scheduled cook trigger returned non-2xx: HTTP ' + $status) -ForegroundColor Red
        exit 20
    }
    Write-Host ('Scheduled cook POST raised: ' + $msg) -ForegroundColor Red
    exit 22
}
