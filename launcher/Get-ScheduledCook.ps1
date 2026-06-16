#requires -Version 7.4

# =====================================================================
# Get-ScheduledCook.ps1
#
# Phase M3.2 -- read-only scheduled cook status.
#
# The single operator entry point for reading the configured
# scheduled cook state. Reads <Workspace>\scheduler.json, applies
# the same strict 3-property classifier the broker and AI.C9.1
# launcher use, and prints the result to stdout.
#
# What this script does (and ONLY what this script does):
#
#   1. Resolve <Workspace>\scheduler.json.
#   2. Classify it via the local 3-property mirror of
#      Get-SchedulerSentinelClassification:
#          - 'scheduler_absent'
#          - 'scheduler_malformed_optin'
#          - 'scheduler_detected'
#   3. Print the classification literal on the first stdout line.
#   4. If 'scheduler_detected', print 'recipe_id=<ULID>' and
#      'daily_time=HH:mm' on the next two lines.
#   5. Exit 0 in all three classification outcomes -- a missing
#      or malformed sentinel is information, not an operator
#      error. Exit non-zero only for operator misuse (bad
#      workspace path).
#
# Hard rules (enforced by smoke_m3_2.ps1):
#
#   - Read-only. No file writes. No file deletes. No registry
#     writes. No SQLite writes. No HTTP calls.
#   - No *-ScheduledTask cmdlet usage. The script does NOT
#     query Windows Task Scheduler; it reads the sentinel only.
#   - No retries / replay / catch-up / lifecycle tracking.
#   - No new broker API / route / table / /health key / exit
#     constant.
#
# Exit code map (small, operator-visible):
#
#    0  Success (one of the three classifications printed).
#    2  Workspace path does not exist or is not a directory.
# =====================================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$WorkspacePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SchedulerSentinelClassificationLocal {
    # Local mirror of the broker's Get-SchedulerSentinelClassification.
    # Kept structurally identical to the launcher / install mirrors
    # so smoke_ai_c9_1.ps1 + smoke_m3_2.ps1 pin shape parity. NO
    # side effects.
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
        if ($null -eq $obj) {
            return 'scheduler_malformed_optin'
        }
        $props = @($obj.PSObject.Properties | Where-Object { $_.MemberType -eq 'NoteProperty' })
        if ($props.Count -ne 3) {
            return 'scheduler_malformed_optin'
        }
        $names = @($props | ForEach-Object { $_.Name } | Sort-Object)
        if ($names[0] -ne 'daily_time' -or $names[1] -ne 'enabled' -or $names[2] -ne 'recipe_id') {
            return 'scheduler_malformed_optin'
        }
        $enabledVal = ($props | Where-Object { $_.Name -eq 'enabled' }).Value
        if ($enabledVal -isnot [bool]) {
            return 'scheduler_malformed_optin'
        }
        if ($enabledVal -ne $true) {
            return 'scheduler_malformed_optin'
        }
        $recipeVal = ($props | Where-Object { $_.Name -eq 'recipe_id' }).Value
        if ($recipeVal -isnot [string]) {
            return 'scheduler_malformed_optin'
        }
        if ($recipeVal -notmatch '^[0-9A-HJKMNP-TV-Z]{26}$') {
            return 'scheduler_malformed_optin'
        }
        $dailyVal = ($props | Where-Object { $_.Name -eq 'daily_time' }).Value
        if ($dailyVal -isnot [string]) {
            return 'scheduler_malformed_optin'
        }
        if ($dailyVal -notmatch '^([01][0-9]|2[0-3]):[0-5][0-9]$') {
            return 'scheduler_malformed_optin'
        }
        return 'scheduler_detected'
    } catch {
        return 'scheduler_malformed_optin'
    }
}

$workspaceFull = [System.IO.Path]::GetFullPath($WorkspacePath)
if (-not (Test-Path -LiteralPath $workspaceFull -PathType Container)) {
    Write-Host ('Workspace path does not exist: ' + $workspaceFull) -ForegroundColor Red
    exit 2
}

$sentinelPath = Join-Path -Path $workspaceFull -ChildPath 'scheduler.json'
$classification = Get-SchedulerSentinelClassificationLocal -Path $sentinelPath

Write-Output $classification

if ($classification -eq 'scheduler_detected') {
    # The classifier already validated shape, types, and regex
    # for both fields; the parse below cannot fail under any
    # input that reached 'scheduler_detected'.
    $sentinel = Get-Content -LiteralPath $sentinelPath -Raw | ConvertFrom-Json
    Write-Output ('recipe_id=' + [string]$sentinel.recipe_id)
    Write-Output ('daily_time=' + [string]$sentinel.daily_time)
}

exit 0
