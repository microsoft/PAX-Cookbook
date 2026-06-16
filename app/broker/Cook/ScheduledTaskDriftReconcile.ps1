# =====================================================================
# Cook\ScheduledTaskDriftReconcile.ps1
# =====================================================================
#
# Read-only OS-task drift comparison for the scheduled-task surface.
#
# The broker never queries Windows Task Scheduler directly. The only
# component permitted to call the *-ScheduledTask cmdlet family is the
# external helper app\install\Register-PAXScheduledRecipe.ps1. This
# reconciler reaches OS task state ONLY by child-spawning that helper
# with "-Action probe", reading the single JSON document it prints to
# stdout, and classifying the result against the cookbook database row.
#
# Everything here is read-only:
#   * no Task Scheduler state is created, changed, enabled, or removed
#   * no database row is written
#   * no repair or re-registration is attempted
#   * the probe child is timeout-guarded and every failure is swallowed
#     into a "probe_failed" verdict so the GET handler can never throw
#
# Public surface:
#   Invoke-ScheduledTaskDriftProbe   child-spawn the registrar probe
#   Compare-ScheduledTaskProbe       pure classifier (probe vs row)
#   New-ScheduledTaskOsDriftObject   orchestrator the GET handler calls
#
# Dot-sourced by Start-Broker.ps1 after Cook\ScheduledTaskReconcile.ps1
# and before Routes\ScheduledTasks.ps1 so the per-recipe GET path can
# compose the additive "osDrift" block.

# ---------------------------------------------------------------------
# Action comparison (loose -- token presence, not byte equality)
# ---------------------------------------------------------------------
function Test-ScheduledTaskDriftActionMatch {
    # Returns $true when the probed task action still invokes the
    # cookbook wrapper for this recipe + scheduled-task id. Tolerates
    # quoting, argument order, and absolute-path differences; only the
    # essential tokens are required.
    param(
        $Action,
        [string]$RecipeId,
        [string]$ScheduledTaskId
    )
    if ($null -eq $Action) { return $false }

    $exec = ''
    try { if ($null -ne $Action.execute) { $exec = [string]$Action.execute } } catch { $exec = '' }
    $arguments = ''
    try { if ($null -ne $Action.arguments) { $arguments = [string]$Action.arguments } } catch { $arguments = '' }

    $execLeaf = $exec
    try { $execLeaf = [System.IO.Path]::GetFileName($exec) } catch { $execLeaf = $exec }
    $execOk = ($execLeaf -match '(?i)^pwsh(\.exe)?$' -or $execLeaf -match '(?i)^powershell(\.exe)?$')

    $hasWrapper = ($arguments -match '(?i)Invoke-PAXScheduledRecipe\.ps1')
    $hasRecipe  = $false
    $hasStid    = $false
    if (-not [string]::IsNullOrWhiteSpace($RecipeId)) {
        $hasRecipe = ($arguments -match ('(?i)-RecipeId\s+"?' + [regex]::Escape($RecipeId)))
    }
    if (-not [string]::IsNullOrWhiteSpace($ScheduledTaskId)) {
        $hasStid = ($arguments -match ('(?i)-ScheduledTaskId\s+"?' + [regex]::Escape($ScheduledTaskId)))
    }

    return ($execOk -and $hasWrapper -and $hasRecipe -and $hasStid)
}

