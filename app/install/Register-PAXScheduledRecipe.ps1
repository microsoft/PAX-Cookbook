#requires -Version 7.4

# =====================================================================
# Register-PAXScheduledRecipe.ps1
#
# V1.S06c -- native Windows Task Scheduler registrar for the
# per-recipe scheduling surface.
#
# This script is the SOLE file in the entire codebase permitted to
# call *-ScheduledTask cmdlets (New-ScheduledTaskAction,
# New-ScheduledTaskTrigger, New-ScheduledTaskPrincipal,
# New-ScheduledTaskSettingsSet, Register-ScheduledTask,
# Get-ScheduledTask, Get-ScheduledTaskInfo, Unregister-ScheduledTask).
# The Phase AB harness contract scan refuses to pass if any other
# file imports ScheduledTasks or invokes a ScheduledTask cmdlet.
#
# It is invoked by the native C# runtime (RecipeScheduleModel, the
# PUT/DELETE/GET /api/v1/recipes/{id}/scheduled-task routes) via
# Start-Process pwsh -File <thisScript> -Action register|unregister
# -WorkspacePath ... -RecipeId ... -ScheduledTaskId ...
# -RecurrenceJson '<json>'  (register only). The frozen PowerShell
# oracle (app/broker/Routes/ScheduledTasks.ps1) shells it the same way.
# An optional -TaskFolderOverride redirects the task folder for smokes.
#
# The read-only -Action probe verb is also invoked from the broker to
# observe OS-side task state for drift reconciliation. Probe only
# calls Get-ScheduledTask / Get-ScheduledTaskInfo, never a mutating
# cmdlet, and writes a single JSON document to stdout with no
# [Registrar] prefix so the caller can parse it directly; all probe
# diagnostics go to stderr. With a recipeId it reports one task
# (single mode); without a recipeId it enumerates the \PAX Cookbook\
# folder (enumerate mode). Probe does not require -WorkspacePath.
#
# The argv NEVER carries the recipe's client secret. The registered task
# action launches the native runtime exe one-shot
# ("PAX Cookbook.exe --run-scheduled-recipe <id> --workspace <ws>
# --approot <app>"); the broker reads the bound Chef's Key from Windows
# Credential Manager at FIRE time and injects it onto the PAX child
# (CK-3 broker credential injection), under the same Windows account
# that scheduled the task. No secret is stored in the task definition.
#
# Doctrine (chef, V1.S06b):
#   - No retries. Register or unregister once and exit.
#   - StartWhenAvailable=false. No missed-run catch-up.
#   - MultipleInstances=IgnoreNew. One run at a time per task.
#   - Daily or weekly only. No cron, no monthly, no idle, no boot.
#   - Task path is fixed: \PAX Cookbook\
#   - Task name is fixed: PAXCookbook_<recipeId>
#
# Exit codes:
#   0   success
#   2   bad arguments (workspace/recipeId/scheduledTaskId)
#   3   native runtime exe (PAX Cookbook.exe) missing
#   4   recurrence JSON parse / validation failed
#   5   pwsh.exe not locatable (legacy code; unused by the exe action)
#   6   ScheduledTask cmdlet failure (Windows surfaces native HRESULT)
#  31   internal registrar error
#
# Probe (-Action probe) exit codes:
#   0   probe ran and wrote one JSON document to stdout. The task may
#       or may not exist; an OS query error is reported inside the
#       JSON probeError field, still with exit 0.
#   2   bad arguments (recipeId failed the ULID pattern)
#  31   internal error before any JSON could be emitted
# =====================================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('register','unregister','probe')]
    [string]$Action,

    [Parameter(Mandatory=$false)]
    [string]$WorkspacePath = '',

    [Parameter(Mandatory=$false)]
    [string]$RecipeId = '',

    [Parameter(Mandatory=$false)]
    [string]$ScheduledTaskId = '',

    [Parameter(Mandatory=$false)]
    [string]$RecurrenceJson = '',

    # Test-only override of the Windows Task Scheduler folder. A smoke can
    # target an isolated sandbox folder (e.g. '\PAX Cookbook Test\') so it
    # never creates, probes, or removes a task in the real \PAX Cookbook\
    # folder. Production callers OMIT this; the default \PAX Cookbook\
    # folder is used. Applies to register, unregister, and probe alike.
    [Parameter(Mandatory=$false)]
    [string]$TaskFolderOverride = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# RecipeId pattern (Crockford base32 ULID, case-insensitive). Mirrors
