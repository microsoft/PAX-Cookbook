#requires -Version 7.4

# =====================================================================
# Register-PAXScheduler.ps1
#
# Phase AI.C9.1 -- scheduler runtime, entry slice.
#
# Registers (or unregisters) a single Windows scheduled task that
# fires the bundled scheduler launcher once per day. The launcher
# (launcher/Invoke-ScheduledCook.ps1) reads the validated
# scheduler.json sentinel and POSTs to the existing broker route
# /api/v1/recipes/<ULID>/cook exactly as a manual browser would.
#
# This script lives in the INSTALL surface (app/install/), NOT in
# the broker. It is the ONLY place in the entire codebase that
# calls *-ScheduledTask cmdlets. The broker has zero scheduler
# runtime in it; the AI.C8.2 invariant
# (final_c4_no_scheduledtask_runtime_in_broker) remains in force.
#
# Hard rules (enforced by smoke_ai_c9_1.ps1):
#
#   - No retries. The script registers a task or unregisters it,
#     once, and exits.
#   - No reconciliation. The script does not inspect or fix any
#     existing-but-different task; -Force replaces, period.
#   - No hidden state. The only state this script writes is the
#     scheduled task itself; no sidecar files, no registry.
#   - No catch-up. The registered task has StartWhenAvailable OFF
#     and no missed-run compensation.
#   - No cron parser. Trigger is one fixed daily time.
#   - Refuse registration unless scheduler.json classifies as
#     'scheduler_detected' via the strict opt-in classifier
#     (same logic as broker's Get-SchedulerSentinelClassification).
#
# Modes:
#
#   -Register      Register the daily task. Requires that
#                  scheduler.json classifies as scheduler_detected.
#                  The fire time is read from the sentinel's
#                  daily_time property (M3.1). Re-registering with
#                  -Force replaces the existing task in place; that
#                  is the 'update' path.
#   -Unregister    Remove the task if present. No-op if absent.
# =====================================================================

[CmdletBinding(DefaultParameterSetName='Register')]
param(
    [Parameter(Mandatory=$true)]
    [string]$WorkspacePath,

    [Parameter(ParameterSetName='Register')]
    [switch]$Register,

    [Parameter(ParameterSetName='Unregister')]
    [switch]$Unregister,

    [Parameter(ParameterSetName='Register')]
    [string]$LauncherPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Script:TaskName = 'PAX Cookbook Scheduled Cook'

function Get-SchedulerSentinelClassificationLocal {
    # Local mirror of the broker's Get-SchedulerSentinelClassification.
    # Kept structurally identical so smoke_ai_c9_1.ps1 can pin the
    # shape across both surfaces. NO side effects.
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

function Invoke-RegisterScheduledCook {
    param(
        [string]$WorkspacePath,
        [string]$LauncherPath
    )

    $workspaceFull = [System.IO.Path]::GetFullPath($WorkspacePath)
    if (-not (Test-Path -LiteralPath $workspaceFull -PathType Container)) {
        Write-Host ('Workspace path does not exist: ' + $workspaceFull) -ForegroundColor Red
        exit 2
    }

    $sentinelPath = Join-Path -Path $workspaceFull -ChildPath 'scheduler.json'
    $classification = Get-SchedulerSentinelClassificationLocal -Path $sentinelPath
    if ($classification -ne 'scheduler_detected') {
        Write-Host ('Refusing to register: scheduler.json classifies as ' + $classification + ' at ' + $sentinelPath) -ForegroundColor Red
        Write-Host 'The strict opt-in shape is exactly: { "enabled": true, "recipe_id": "<26-char ULID>", "daily_time": "HH:mm" }' -ForegroundColor Yellow
        exit 3
    }

    # M3.1: daily_time is now a sentinel property. Read it back
    # through the same parse path the classifier just validated.
    $sentinel = Get-Content -LiteralPath $sentinelPath -Raw | ConvertFrom-Json
    $DailyTime = [string]$sentinel.daily_time

    # Resolve launcher path (default: <repoRoot>\launcher\Invoke-ScheduledCook.ps1).
    if (-not $LauncherPath -or $LauncherPath.Length -eq 0) {
        $installDir = Split-Path -Parent $PSCommandPath
        $appDir     = Split-Path -Parent $installDir
        $repoRoot   = Split-Path -Parent $appDir
        $LauncherPath = Join-Path -Path $repoRoot -ChildPath (Join-Path 'launcher' 'Invoke-ScheduledCook.ps1')
    }
    if (-not (Test-Path -LiteralPath $LauncherPath -PathType Leaf)) {
        Write-Host ('Launcher script not found: ' + $LauncherPath) -ForegroundColor Red
        exit 4
    }

    # Resolve pwsh.exe path (must be the same major+minor as the broker requires).
    $pwsh = (Get-Process -Id $PID).Path
    if (-not $pwsh -or -not (Test-Path -LiteralPath $pwsh -PathType Leaf)) {
        $cmd = Get-Command pwsh.exe -ErrorAction SilentlyContinue
        if ($cmd) { $pwsh = $cmd.Source }
    }
    if (-not $pwsh -or -not (Test-Path -LiteralPath $pwsh -PathType Leaf)) {
        Write-Host 'Cannot locate pwsh.exe.' -ForegroundColor Red
        exit 5
    }

    # Parse DailyTime (HH:mm 24h).
    $parsedTime = $null
    if (-not [DateTime]::TryParseExact($DailyTime, 'HH:mm', [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$parsedTime)) {
        Write-Host ('Invalid DailyTime format: ' + $DailyTime + ' (expected HH:mm)') -ForegroundColor Red
        exit 6
    }

    $argList = @(
        '-NoProfile',
        '-NoLogo',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"' + $LauncherPath + '"'),
        '-WorkspacePath', ('"' + $workspaceFull + '"')
    ) -join ' '

    $action = New-ScheduledTaskAction -Execute $pwsh -Argument $argList
    $trigger = New-ScheduledTaskTrigger -Daily -At $parsedTime
    $principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive
    # StartWhenAvailable explicitly OFF -- no catch-up, no missed-run
    # compensation, no replay. If the user's machine is off at the
    # scheduled time, the cook simply does not run.
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable:$false -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew

    Register-ScheduledTask -TaskName $Script:TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    Write-Host ('Registered scheduled task "' + $Script:TaskName + '" daily at ' + $parsedTime.ToString('HH:mm') + ' UTC offset (local time).') -ForegroundColor Green
    Write-Host ('  Workspace : ' + $workspaceFull)
    Write-Host ('  Launcher  : ' + $LauncherPath)
    Write-Host ('  pwsh      : ' + $pwsh)
    exit 0
}

function Invoke-UnregisterScheduledCook {
    $existing = Get-ScheduledTask -TaskName $Script:TaskName -ErrorAction SilentlyContinue
    if (-not $existing) {
        Write-Host ('No scheduled task named "' + $Script:TaskName + '" registered.') -ForegroundColor Yellow
        exit 0
    }
    Unregister-ScheduledTask -TaskName $Script:TaskName -Confirm:$false
    Write-Host ('Unregistered scheduled task "' + $Script:TaskName + '".') -ForegroundColor Green
    exit 0
}

if ($Unregister.IsPresent) {
    Invoke-UnregisterScheduledCook
} else {
    Invoke-RegisterScheduledCook -WorkspacePath $WorkspacePath -LauncherPath $LauncherPath
}