# ---------------------------------------------------------------------
# Trigger comparison (against the stored registered recurrence)
# ---------------------------------------------------------------------
function Compare-ScheduledTaskDriftTrigger {
    # Returns 'match' | 'divergent' | 'unknown'.
    #
    #   * No stored recurrence (legacy rows registered before the
    #     registered_recurrence_json column existed)  -> 'unknown'
    #   * Probe could not classify the trigger (kind 'other')          -> 'unknown'
    #   * kind / hour / minute differ, or weekly day-set differs       -> 'divergent'
    #   * everything compared matches                                  -> 'match'
    #
    # Weekly day-of-week comparison is order-insensitive (both sides
    # are broker-space ints 0=Sun..6=Sat).
    param(
        $ProbeTrigger,
        [string]$RegisteredRecurrenceJson
    )
    if ([string]::IsNullOrWhiteSpace($RegisteredRecurrenceJson)) { return 'unknown' }
    if ($null -eq $ProbeTrigger) { return 'unknown' }

    $registered = $null
    try { $registered = $RegisteredRecurrenceJson | ConvertFrom-Json -Depth 6 } catch { $registered = $null }
    if ($null -eq $registered) { return 'unknown' }

    $probeKind = ''
    try { if ($null -ne $ProbeTrigger.kind) { $probeKind = [string]$ProbeTrigger.kind } } catch { $probeKind = '' }
    if ([string]::IsNullOrWhiteSpace($probeKind) -or $probeKind -eq 'other') { return 'unknown' }

    $registeredKind = ''
    try { if ($null -ne $registered.kind) { $registeredKind = [string]$registered.kind } } catch { $registeredKind = '' }
    if ($registeredKind -ne $probeKind) { return 'divergent' }

    $registeredHour = $null; $registeredMinute = $null
    $probeHour = $null;      $probeMinute = $null
    try { if ($null -ne $registered.hour) { $registeredHour = [int]$registered.hour } } catch { $registeredHour = $null }
    try { if ($null -ne $registered.minute) { $registeredMinute = [int]$registered.minute } } catch { $registeredMinute = $null }
    try { if ($null -ne $ProbeTrigger.hour) { $probeHour = [int]$ProbeTrigger.hour } } catch { $probeHour = $null }
    try { if ($null -ne $ProbeTrigger.minute) { $probeMinute = [int]$ProbeTrigger.minute } } catch { $probeMinute = $null }

    if ($registeredHour -ne $probeHour) { return 'divergent' }
    if ($registeredMinute -ne $probeMinute) { return 'divergent' }

    if ($registeredKind -eq 'weekly') {
        $registeredDays = @()
        $probeDays = @()
        try { $registeredDays = @($registered.daysOfWeek | ForEach-Object { [int]$_ } | Sort-Object) } catch { $registeredDays = @() }
        try { $probeDays = @($ProbeTrigger.daysOfWeek | ForEach-Object { [int]$_ } | Sort-Object) } catch { $probeDays = @() }
        if ($registeredDays.Count -ne $probeDays.Count) { return 'divergent' }
        for ($i = 0; $i -lt $registeredDays.Count; $i++) {
            if ($registeredDays[$i] -ne $probeDays[$i]) { return 'divergent' }
        }
    }

    return 'match'
}