# the broker's $Script:RecipeIdPattern; kept literal here so this
# script can run without dot-sourcing broker code.
$Script:RecipeIdPattern        = '^[0-9A-HJKMNP-TV-Z]{26}$'
$Script:ScheduledTaskFolderPath = '\PAX Cookbook\'

# Apply the sandbox task-folder override (test-only). Normalized to a
# leading + trailing backslash so register/unregister/probe all address
# the same folder. Because every task operation reads
# $Script:ScheduledTaskFolderPath, setting it here is the single point of
# redirection and the production code paths stay byte-identical.
if (-not [string]::IsNullOrWhiteSpace($TaskFolderOverride)) {
    $tf = $TaskFolderOverride
    if (-not $tf.StartsWith('\')) { $tf = '\' + $tf }
    if (-not $tf.EndsWith('\'))   { $tf = $tf + '\' }
    $Script:ScheduledTaskFolderPath = $tf
}

function Write-RegistrarLine {
    # Stdout helper that prefixes every line with a stable tag so
    # the broker side can parse / surface registrar output without
    # confusing it with raw PowerShell traceback text.
    param([string]$Text, [string]$Stream = 'stdout')
    if ($Stream -eq 'stderr') {
        [Console]::Error.WriteLine('[Registrar] ' + $Text)
    } else {
        [Console]::Out.WriteLine('[Registrar] ' + $Text)
    }
}

function Get-WindowsTaskNameForRecipe {
    param([string]$RecipeIdArg)
    return 'PAXCookbook_' + $RecipeIdArg
}

function Resolve-PwshPath {
    # Use the SAME pwsh interpreter that launched this script
    # whenever possible (it is the one the broker chose). Fall
    # back to whatever pwsh.exe is on PATH; if neither resolves,
    # exit 5.
    $proc = Get-Process -Id $PID -ErrorAction SilentlyContinue
    if ($proc -and $proc.Path -and (Test-Path -LiteralPath $proc.Path -PathType Leaf)) {
        return $proc.Path
    }
    $cmd = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -and (Test-Path -LiteralPath $cmd.Source -PathType Leaf)) {
        return $cmd.Source
    }
    return $null
}

function Test-RecurrenceShape {
    # Parses + validates the recurrence JSON. Returns @{ ok=<bool>;
    # error=<msg or null>; recurrence=<hashtable or null> }. Mirrors
    # broker-side Test-ScheduledTaskPutBody so a registrar invoked
    # with a hand-crafted JSON (operator running this script
    # directly) gets the same refusal taxonomy as one invoked from
    # the route.
    param([string]$Json)
    if ([string]::IsNullOrWhiteSpace($Json)) {
        return @{ ok = $false; error = 'recurrence_json_empty'; recurrence = $null }
    }
    $obj = $null
    try {
        $obj = $Json | ConvertFrom-Json -AsHashtable -Depth 8 -ErrorAction Stop
    } catch {
        return @{ ok = $false; error = 'recurrence_json_unparseable: ' + $_.Exception.Message; recurrence = $null }
    }
    if ($null -eq $obj -or -not (($obj -is [hashtable]) -or ($obj -is [System.Collections.IDictionary]))) {
        return @{ ok = $false; error = 'recurrence_not_object'; recurrence = $null }
    }
    $kind = if ($obj.ContainsKey('kind')) { [string]$obj['kind'] } else { '' }
    if ($kind -notin @('daily','weekly')) {
        return @{ ok = $false; error = "recurrence_kind_invalid: '$kind' (expected daily|weekly)"; recurrence = $null }
    }
    $hour   = if ($obj.ContainsKey('hour'))   { try { [int]$obj['hour']   } catch { -1 } } else { -1 }
    $minute = if ($obj.ContainsKey('minute')) { try { [int]$obj['minute'] } catch { -1 } } else { -1 }
    if ($hour -lt 0 -or $hour -gt 23) {
        return @{ ok = $false; error = "recurrence_hour_out_of_range: $hour"; recurrence = $null }
    }
    if ($minute -lt 0 -or $minute -gt 59) {
        return @{ ok = $false; error = "recurrence_minute_out_of_range: $minute"; recurrence = $null }
    }
    $daysOfWeek = $null
    if ($kind -eq 'weekly') {
        if (-not $obj.ContainsKey('daysOfWeek')) {
            return @{ ok = $false; error = 'weekly_recurrence_missing_daysOfWeek'; recurrence = $null }
        }
        $raw = @($obj['daysOfWeek'])
        if ($raw.Count -lt 1 -or $raw.Count -gt 7) {
            return @{ ok = $false; error = "weekly_daysOfWeek_invalid_length: $($raw.Count)"; recurrence = $null }
        }
        $set = New-Object System.Collections.Generic.List[int]
        foreach ($d in $raw) {
            $di = -1
            try { $di = [int]$d } catch { $di = -1 }
            if ($di -lt 0 -or $di -gt 6) {
                return @{ ok = $false; error = "weekly_daysOfWeek_entry_out_of_range: $di"; recurrence = $null }
            }
            if (-not $set.Contains($di)) { [void]$set.Add($di) }
        }
        $daysOfWeek = @($set.ToArray() | Sort-Object)
    }
    $normalized = [ordered]@{ kind = $kind; hour = $hour; minute = $minute }
    if ($daysOfWeek) { $normalized['daysOfWeek'] = $daysOfWeek }
    return @{ ok = $true; error = $null; recurrence = $normalized }
}

function New-RecurrenceTrigger {
    # Builds the [Microsoft.Management.Infrastructure.CimInstance]
    # trigger object from the validated recurrence hashtable.
    # Daily   -> New-ScheduledTaskTrigger -Daily -At <DateTime>
    # Weekly  -> New-ScheduledTaskTrigger -Weekly -DaysOfWeek <enum[]> -At <DateTime>
    param([hashtable]$Recurrence)

    # Choose a base date that has the requested HH:mm in local time.
    # The OS uses the time-of-day component to fire daily/weekly; the
    # date component (today's date) is only the "first occurrence"
    # anchor. Using today + the requested H/M gives the operator the
    # natural "fire next at HH:mm local" semantics.
    $now  = Get-Date
    $when = Get-Date -Year $now.Year -Month $now.Month -Day $now.Day -Hour $Recurrence.hour -Minute $Recurrence.minute -Second 0

    if ($Recurrence.kind -eq 'daily') {
        return (New-ScheduledTaskTrigger -Daily -At $when)
    }
    # Weekly: translate int days-of-week (0=Sunday..6=Saturday) into
    # the [System.DayOfWeek] enum array the cmdlet expects.
    $dows = New-Object System.Collections.Generic.List[System.DayOfWeek]
    foreach ($i in $Recurrence.daysOfWeek) {
        [void]$dows.Add([System.DayOfWeek]$i)
    }
    return (New-ScheduledTaskTrigger -Weekly -DaysOfWeek $dows.ToArray() -At $when)
}

function Resolve-LauncherPath {
    # Defaults to <repoRoot>/launcher/Invoke-PAXScheduledRecipe.ps1
    # relative to this script's install-tree location. The repoRoot
    # is two levels up from this file: app/install/ -> app/ -> root.
    #
    # SUPERSEDED: the OS task action no longer runs this launcher
    # wrapper. Register now points the action directly at the native
    # runtime exe one-shot (Resolve-ExePath). This function is left in
    # place (dormant) for provenance; it is not called by Invoke-Register.
    $installDir = Split-Path -Parent $PSCommandPath
    $appDir     = Split-Path -Parent $installDir
    $repoRoot   = Split-Path -Parent $appDir
    return (Join-Path -Path $repoRoot -ChildPath (Join-Path 'launcher' 'Invoke-PAXScheduledRecipe.ps1'))
}

function Resolve-ExePath {
    # Resolves the native runtime executable (PAX Cookbook.exe) relative
    # to this script's install-tree location. This script lives at
    # <App>\install\Register-PAXScheduledRecipe.ps1 and the exe lives at
    # <App>\bin\PAX Cookbook.exe, so:
    #   installDir = <App>\install   (parent of this file)
    #   appDir     = <App>           (one level up)
    #   exe        = <App>\bin\PAX Cookbook.exe
    # The registered task action targets this exe with the headless
    # one-shot flag (--run-scheduled-recipe); the exe reads the bound
    # Chef's Key from Windows Credential Manager at FIRE time (CK-3), so
    # the task argv carries NO secret.
    $installDir = Split-Path -Parent $PSCommandPath
    $appDir     = Split-Path -Parent $installDir
    return (Join-Path -Path $appDir -ChildPath (Join-Path 'bin' 'PAX Cookbook.exe'))
}

function Resolve-DllPath {
    # The managed entry assembly dotnet.exe runs at fire time:
    #   <App>\bin\PAX Cookbook.dll
    $installDir = Split-Path -Parent $PSCommandPath
    $appDir     = Split-Path -Parent $installDir
    return (Join-Path -Path $appDir -ChildPath (Join-Path 'bin' 'PAX Cookbook.dll'))
}

function Resolve-VbsPath {
    # The hidden launcher shipped next to the app DLL:
    #   <App>\bin\launch.vbs
    $installDir = Split-Path -Parent $PSCommandPath
    $appDir     = Split-Path -Parent $installDir
    return (Join-Path -Path $appDir -ChildPath (Join-Path 'bin' 'launch.vbs'))
}

function Resolve-WScriptPath {
    # Microsoft-signed Windows Script Host (windowed/no-console). Runs launch.vbs
    # hidden so dotnet.exe's console window never appears when a bake fires.
    $p = Join-Path -Path $env:SystemRoot -ChildPath (Join-Path 'System32' 'wscript.exe')
    if (Test-Path -LiteralPath $p -PathType Leaf) { return $p }
    $cmd = Get-Command wscript.exe -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { return $cmd.Source }
    return 'wscript.exe'
}

function Invoke-Register {
    param(
        [string]$WorkspaceFull,
        [string]$RecipeIdArg,
        [string]$ScheduledTaskIdArg,
        [string]$RecurrenceJsonArg
    )

    if ($RecipeIdArg -notmatch $Script:RecipeIdPattern) {
        Write-RegistrarLine ('invalid_recipe_id: ' + $RecipeIdArg) 'stderr'
        exit 2
    }
    if ($ScheduledTaskIdArg -notmatch $Script:RecipeIdPattern) {
        Write-RegistrarLine ('invalid_scheduled_task_id: ' + $ScheduledTaskIdArg) 'stderr'
        exit 2
    }

    $verdict = Test-RecurrenceShape -Json $RecurrenceJsonArg
    if (-not $verdict.ok) {
        Write-RegistrarLine ('recurrence_invalid: ' + $verdict.error) 'stderr'
        exit 4
    }
    $rec = $verdict.recurrence

    $dllPath = Resolve-DllPath
    if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
        Write-RegistrarLine ('dll_missing: ' + $dllPath) 'stderr'
        exit 3
    }
    $vbsPath = Resolve-VbsPath
    if (-not (Test-Path -LiteralPath $vbsPath -PathType Leaf)) {
        Write-RegistrarLine ('launcher_missing: ' + $vbsPath) 'stderr'
        exit 3
    }
    $wscriptPath = Resolve-WScriptPath
    $binDir = Split-Path -Parent $dllPath
    $appDir = Split-Path -Parent $binDir

    # Build the OS-side action. New-ScheduledTaskAction accepts a SINGLE
    # -Argument string. Under corporate WDAC the unsigned apphost cannot be
    # executed AND dotnet.exe is a console app (it would flash a terminal when a
    # bake fires), so the task runs the Microsoft-signed wscript.exe on the
    # shipped launch.vbs, which starts dotnet hidden. The "--pax-wait" sentinel
    # makes the launcher WAIT for the cook and return its exit code, so the
    # task's run result reflects the bake and the MultipleInstances=IgnoreNew
    # overlap policy still works. At fire time:
    #   wscript.exe "<App>\bin\launch.vbs" --pax-wait --run-scheduled-recipe <id>
    #               --workspace <ws> --approot <app>
    # runs exactly ONE scheduled cook through the same cook pipeline a manual
    # bake uses (constraint 8). Paths that may contain spaces (the VBS,
    # workspace, app root) are double-quoted; the RecipeId is a ULID with no
    # spaces or quotes. The argv carries NO secret -- the bound Chef's Key is
    # read from Windows Credential Manager at fire time.
    $argString = '"' + $vbsPath + '" --pax-wait --run-scheduled-recipe ' + $RecipeIdArg + ' --workspace "' + $WorkspaceFull + '" --approot "' + $appDir + '"'

    $taskName = Get-WindowsTaskNameForRecipe -RecipeIdArg $RecipeIdArg

    try {
        $action = New-ScheduledTaskAction -Execute $wscriptPath -Argument $argString -WorkingDirectory $binDir
        $trigger = New-RecurrenceTrigger -Recurrence $rec
        $principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive
        # Doctrine knobs:
        #   StartWhenAvailable = $false   -- no catch-up
        #   AllowStartIfOnBatteries / DontStopIfGoingOnBatteries: a
        #       laptop user expects the task to run regardless of AC.
        #   MultipleInstances = IgnoreNew -- if a previous wrapper is
        #       still running when the next trigger fires, drop the new
        #       fire (the wrapper enforces its own single-instance
        #       lock too, but defense-in-depth).
        #   ExecutionTimeLimit: PT0S means "no time limit" -- PAX cooks
        #       can legitimately run for hours and we do not want the
        #       OS to terminate them.
        $settings = New-ScheduledTaskSettingsSet `
                        -StartWhenAvailable:$false `
                        -AllowStartIfOnBatteries `
                        -DontStopIfGoingOnBatteries `
                        -MultipleInstances IgnoreNew `
                        -ExecutionTimeLimit ([TimeSpan]::FromHours(0))

        # The TaskPath argument creates the \PAX Cookbook\ folder
        # automatically the first time. -Force replaces the task
        # in place if one already exists with the same name +
        # path (the broker's PUT flow relies on this for updates).
        [void](Register-ScheduledTask -TaskName $taskName -TaskPath $Script:ScheduledTaskFolderPath -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force)
    } catch {
        Write-RegistrarLine ('register_failed: ' + $_.Exception.Message) 'stderr'
        exit 6
    }

    Write-RegistrarLine ('registered: ' + $Script:ScheduledTaskFolderPath + $taskName)
    Write-RegistrarLine ('  recipeId        : ' + $RecipeIdArg)
    Write-RegistrarLine ('  scheduledTaskId : ' + $ScheduledTaskIdArg)
    Write-RegistrarLine ('  recurrence      : ' + ($rec | ConvertTo-Json -Compress -Depth 5))
    Write-RegistrarLine ('  exe             : ' + $exePath)
    Write-RegistrarLine ('  arguments       : ' + $argString)
    exit 0
}

function Invoke-Unregister {
    param(
        [string]$RecipeIdArg
    )
    if ($RecipeIdArg -notmatch $Script:RecipeIdPattern) {
        Write-RegistrarLine ('invalid_recipe_id: ' + $RecipeIdArg) 'stderr'
        exit 2
    }
    $taskName = Get-WindowsTaskNameForRecipe -RecipeIdArg $RecipeIdArg
    $existing = $null
    try {
        $existing = Get-ScheduledTask -TaskPath $Script:ScheduledTaskFolderPath -TaskName $taskName -ErrorAction SilentlyContinue
    } catch { $existing = $null }
    if (-not $existing) {
        Write-RegistrarLine ('no_task_present: ' + $Script:ScheduledTaskFolderPath + $taskName)
        exit 0
    }
    try {
        Unregister-ScheduledTask -TaskPath $Script:ScheduledTaskFolderPath -TaskName $taskName -Confirm:$false
    } catch {
        Write-RegistrarLine ('unregister_failed: ' + $_.Exception.Message) 'stderr'
        exit 6
    }
    Write-RegistrarLine ('unregistered: ' + $Script:ScheduledTaskFolderPath + $taskName)
    exit 0
}

# ---------------------------------------------------------------------
# Probe (read-only)
# ---------------------------------------------------------------------

function Write-ProbeJson {
    # Probe stdout contract: ONE compact JSON document, no [Registrar]
    # prefix, so the broker can parse it directly. All probe
    # diagnostics go to stderr via Write-RegistrarLine elsewhere.
    param($Doc)
    $json = $Doc | ConvertTo-Json -Depth 8 -Compress
    [Console]::Out.WriteLine($json)
}

function ConvertFrom-TaskDaysOfWeekBitmask {
    # Task Scheduler weekly DaysOfWeek is a bitmask: Sunday=1,
    # Monday=2, Tuesday=4, Wednesday=8, Thursday=16, Friday=32,
    # Saturday=64. The broker day vocabulary is 0=Sunday..6=Saturday.
    # Returns a sorted int[] in that 0..6 space.
    param([int]$Mask)
    $bitToDay = [ordered]@{ 1 = 0; 2 = 1; 4 = 2; 8 = 3; 16 = 4; 32 = 5; 64 = 6 }
    $out = New-Object System.Collections.Generic.List[int]
    foreach ($bit in @(1, 2, 4, 8, 16, 32, 64)) {
        if (($Mask -band $bit) -eq $bit) { [void]$out.Add([int]$bitToDay[$bit]) }
    }
    return @($out.ToArray() | Sort-Object)
}

function Format-ProbeDate {
    # Returns an ISO-8601 round-trip ('o') UTC string, or $null when
    # the value is absent or the Task Scheduler "never" sentinel (a
    # year far in the past). Never throws.
    param($Value)
    if ($null -eq $Value) { return $null }
    $dt = [datetime]::MinValue
    $ok = $false
    try { $dt = [datetime]$Value; $ok = $true } catch { $ok = $false }
    if (-not $ok) { return $null }
    if ($dt.Year -lt 2000) { return $null }
    return $dt.ToUniversalTime().ToString('o')
}

function ConvertTo-ProbeTrigger {
    # Classifies the first trigger as daily|weekly|other and reads its
    # time of day. If the kind is daily/weekly but the time cannot be
    # resolved the read is uncertain, so it is downgraded to 'other'
    # (the broker treats 'other' as 'unknown' rather than raising a
    # false trigger-divergence).
    param([object[]]$Triggers)
    $out = [ordered]@{ kind = 'other'; hour = $null; minute = $null; daysOfWeek = $null }
    if (-not $Triggers -or $Triggers.Count -lt 1) { return $out }
    $trig = $Triggers[0]
    if ($null -eq $trig) { return $out }

    $className = $null
    try { $className = [string]$trig.CimClass.CimClassName } catch { $className = $null }

    $hour = $null
    $minute = $null
    try {
        $sb = [string]$trig.StartBoundary
        if (-not [string]::IsNullOrWhiteSpace($sb)) {
            $dt = [datetime]::MinValue
            if ([datetime]::TryParse($sb, [ref]$dt)) {
                $hour = $dt.Hour
                $minute = $dt.Minute
            }
        }
    } catch { $hour = $null; $minute = $null }

    switch ($className) {
        'MSFT_TaskDailyTrigger' {
            $out.kind = 'daily'
            $out.hour = $hour
            $out.minute = $minute
        }
        'MSFT_TaskWeeklyTrigger' {
            $out.kind = 'weekly'
            $out.hour = $hour
            $out.minute = $minute
            $mask = 0
            try { $mask = [int]$trig.DaysOfWeek } catch { $mask = 0 }
            $out.daysOfWeek = ConvertFrom-TaskDaysOfWeekBitmask -Mask $mask
        }
        default {
            $out.kind = 'other'
        }
    }

    if (($out.kind -eq 'daily' -or $out.kind -eq 'weekly') -and ($null -eq $out.hour)) {
        $out.kind = 'other'
        $out.hour = $null
        $out.minute = $null
        $out.daysOfWeek = $null
    }
    return $out
}

function Get-PaxCookbookTasks {
    # Read-only. Returns @{ ok=<bool>; error=<string|null>;
    # tasks=@(<CimInstance>) }. Fetches ALL scheduled tasks (no
    # -TaskPath filter, which throws if the \PAX Cookbook\ folder has
    # never been created) and filters to the PAX Cookbook folder
    # in-process. Never mutates.
    $all = $null
    try {
        $all = @(Get-ScheduledTask -ErrorAction Stop)
    } catch {
        return @{ ok = $false; error = ('query_failed: ' + $_.Exception.Message); tasks = @() }
    }
    $matched = @($all | Where-Object { ([string]$_.TaskPath) -eq $Script:ScheduledTaskFolderPath })
    return @{ ok = $true; error = $null; tasks = $matched }
}

function Get-ProbeTaskFields {
    # Builds the per-task observation block (state/enabled/action/
    # trigger/run-times). Read-only; every OS member access is guarded
    # so a malformed task never aborts the probe.
    param($Task)
    $f = [ordered]@{
        exists         = $true
        state          = $null
        enabled        = $null
        action         = $null
        trigger        = [ordered]@{ kind = 'other'; hour = $null; minute = $null; daysOfWeek = $null }
        lastRunTime    = $null
        nextRunTime    = $null
        lastTaskResult = $null
    }
    try { $f.state = [string]$Task.State } catch { $f.state = $null }
    try {
        if ($Task.Settings -and ($null -ne $Task.Settings.Enabled)) {
            $f.enabled = [bool]$Task.Settings.Enabled
        } elseif ($f.state) {
            $f.enabled = ($f.state -ne 'Disabled')
        }
    } catch { $f.enabled = $null }
    try {
        $exec = $null
        foreach ($a in @($Task.Actions)) {
            $hasExecute = $false
            try { $hasExecute = -not [string]::IsNullOrWhiteSpace([string]$a.Execute) } catch { $hasExecute = $false }
            if ($hasExecute) { $exec = $a; break }
        }
        if ($exec) {
            $f.action = [ordered]@{
                execute          = [string]$exec.Execute
                arguments        = [string]$exec.Arguments
                workingDirectory = [string]$exec.WorkingDirectory
            }
        }
    } catch { $f.action = $null }
    try { $f.trigger = ConvertTo-ProbeTrigger -Triggers @($Task.Triggers) } catch { }
    try {
        $info = Get-ScheduledTaskInfo -TaskName ([string]$Task.TaskName) -TaskPath ([string]$Task.TaskPath) -ErrorAction Stop
        if ($info) {
            $f.lastRunTime = Format-ProbeDate $info.LastRunTime
            $f.nextRunTime = Format-ProbeDate $info.NextRunTime
            try { $f.lastTaskResult = [int]$info.LastTaskResult } catch { $f.lastTaskResult = $null }
        }
    } catch { }
    return $f
}

function Invoke-ProbeSingle {
    param([string]$RecipeIdArg)
    $taskName = Get-WindowsTaskNameForRecipe -RecipeIdArg $RecipeIdArg
    $doc = [ordered]@{
        schemaVersion  = 1
        mode           = 'single'
        recipeId       = $RecipeIdArg
        taskName       = $taskName
        taskPath       = $Script:ScheduledTaskFolderPath
        exists         = $false
        state          = $null
        enabled        = $null
        action         = $null
        trigger        = [ordered]@{ kind = 'other'; hour = $null; minute = $null; daysOfWeek = $null }
        lastRunTime    = $null
        nextRunTime    = $null
        lastTaskResult = $null
        probedAt       = (Get-Date).ToUniversalTime().ToString('o')
        probeError     = $null
    }
    $q = Get-PaxCookbookTasks
    if (-not $q.ok) {
        $doc.probeError = $q.error
        Write-ProbeJson -Doc $doc
        exit 0
    }
    $task = @($q.tasks | Where-Object { ([string]$_.TaskName) -eq $taskName }) | Select-Object -First 1
    if ($task) {
        $f = Get-ProbeTaskFields -Task $task
        $doc.exists         = $f.exists
        $doc.state          = $f.state
        $doc.enabled        = $f.enabled
        $doc.action         = $f.action
        $doc.trigger        = $f.trigger
        $doc.lastRunTime    = $f.lastRunTime
        $doc.nextRunTime    = $f.nextRunTime
        $doc.lastTaskResult = $f.lastTaskResult
    }
    Write-ProbeJson -Doc $doc
    exit 0
}

function Invoke-ProbeEnumerate {
    $doc = [ordered]@{
        schemaVersion = 1
        mode          = 'enumerate'
        taskPath      = $Script:ScheduledTaskFolderPath
        tasks         = @()
        probedAt      = (Get-Date).ToUniversalTime().ToString('o')
        probeError    = $null
    }
    $q = Get-PaxCookbookTasks
    if (-not $q.ok) {
        $doc.probeError = $q.error
        Write-ProbeJson -Doc $doc
        exit 0
    }
    $prefix = 'PAXCookbook_'
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($t in $q.tasks) {
        $tn = [string]$t.TaskName
        $rid = if ($tn.StartsWith($prefix)) { $tn.Substring($prefix.Length) } else { $tn }
        $f = Get-ProbeTaskFields -Task $t
        $entry = [ordered]@{
            taskName       = $tn
            recipeId       = $rid
            exists         = $f.exists
            state          = $f.state
            enabled        = $f.enabled
            action         = $f.action
            trigger        = $f.trigger
            lastRunTime    = $f.lastRunTime
            nextRunTime    = $f.nextRunTime
            lastTaskResult = $f.lastTaskResult
        }
        [void]$list.Add($entry)
    }
    $doc.tasks = $list.ToArray()
    Write-ProbeJson -Doc $doc
    exit 0
}

function Invoke-Probe {
    param([string]$RecipeIdArg)
    if ([string]::IsNullOrWhiteSpace($RecipeIdArg)) {
        Invoke-ProbeEnumerate
        return
    }
    if ($RecipeIdArg -notmatch $Script:RecipeIdPattern) {
        Write-RegistrarLine ('invalid_recipe_id: ' + $RecipeIdArg) 'stderr'
        exit 2
    }
    Invoke-ProbeSingle -RecipeIdArg $RecipeIdArg
}

# ---------------------------------------------------------------------
# Entrypoint
# ---------------------------------------------------------------------

try {
    # Probe is a read-only OS-task query. It does not touch the
    # workspace, so it skips the workspace-path validation that
    # register/unregister require.
    if ($Action -eq 'probe') {
        Invoke-Probe -RecipeIdArg $RecipeId
    } else {
        if ([string]::IsNullOrWhiteSpace($WorkspacePath)) {
            Write-RegistrarLine 'workspace_path_empty' 'stderr'
            exit 2
        }
        $workspaceFull = $null
        try { $workspaceFull = [System.IO.Path]::GetFullPath($WorkspacePath) } catch { $workspaceFull = $null }
        if (-not $workspaceFull) {
            Write-RegistrarLine ('workspace_path_unresolvable: ' + $WorkspacePath) 'stderr'
            exit 2
        }
        if (-not (Test-Path -LiteralPath $workspaceFull -PathType Container)) {
            Write-RegistrarLine ('workspace_path_missing: ' + $workspaceFull) 'stderr'
            exit 2
        }

        switch ($Action) {
            'register'   { Invoke-Register   -WorkspaceFull $workspaceFull -RecipeIdArg $RecipeId -ScheduledTaskIdArg $ScheduledTaskId -RecurrenceJsonArg $RecurrenceJson }
            'unregister' { Invoke-Unregister -RecipeIdArg $RecipeId }
            default {
                Write-RegistrarLine ('unknown_action: ' + $Action) 'stderr'
                exit 2
            }
        }
    }
} catch {
    Write-RegistrarLine ('internal_error: ' + $_.Exception.Message) 'stderr'
    exit 31
}