# ---------------------------------------------------------------------
# Pure classifier -- parsed probe + DB row -> drift verdict
# ---------------------------------------------------------------------
function Compare-ScheduledTaskProbe {
    # Classifies a single recipe's OS task state against its database
    # row. Never performs IO. Priority (first match wins):
    #   1 probe_failed   2 row_without_task   3 action_divergent
    #   4 trigger_divergent   5 disabled_in_os   6 in_sync   7 unknown
    param(
        $Probe,
        [string]$ProbeError,
        [string]$ScheduledTaskId,
        [string]$RecipeId,
        [string]$RegisteredRecurrenceJson
    )

    $result = [ordered]@{
        status         = 'unknown'
        taskExists     = $null
        enabledInOs    = $null
        actionMatches  = $null
        triggerMatch   = 'unknown'
        lastRunTime    = $null
        nextRunTime    = $null
        lastTaskResult = $null
        probeError     = $null
        message        = $null
    }

    # 1. probe_failed -- spawn / parse / timeout error, or no probe doc
    if (-not [string]::IsNullOrWhiteSpace($ProbeError) -or $null -eq $Probe) {
        $result.status = 'probe_failed'
        $detail = if ([string]::IsNullOrWhiteSpace($ProbeError)) { 'probe_unavailable' } else { $ProbeError }
        $result.probeError = $detail
        $result.message = 'Could not read the Windows task state to compare against the registered schedule.'
        return $result
    }

    # probeError surfaced INSIDE the probe JSON (OS query error, exit 0)
    $innerError = $null
    try { if ($null -ne $Probe.probeError) { $innerError = [string]$Probe.probeError } } catch { $innerError = $null }
    if (-not [string]::IsNullOrWhiteSpace($innerError)) {
        $result.status = 'probe_failed'
        $result.probeError = $innerError
        $result.message = 'The OS task query reported an error while reading the Windows task state.'
        return $result
    }

    $exists = $false
    try { $exists = [bool]$Probe.exists } catch { $exists = $false }
    $result.taskExists = $exists

    # observability carried regardless of the final class
    try { $result.lastRunTime = $Probe.lastRunTime } catch { $result.lastRunTime = $null }
    try { $result.nextRunTime = $Probe.nextRunTime } catch { $result.nextRunTime = $null }
    try { $result.lastTaskResult = $Probe.lastTaskResult } catch { $result.lastTaskResult = $null }

    # 2. row_without_task -- DB row present, no Windows task
    if (-not $exists) {
        $result.status = 'row_without_task'
        $result.enabledInOs = $false
        $result.actionMatches = $false
        $result.triggerMatch = 'unknown'
        $result.message = 'A schedule is registered in the cookbook but the matching Windows task is missing. Re-save the schedule to re-register it.'
        return $result
    }

    # task exists -- gather dimensions
    $enabled = $null
    try { if ($null -ne $Probe.enabled) { $enabled = [bool]$Probe.enabled } } catch { $enabled = $null }
    $result.enabledInOs = $enabled

    $action = $null
    try { $action = $Probe.action } catch { $action = $null }
    $actionMatches = Test-ScheduledTaskDriftActionMatch -Action $action -RecipeId $RecipeId -ScheduledTaskId $ScheduledTaskId
    $result.actionMatches = $actionMatches

    $trigger = $null
    try { $trigger = $Probe.trigger } catch { $trigger = $null }
    $triggerMatch = Compare-ScheduledTaskDriftTrigger -ProbeTrigger $trigger -RegisteredRecurrenceJson $RegisteredRecurrenceJson
    $result.triggerMatch = $triggerMatch

    # 3. action_divergent
    if (-not $actionMatches) {
        $result.status = 'action_divergent'
        $result.message = 'The Windows task exists but its action no longer matches the cookbook wrapper invocation. Re-save the schedule to repair it.'
        return $result
    }

    # 4. trigger_divergent
    if ($triggerMatch -eq 'divergent') {
        $result.status = 'trigger_divergent'
        $result.message = 'The Windows task schedule differs from the registered recurrence. Re-save the schedule to realign it.'
        return $result
    }

    # 5. disabled_in_os -- explicit false only
    if ($enabled -eq $false) {
        $result.status = 'disabled_in_os'
        $result.message = 'The Windows task is present but disabled in Task Scheduler. Enable it in Windows, or re-save the schedule.'
        return $result
    }

    # 6. in_sync -- enabled, action matches, trigger matches or is unknown
    if ($enabled -eq $true) {
        $result.status = 'in_sync'
        if ($triggerMatch -eq 'unknown') {
            $result.message = 'The Windows task is present, enabled, and its action matches; the registered recurrence is unknown so the trigger was not compared.'
        } else {
            $result.message = 'The Windows task is present, enabled, and matches the registered schedule.'
        }
        return $result
    }

    # 7. unknown -- enabled state indeterminate
    $result.status = 'unknown'
    $result.message = 'The Windows task is present but its enabled state could not be determined.'
    return $result
}

# ---------------------------------------------------------------------
# Child-spawn the registrar probe (timeout-guarded, never throws)
# ---------------------------------------------------------------------
function Invoke-ScheduledTaskDriftProbe {
    # Spawns app\install\Register-PAXScheduledRecipe.ps1 -Action probe
    # -RecipeId <id> in a child pwsh, captures stdout/stderr, enforces a
    # timeout, and parses the single JSON document. Returns a structured
    # result and never throws and never calls *-ScheduledTask cmdlets:
    #
    #   @{ ran; exitCode; timedOut; probe; error; durationMs; stdout; stderr }
    #
    # On any failure (spawn error, nonzero exit, unparseable JSON,
    # timeout) `error` is populated and `probe` is $null; the caller
    # maps that to a probe_failed verdict.
    param(
        [Parameter(Mandatory)][string]$RecipeId,
        [string]$RegistrarPath = '',
        [string]$StagingDir = '',
        [int]$TimeoutMs = 8000
    )

    $result = @{
        ran        = $false
        exitCode   = $null
        timedOut   = $false
        probe      = $null
        error      = $null
        durationMs = 0
        stdout     = ''
        stderr     = ''
    }

    # Resolve the registrar (default from broker $Script:AppRoot)
    if ([string]::IsNullOrWhiteSpace($RegistrarPath)) {
        $appRoot = ''
        try { if ($null -ne $Script:AppRoot) { $appRoot = [string]$Script:AppRoot } } catch { $appRoot = '' }
        if (-not [string]::IsNullOrWhiteSpace($appRoot)) {
            $RegistrarPath = Join-Path $appRoot 'install\Register-PAXScheduledRecipe.ps1'
        }
    }
    if ([string]::IsNullOrWhiteSpace($RegistrarPath) -or -not (Test-Path -LiteralPath $RegistrarPath -PathType Leaf)) {
        $result.error = 'registrar_not_found'
        return $result
    }

    # Resolve the staging dir (default from broker $Script:WorkspacePath)
    if ([string]::IsNullOrWhiteSpace($StagingDir)) {
        $workspace = ''
        try { if ($null -ne $Script:WorkspacePath) { $workspace = [string]$Script:WorkspacePath } } catch { $workspace = '' }
        if (-not [string]::IsNullOrWhiteSpace($workspace)) {
            $StagingDir = Join-Path $workspace '_tmp\scheduler'
        } else {
            $StagingDir = Join-Path ([System.IO.Path]::GetTempPath()) 'pax_drift_probe'
        }
    }
    if (-not (Test-Path -LiteralPath $StagingDir -PathType Container)) {
        try { [void](New-Item -ItemType Directory -Path $StagingDir -Force -ErrorAction Stop) } catch { }
    }

    $stamp   = (Get-Date -Format 'yyyyMMdd_HHmmss_fff')
    $outPath = Join-Path $StagingDir ('drift_probe_' + $stamp + '.out.log')
    $errPath = Join-Path $StagingDir ('drift_probe_' + $stamp + '.err.log')

    $argList = New-Object System.Collections.Generic.List[string]
    foreach ($a in @(
        '-NoProfile', '-NoLogo', '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', $RegistrarPath,
        '-Action', 'probe',
        '-RecipeId', $RecipeId
    )) { $argList.Add($a) | Out-Null }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = $null
    $stdout = ''
    $stderr = ''
    try {
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = 'pwsh'
        foreach ($a in $argList) { [void]$psi.ArgumentList.Add($a) }
        $psi.UseShellExecute        = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError  = $true
        $psi.CreateNoWindow         = $true

        $proc = [System.Diagnostics.Process]::new()
        $proc.StartInfo = $psi
        [void]$proc.Start()

        # Drain both streams asynchronously to avoid a full-buffer deadlock.
        $outReader = $proc.StandardOutput.ReadToEndAsync()
        $errReader = $proc.StandardError.ReadToEndAsync()

        $exited = $proc.WaitForExit($TimeoutMs)
        if (-not $exited) {
            $result.timedOut = $true
            try { $proc.Kill($true) } catch { try { $proc.Kill() } catch { } }
            try { [void]$proc.WaitForExit(2000) } catch { }
            $result.error = ('probe_timeout_after_' + $TimeoutMs + 'ms')
        } else {
            $result.exitCode = [int]$proc.ExitCode
        }

        try { $stdout = $outReader.GetAwaiter().GetResult() } catch { $stdout = '' }
        try { $stderr = $errReader.GetAwaiter().GetResult() } catch { $stderr = '' }
    } catch {
        $result.error = ('probe_spawn_failed: ' + $_.Exception.Message)
    } finally {
        $stopwatch.Stop()
        $result.durationMs = [int]$stopwatch.Elapsed.TotalMilliseconds
        if ($null -ne $proc) { try { $proc.Dispose() } catch { } }
    }

    $result.stdout = $stdout
    $result.stderr = $stderr

    # Persist transcripts for post-hoc diagnostics; retain the recent set.
    try { [System.IO.File]::WriteAllText($outPath, $stdout, [System.Text.UTF8Encoding]::new($false)) } catch { }
    try { [System.IO.File]::WriteAllText($errPath, $stderr, [System.Text.UTF8Encoding]::new($false)) } catch { }
    try {
        $kept = Get-ChildItem -LiteralPath $StagingDir -File -Filter 'drift_probe_*' -ErrorAction SilentlyContinue |
            Sort-Object -Property LastWriteTime -Descending
        if ($kept -and $kept.Count -gt 64) {
            foreach ($old in ($kept | Select-Object -Skip 64)) {
                try { Remove-Item -LiteralPath $old.FullName -Force -ErrorAction SilentlyContinue } catch { }
            }
        }
    } catch { }

    if ($result.timedOut) { return $result }
    if (-not [string]::IsNullOrWhiteSpace($result.error)) { return $result }

    if ($null -ne $result.exitCode -and $result.exitCode -ne 0) {
        $tail = ''
        if (-not [string]::IsNullOrWhiteSpace($stderr)) { $tail = ': ' + $stderr.Trim() }
        $result.error = ('probe_exit_' + $result.exitCode + $tail)
        return $result
    }

    if ([string]::IsNullOrWhiteSpace($stdout)) {
        $result.error = 'probe_empty_stdout'
        return $result
    }

    $doc = $null
    try { $doc = $stdout | ConvertFrom-Json -Depth 12 } catch { $doc = $null }
    if ($null -eq $doc) {
        $result.error = 'probe_unparseable_json'
        return $result
    }

    $result.probe = $doc
    $result.ran   = $true
    return $result
}

# ---------------------------------------------------------------------
# Orchestrator -- the GET handler's single entry point
# ---------------------------------------------------------------------
function New-ScheduledTaskOsDriftObject {
    # Builds the additive "osDrift" block for GET
    # /api/v1/recipes/{id}/scheduled-task.
    #
    #   * $TaskRow $null  -> status 'not_applicable', NO probe spawn
    #   * otherwise       -> spawn the probe (or use an injected
    #                        $ProbeResult for tests), classify, assemble
    #
    # $ProbeResult, when supplied, is the structured hashtable returned
    # by Invoke-ScheduledTaskDriftProbe (or a synthetic equivalent with
    # `.probe` / `.error`); supplying it skips the child spawn entirely.
    param(
        $TaskRow,
        $ProbeResult = $null,
        [string]$RegistrarPath = '',
        [string]$StagingDir = '',
        [int]$TimeoutMs = 8000
    )

    $checkedAt = (Get-Date).ToUniversalTime().ToString('o')

    if ($null -eq $TaskRow) {
        return [ordered]@{
            status         = 'not_applicable'
            checked        = $false
            taskExists     = $false
            enabledInOs    = $null
            actionMatches  = $null
            triggerMatch   = 'not_applicable'
            lastRunTime    = $null
            nextRunTime    = $null
            lastTaskResult = $null
            probeError     = $null
            checkedAt      = $checkedAt
            message        = 'No scheduled task is registered for this recipe; OS drift does not apply.'
        }
    }

    $recipeId = ''
    $scheduledTaskId = ''
    $registeredRecurrenceJson = ''
    try { if ($null -ne $TaskRow.recipeId) { $recipeId = [string]$TaskRow.recipeId } } catch { $recipeId = '' }
    try { if ($null -ne $TaskRow.scheduledTaskId) { $scheduledTaskId = [string]$TaskRow.scheduledTaskId } } catch { $scheduledTaskId = '' }
    try { if ($null -ne $TaskRow.registeredRecurrenceJson) { $registeredRecurrenceJson = [string]$TaskRow.registeredRecurrenceJson } } catch { $registeredRecurrenceJson = '' }

    if ($null -eq $ProbeResult) {
        $ProbeResult = Invoke-ScheduledTaskDriftProbe `
            -RecipeId $recipeId `
            -RegistrarPath $RegistrarPath `
            -StagingDir $StagingDir `
            -TimeoutMs $TimeoutMs
    }

    $probeObj   = $null
    $probeError = $null
    try { $probeObj = $ProbeResult.probe } catch { $probeObj = $null }
    try { if ($null -ne $ProbeResult.error) { $probeError = [string]$ProbeResult.error } } catch { $probeError = $null }

    $comparison = Compare-ScheduledTaskProbe `
        -Probe $probeObj `
        -ProbeError $probeError `
        -ScheduledTaskId $scheduledTaskId `
        -RecipeId $recipeId `
        -RegisteredRecurrenceJson $registeredRecurrenceJson

    return [ordered]@{
        status         = $comparison.status
        checked        = $true
        taskExists     = $comparison.taskExists
        enabledInOs    = $comparison.enabledInOs
        actionMatches  = $comparison.actionMatches
        triggerMatch   = $comparison.triggerMatch
        lastRunTime    = $comparison.lastRunTime
        nextRunTime    = $comparison.nextRunTime
        lastTaskResult = $comparison.lastTaskResult
        probeError     = $comparison.probeError
        checkedAt      = $checkedAt
        message        = $comparison.message
    }
}
