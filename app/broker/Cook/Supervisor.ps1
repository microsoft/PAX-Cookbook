#requires -Version 7.4

# Cook/Supervisor.ps1
#
# Single function exported to the dispatch thread:
#
#     Start-CookSupervisor -CookId -RecipeId -RecipeSnapshot
#                           -CommandExpr -CookFolder -PwshPath
#                           -PaxScriptPath -FactPath
#                           -SqliteConnectionString -HostName
#
# The supervisor is a single ThreadJob per active cook. It:
#
#   1. Spawns `pwsh -NoProfile -NoLogo -Command "<CommandExpr>"` as a
#      child process via System.Diagnostics.Process. The CommandExpr
#      value is built ONCE by the broker dispatch path via
#      Get-PaxInvocationPlan (Pax/Adapter.psm1) and passed in verbatim;
#      the supervisor MUST NOT reconstruct it, re-quote it, or splice
#      additional flags into it. The single source of authority for
#      the rendered command is the adapter. Stdin/stdout/stderr are
#      redirected. stdout+stderr are tee'd to <CookFolder>/cook.log AND
#      published to WebSocket subscribers via the shared
#      $Script:WsRegistry. Stderr lines are prefixed `[STDERR] ` in the
#      log only.
#
#   2. Writes started.json on successful spawn; finished.json on clean
#      exit; interrupted.json on cancel or spawn failure. Schemas match
#      what C5 reconciliation reads in Start-Broker.ps1's
#      Invoke-SentinelReconciliation / Set-CookFromFinishedJson.
#
#   3. Updates the cooks row (status / exit_code / finished_at /
#      duration_seconds / error_class) using its own SqliteConnection
#      (WAL allows multi-connection writes).
#
#   4. Inserts ONE cook_artifacts row at terminal time pointing at the
#      configured fact destination path. The row carries:
#
#         stream='fact', artifact_kind='file', tier='Local',
#         location=<FactPath>, size_bytes=<file size or NULL>,
#         row_count=NULL.
#
#      No inference, no scan, no glob. If the file doesn't exist (e.g.,
#      cook failed before producing output) size_bytes is NULL.
#
#   5. Publishes lifecycle frames on WS:  started / stdout / stderr /
#      finished / interrupted / error.  No other types.
#
# INTENTIONAL: the child PAX process is NOT attached to a Win32 job
# object, NOT given CREATE_BREAKAWAY_FROM_JOB, NOT bound to the broker
# via any process-group mechanism. If the broker dies mid-cook the child
# is required to keep running so C5 sentinel reconciliation on the next
# broker start can deterministically synthesize an interrupted sentinel
# and mark the row interrupted. Do NOT add job-object plumbing here.
#
# /stop  semantics: close child stdin -> WaitForExit(5000) -> Kill($true)
# /kill  semantics: Kill($true) immediately
# (Kill($true) terminates the child process tree; that's what /stop and
# /kill mean.  It does NOT touch any orphan children left by a prior
# broker crash — those are unreachable from this supervisor.)
#
# This file is dot-sourced from Start-Broker.ps1 and runs in the main
# runspace; the supervisor scriptblock runs in a ThreadJob runspace and
# inlines its own helpers (publish, sentinel writer, DB writer) because
# ThreadJob runspaces do NOT inherit dot-sourced functions from the
# parent.

$Script:CookSupervisorScript = {
    param(
        [string]$CookId,
        [string]$RecipeId,
        [string]$CommandExpr,
        [string]$CookFolder,
        [string]$PwshPath,
        [string]$PaxScriptPath,
        [string]$FactPath,
        [string]$ConnectionString,
        [string]$HostName,
        # Phase AF: per-cook environment-mode declaration. The
        # supervisor only runs cooks tagged as 'local-manual' or
        # 'local-scheduled' (passed in by the caller; defense-in-depth
        # alongside the Cooks.ps1 dispatch gate). 'fabric-hosted' and
        # 'azure-hosted' would be wrong by definition -- this
        # supervisor IS the local spawn path. Empty string defaults
        # to 'local-manual' for backward compat with pre-AF callers.
        [string]$ExecutionMode,
        # Phase AF: client secret for AppRegistrationSecret cooks.
        # Delivered as a SecureString reference (crosses runspace
        # boundaries because SecureString is a reference type); the
        # supervisor unwraps it into a transient plaintext string,
        # places it on the child process's EnvironmentVariables under
        # the well-known name GRAPH_CLIENT_SECRET, and zero-frees the
        # BSTR + clears the StringDictionary entry immediately after
        # $proc.Start() returns. $null for any other auth mode.
        [object]$ClientSecretSecure,
        [hashtable]$CookState,
        [hashtable]$WsRegistry,
        [hashtable]$CookRegistry,
        # Absolute path to the notification core (app/broker/Notify/
        # Notification.ps1) and the active broker workspace path. Both
        # are supplied by Start-CookSupervisor because the ThreadJob
        # runspace cannot see the main-runspace dot-source or the
        # $Script:WorkspacePath broker variable.
        [string]$NotifyModulePath,
        [string]$WorkspacePath
    )

    $ErrorActionPreference = 'Continue'

    # ---------------- Terminal notification helper (best-effort) ----------------
    #
    # ThreadJob runspaces do not inherit dot-sourced functions, so the
    # notification core is loaded once per cook from the same file the
    # main runspace uses. The load and every later dispatch are strictly
    # best-effort: a missing, unparseable, or failing helper must never
    # stop a bake from running or alter its terminal outcome. The core
    # sets Stop + StrictMode for its own body, so supervisor preferences
    # are re-asserted immediately after the dot-source.
    $notifyDispatchAvailable = $false
    try {
        if ($NotifyModulePath -and (Test-Path -LiteralPath $NotifyModulePath -PathType Leaf)) {
            . $NotifyModulePath
            if (Get-Command -Name Invoke-NotificationDispatch -ErrorAction SilentlyContinue) {
                $notifyDispatchAvailable = $true
            }
        }
    } catch {
        $notifyDispatchAvailable = $false
    }
    $ErrorActionPreference = 'Continue'
    Set-StrictMode -Off

    # Emits exactly one terminal notification for the current cook. The
    # recipe display name is not carried into the supervisor runspace, so
    # a stable privacy-safe fallback is used. Scope is manual bakes only:
    # the supervisor may also run 'local-scheduled' cooks, whose terminal
    # notification is a later slice, so a non-manual execution mode emits
    # nothing here. Any failure is swallowed; a notification fault must
    # never touch cook status, exit code, artifacts, or terminal frames.
    $emitNotification = {
        param([string]$Status, $ExitCode, $DurationSec, $RowCount)
        if (-not $notifyDispatchAvailable) { return }
        if ($ExecutionMode -and $ExecutionMode -ne 'local-manual') { return }
        try {
            $null = Invoke-NotificationDispatch -WorkspacePath $WorkspacePath `
                -CookId $CookId -RecipeId $RecipeId -RecipeName 'Bake' `
                -Source 'manual' -Status $Status `
                -ExitCode $ExitCode -DurationSec $DurationSec -RowCount $RowCount
        } catch {}
    }

    # ---------------- Inline helpers (ThreadJob runspace scope) ----------------

    $publish = {
        param([string]$Type, [string]$Data)
        if (-not $WsRegistry.ContainsKey($CookId)) { return }
        $frame = (@{ type = $Type; cookId = $CookId; data = $Data } | ConvertTo-Json -Compress -Depth 4)
        $list = $WsRegistry[$CookId]
        if ($null -eq $list) { return }
        $snapshot = $null
        [System.Threading.Monitor]::Enter($list.SyncRoot)
        try { $snapshot = $list.ToArray() } finally { [System.Threading.Monitor]::Exit($list.SyncRoot) }
        foreach ($sub in $snapshot) {
            if ($sub.closed) { continue }
            # Phase AG -- bounded enqueue, drop-OLDEST. Mirrors the
            # Publish-CookEvent guard in WebSocketHub.ps1. Both
            # publishers must apply the same cap symmetrically; the
            # cap lives on the subscriber so the policy is per-socket
            # and survives across both code paths.
            $junk = $null
            while ($sub.sendQueue.Count -ge $sub.sendQueueCap -and $sub.sendQueue.TryDequeue([ref]$junk)) {
                $sub.droppedFrames[0]++
            }
            [void]$sub.sendQueue.Enqueue($frame)
            try { $sub.sendSignal.Set() } catch {}
        }
    }

    $writeSentinel = {
        param([string]$Name, $Body)
        $path = Join-Path $CookFolder $Name
        $tmp  = $path + '.tmp'
        $json = $Body | ConvertTo-Json -Depth 6
        [System.IO.File]::WriteAllText($tmp, $json, [System.Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        [System.IO.File]::Move($tmp, $path, $true)
    }

    $logAppend = {
        param([System.IO.StreamWriter]$Writer, [string]$Line)
        try { $Writer.WriteLine($Line) } catch {}
    }

    $sqlExec = {
        param([string]$Sql, [hashtable]$Params)
        # Phase AB: opens a fresh connection and applies the canonical
        # PRAGMA set so every supervisor write goes through the same
        # WAL/synchronous=NORMAL/busy_timeout=5000/foreign_keys=ON
        # policy as the broker main connection. The inline list MUST
        # mirror Start-Broker.ps1::Set-SqliteConnectionPragmas --
        # ThreadJob runspaces cannot see dot-sourced functions, so the
        # parity is enforced by the AB harness, not by code reuse.
        # Phase AG.C6: wal_autocheckpoint=1000 added to make the
        # checkpoint policy explicit on supervisor connections too.
        # Single-statement autocommit. Do NOT widen to multi-statement
        # use -- multi-statement durability is the job of $sqlExecAtomic.
        $conn = [Microsoft.Data.Sqlite.SqliteConnection]::new($ConnectionString)
        try {
            $conn.Open()
            foreach ($pragma in @(
                'PRAGMA journal_mode=WAL;',
                'PRAGMA synchronous=NORMAL;',
                'PRAGMA busy_timeout=5000;',
                'PRAGMA foreign_keys=ON;',
                'PRAGMA wal_autocheckpoint=1000;'
            )) {
                $pc = $conn.CreateCommand()
                $pc.CommandText = $pragma
                [void]$pc.ExecuteNonQuery()
                $pc.Dispose()
            }
            $cmd = $conn.CreateCommand()
            $cmd.CommandText = $Sql
            foreach ($k in $Params.Keys) {
                $p = $cmd.CreateParameter()
                $p.ParameterName = $k
                $v = $Params[$k]
                if ($null -eq $v) { $p.Value = [System.DBNull]::Value } else { $p.Value = $v }
                [void]$cmd.Parameters.Add($p)
            }
            [void]$cmd.ExecuteNonQuery()
            $cmd.Dispose()
        } finally {
            try { $conn.Close() } catch {}
            try { $conn.Dispose() } catch {}
        }
    }

    $sqlExecAtomic = {
        # Phase AB: durable multi-statement terminal write. Takes an
        # array of @{ sql='...'; params=@{} } hashtables and runs them
        # under a single BEGIN IMMEDIATE / COMMIT on a single
        # connection. Used for the cook-terminal write (UPDATE cooks
        # status + INSERT cook_artifacts row) so that an observer can
        # never see a terminal cook with no artifact row, and a crash
        # mid-write leaves the cook in 'running' for C5 reconciliation
        # rather than half-terminal.
        #
        # Doctrine: narrow, explicit, durability-oriented. Do NOT widen
        # to wrap unrelated work, do NOT add retry, do NOT add ROLLBACK
        # logic that masks errors. On exception the throw propagates;
        # SQLite implicitly rolls back the uncommitted transaction on
        # connection close.
        param([object[]]$Statements)
        $conn = [Microsoft.Data.Sqlite.SqliteConnection]::new($ConnectionString)
        try {
            $conn.Open()
            foreach ($pragma in @(
                'PRAGMA journal_mode=WAL;',
                'PRAGMA synchronous=NORMAL;',
                'PRAGMA busy_timeout=5000;',
                'PRAGMA foreign_keys=ON;',
                'PRAGMA wal_autocheckpoint=1000;'
            )) {
                $pc = $conn.CreateCommand()
                $pc.CommandText = $pragma
                [void]$pc.ExecuteNonQuery()
                $pc.Dispose()
            }
            # BEGIN IMMEDIATE acquires the writer lock up-front,
            # surfacing contention as a clean SQLITE_BUSY (bounded by
            # busy_timeout=5000) rather than mid-transaction.
            $tx = $conn.BeginTransaction([System.Data.IsolationLevel]::Serializable)
            try {
                # SqliteCommand.Transaction wiring is implicit when the
                # connection has an open transaction; explicit assign
                # below for clarity and forward-compat.
                foreach ($stmt in $Statements) {
                    $cmd = $conn.CreateCommand()
                    $cmd.Transaction = $tx
                    $cmd.CommandText = [string]$stmt.sql
                    if ($stmt.params) {
                        foreach ($k in $stmt.params.Keys) {
                            $p = $cmd.CreateParameter()
                            $p.ParameterName = $k
                            $v = $stmt.params[$k]
                            if ($null -eq $v) { $p.Value = [System.DBNull]::Value } else { $p.Value = $v }
                            [void]$cmd.Parameters.Add($p)
                        }
                    }
                    [void]$cmd.ExecuteNonQuery()
                    $cmd.Dispose()
                }
                $tx.Commit()
            } catch {
                try { $tx.Rollback() } catch {}
                throw
            } finally {
                try { $tx.Dispose() } catch {}
            }
        } finally {
            try { $conn.Close() } catch {}
            try { $conn.Dispose() } catch {}
        }
    }

    # ---------------- Open per-cook append log ----------------

    $logPath  = Join-Path $CookFolder 'cook.log'
    $logFs    = [System.IO.File]::Open($logPath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    $logWr    = [System.IO.StreamWriter]::new($logFs, [System.Text.UTF8Encoding]::new($false))
    $logWr.AutoFlush = $true

    # ---------------- Build and spawn the child process ----------------
    #
    # $CommandExpr was built once by the broker dispatch path via
    # Get-PaxInvocationPlan (Pax/Adapter.psm1). It is the literal value
    # passed as the pwsh `-Command` argument and contains the inner
    # `& '<PaxScriptPath>' <paxCommand>` invocation. The supervisor uses
    # it verbatim -- no re-quoting, no flag injection, no path
    # re-escaping. The path-escaping rule for embedded single quotes is
    # the adapter's responsibility.

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName               = $PwshPath
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.RedirectStandardInput  = $true
    $psi.CreateNoWindow         = $true
    $psi.WorkingDirectory       = $CookFolder
    [void]$psi.ArgumentList.Add('-NoProfile')
    [void]$psi.ArgumentList.Add('-NoLogo')
    [void]$psi.ArgumentList.Add('-Command')
    [void]$psi.ArgumentList.Add($CommandExpr)

    $spawnErr = $null

    # Phase AF -- defense-in-depth executionMode gate. The Cooks.ps1
    # manual-cook entry refuses hosted execution modes outright, but
    # the supervisor repeats the check because it is the LAST writer
    # before $proc.Start(). Treat hosted modes as a spawn failure so
    # the existing spawn-failure sentinel path handles cleanup.
    $effectiveExecMode = $ExecutionMode
    if ([string]::IsNullOrWhiteSpace($effectiveExecMode)) {
        $effectiveExecMode = 'local-manual'
    }
    if ($effectiveExecMode -notin @('local-manual', 'local-scheduled')) {
        $spawnErr = "Refusing to spawn cook for executionMode '$effectiveExecMode'. The local supervisor only runs local-manual / local-scheduled cooks; hosted modes (fabric-hosted, azure-hosted) require their own runtime."
    }

    # Phase AF -- env-var secret injection for AppRegistrationSecret.
    # $ClientSecretSecure is a SecureString reference passed in from
    # the dispatch path (which read it from Windows Credential Manager
    # via Read-AuthProfileSecret). We marshal to a transient plaintext
    # string ONLY long enough to copy it into psi.EnvironmentVariables,
    # then zero-free the BSTR and remove the env-var entry from psi
    # immediately after $proc.Start() returns (the child has already
    # received its own copy of the env block). The original
    # SecureString is owned by the caller and disposed there.
    #
    # Acknowledged limitation: PowerShell strings are immutable; the
    # transient plaintext stays in managed memory until GC. The
    # primary defense is "the secret never leaves the broker process
    # except via Windows Credential Manager"; this best-effort zero
    # narrows the window but does not eliminate the GC retention
    # surface.
    #
    # Phase AG -- secret-isolation invariants (canonical reference).
    # The following properties hold across the entire broker codebase
    # and must NEVER regress:
    #
    #   1. The broker process MUST NOT call
    #      [Environment]::SetEnvironmentVariable('GRAPH_CLIENT_SECRET', ..., 'Process'|'User'|'Machine')
    #      under ANY code path. The verifier greps for this pattern
    #      and refuses to pass if found.
    #
    #   2. GRAPH_CLIENT_SECRET reaches the child ONLY via
    #      $psi.EnvironmentVariables['GRAPH_CLIENT_SECRET'] = ...
    #      together with $psi.UseShellExecute = $false. This combo
    #      gives the child a FRESH env block; the child does NOT
    #      inherit the broker's process env.
    #
    #   3. Sibling cooks each get their own $psi instance. A second
    #      cook spawned in parallel cannot see the first cook's
    #      secret because the second cook's psi has its own
    #      EnvironmentVariables dictionary.
    #
    #   4. Within the supervisor, the env-var entry is REMOVED from
    #      $psi.EnvironmentVariables immediately after $proc.Start()
    #      returns (success or failure). The BSTR is ZeroFreed in the
    #      same path. Both run unconditionally via the secret-cleanup
    #      block below.
    #
    #   5. The broker never logs, echoes, returns, or persists
    #      GRAPH_CLIENT_SECRET in cleartext. Recipe storage uses
    #      Windows Credential Manager (Read-AuthProfileSecret).
    $secretBSTR      = [IntPtr]::Zero
    $secretEnvSet    = $false
    if (-not $spawnErr -and $null -ne $ClientSecretSecure -and $ClientSecretSecure -is [System.Security.SecureString]) {
        try {
            $secretBSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($ClientSecretSecure)
            $plaintextSecret = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretBSTR)
            $psi.EnvironmentVariables['GRAPH_CLIENT_SECRET'] = $plaintextSecret
            $secretEnvSet = $true
            $plaintextSecret = $null
        } catch {
            $spawnErr = 'client_secret_marshal_failed: ' + $_.Exception.Message
        }
    }

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo           = $psi
    $proc.EnableRaisingEvents = $true

    # Phase AG -- bounded stdout/stderr queues. The drain loop normally
    # keeps these near-empty (cycles every ~50ms), but a misbehaving
    # child can emit faster than the drain consumes, or the loop can be
    # blocked momentarily during cancel handling. An unbounded queue is
    # a broker-side OOM vector. Behaviour at the cap is drop-OLDEST so
    # the most recent (and usually most diagnostically valuable for
    # stall / crash scenarios) lines survive; a counter is incremented
    # for each dropped line and surfaced in closure_evidence_json. The
    # cook.log file on disk is the authoritative full record and is
    # NOT bounded -- the cap only protects in-memory state.
    #
    # Cap is intentionally inlined here; ThreadJob runspaces cannot see
    # the parent's $Script: scope.
    $stdoutQ = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
    $stderrQ = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
    $stdoutCtx = @{
        queue   = $stdoutQ
        cap     = 50000
        dropped = [int[]]@(0)
        lastTs  = [datetime[]]@(([datetime]::UtcNow))
    }
    $stderrCtx = @{
        queue   = $stderrQ
        cap     = 50000
        dropped = [int[]]@(0)
        lastTs  = [datetime[]]@(([datetime]::UtcNow))
    }

    $stdoutSub = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived `
        -MessageData $stdoutCtx -Action {
            if ($null -ne $EventArgs.Data) {
                $ctx = $Event.MessageData
                $junk = $null
                while ($ctx.queue.Count -ge $ctx.cap -and $ctx.queue.TryDequeue([ref]$junk)) {
                    $ctx.dropped[0]++
                }
                $ctx.queue.Enqueue($EventArgs.Data)
                $ctx.lastTs[0] = [datetime]::UtcNow
            }
        }
    $stderrSub = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived `
        -MessageData $stderrCtx -Action {
            if ($null -ne $EventArgs.Data) {
                $ctx = $Event.MessageData
                $junk = $null
                while ($ctx.queue.Count -ge $ctx.cap -and $ctx.queue.TryDequeue([ref]$junk)) {
                    $ctx.dropped[0]++
                }
                $ctx.queue.Enqueue($EventArgs.Data)
                $ctx.lastTs[0] = [datetime]::UtcNow
            }
        }

    $startedAt = $null
    $childPid  = 0

    if (-not $spawnErr) {
        try {
            [void]$proc.Start()
            $childPid  = $proc.Id
            $startedAt = (Get-Date).ToUniversalTime().ToString('o')
            $proc.BeginOutputReadLine()
            $proc.BeginErrorReadLine()
        } catch {
            $spawnErr = $_.Exception.Message
        }
    }

    # ZERO the secret as soon as $proc.Start() returns (or fails). The
    # child already has its own copy of the env block; clearing the
    # parent's psi.EnvironmentVariables and zero-freeing the BSTR
    # narrows the broker-side window. This MUST run regardless of
    # whether Start() succeeded or threw.
    if ($secretBSTR -ne [IntPtr]::Zero) {
        try { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretBSTR) } catch {}
        $secretBSTR = [IntPtr]::Zero
    }
    if ($secretEnvSet) {
        try {
            if ($psi.EnvironmentVariables.ContainsKey('GRAPH_CLIENT_SECRET')) {
                [void]$psi.EnvironmentVariables.Remove('GRAPH_CLIENT_SECRET')
            }
        } catch {}
        $secretEnvSet = $false
    }

    if ($spawnErr) {
        # Spawn failed: write interrupted sentinel, mark row interrupted,
        # publish error/interrupted, clean up, exit job.
        & $logAppend $logWr ('[BROKER] cook spawn failed: ' + $spawnErr)
        try { $logWr.Dispose() } catch {}
        try { Unregister-Event -SourceIdentifier $stdoutSub.Name -ErrorAction SilentlyContinue } catch {}
        try { Unregister-Event -SourceIdentifier $stderrSub.Name -ErrorAction SilentlyContinue } catch {}

        $now = (Get-Date).ToUniversalTime().ToString('o')
        & $writeSentinel 'interrupted.json' ([ordered]@{
            interruptedAt  = $now
            lastKnownPhase = $null
            pid            = $null
            host           = $HostName
            reason         = ('spawn_failed: ' + $spawnErr)
        })

        # Phase AG -- distinguish executionmode-rejection from a
        # genuine $proc.Start() failure. The executionMode gate writes
        # $spawnErr with the verbatim 'Refusing to spawn' prefix; any
        # other value is a real Start() failure or a SecureString
        # unwrap failure.
        $spawnClosure = if ($spawnErr -like 'Refusing to spawn*')              { 'executionmode_rejected' }
                        elseif ($spawnErr -like 'client_secret_marshal_failed*') { 'client_secret_marshal_failed' }
                        else                                                   { 'spawn_failed' }
        $spawnEvidence = ([ordered]@{
            spawnErr      = $spawnErr
            spawnClosure  = $spawnClosure
            stdoutDropped = 0
            stderrDropped = 0
            childPid      = $null
        } | ConvertTo-Json -Compress -Depth 4)
        try {
            & $sqlExec @"
UPDATE cooks
SET status='interrupted',
    finished_at=@f,
    duration_seconds=0,
    error_class='spawn_failed',
    error_message=@em,
    closure_reason=COALESCE(closure_reason, @cr),
    closure_evidence_json=COALESCE(closure_evidence_json, @ev),
    abnormal_close_recorded_utc=COALESCE(abnormal_close_recorded_utc, @f),
    updated_at=@f
WHERE cook_id=@c
"@ @{ '@f' = $now; '@em' = $spawnErr; '@cr' = $spawnClosure; '@ev' = $spawnEvidence; '@c' = $CookId }
        } catch {}

        & $publish 'error'       ('spawn_failed: ' + $spawnErr)
        & $publish 'interrupted' 'spawn_failed'

        # N2 -- best-effort terminal notification for the spawn-failed
        # path. The row above persisted status='interrupted'; emit one
        # interrupted notification. No exit code, duration, or row count
        # is meaningful for a process that never started.
        & $emitNotification 'interrupted' $null $null $null

        try { $CookRegistry.Remove($CookId) } catch {}
        return
    }

    # ---------------- Successful spawn: started sentinel + PID + 'started' ----------------

    $CookState.pid       = $childPid
    $CookState.started_at = $startedAt

    & $writeSentinel 'started.json' ([ordered]@{
        cookId        = $CookId
        recipeId      = $RecipeId
        pid           = $childPid
        startedAt     = $startedAt
        command       = @($PwshPath, '-NoProfile', '-NoLogo', '-Command', $CommandExpr)
        pwshPath      = $PwshPath
        paxScriptPath = $PaxScriptPath
        host          = $HostName
    })

    try {
        # cooks.started_at is owned by the supervisor: it is set IFF the
        # child process actually launched. A row with status='running'
        # AND started_at IS NULL means the broker dispatched a supervisor
        # but spawn has not yet been confirmed (or, post-mortem, never
        # completed). This is an observable signal, not an error state.
        & $sqlExec 'UPDATE cooks SET pid=@p, started_at=@s, updated_at=@u WHERE cook_id=@c' `
            @{ '@p' = [int]$childPid; '@s' = $startedAt; '@u' = $startedAt; '@c' = $CookId }
    } catch {
        & $logAppend $logWr ('[BROKER] pid update failed: ' + $_.Exception.Message)
    }

    & $publish 'started' ('pid=' + $childPid)

    # ---------------- Drain loop ----------------

    $cancelHandled    = $false
    $interruptReason  = $null
    # Phase AG -- tracks whether a /stop request had to be escalated
    # to Kill($true) after the 5s cooperative grace expired. Drives
    # the closure_reason distinction between cancel_stop and
    # cancel_stop_escalated_kill at terminal-write time.
    $escalatedToKill  = $false

    # Phase AG -- stall detection. The supervisor SURFACES prolonged
    # output silence; it does NOT kill on stall. Multi-hour cooks are
    # legitimate and may be IO-bound with intermittent output. The
    # operator gets the signal (cook.log line + WS 'stall' frame +
    # closure_evidence_json flag) and decides whether to /stop. The
    # threshold is intentionally generous (10 minutes) and inlined --
    # this is a forensic signal, not a circuit-breaker.
    $stallSeconds      = 600
    $stallDetected     = $false
    $stallDetectedAt   = $null

    $drainOnce = {
        $line = $null
        while ($stdoutCtx.queue.TryDequeue([ref]$line)) {
            & $logAppend $logWr $line
            & $publish 'stdout' $line
        }
        while ($stderrCtx.queue.TryDequeue([ref]$line)) {
            & $logAppend $logWr ('[STDERR] ' + $line)
            & $publish 'stderr' $line
        }
    }

    while (-not $proc.HasExited) {
        & $drainOnce

        # Phase AG -- stall surfacing. Fires once per cook. The signal
        # persists in closure_evidence_json even after output resumes.
        # The WS frame is routed through type='stderr' with a [BROKER]
        # prefix because the M1 frame model is FROZEN (no new types);
        # supervisor-side notices ride on stderr, mirroring how
        # cook.log captures them.
        if (-not $stallDetected) {
            $latestOutput = $stdoutCtx.lastTs[0]
            if ($stderrCtx.lastTs[0] -gt $latestOutput) { $latestOutput = $stderrCtx.lastTs[0] }
            $idleSec = ([datetime]::UtcNow - $latestOutput).TotalSeconds
            if ($idleSec -ge $stallSeconds) {
                $stallDetected   = $true
                $stallDetectedAt = (Get-Date).ToUniversalTime().ToString('o')
                & $logAppend $logWr ('[BROKER] stall detected: no stdout/stderr for ' + [int]$idleSec + 's')
                & $publish 'stderr' ('[BROKER] stall detected: no stdout/stderr for ' + [int]$idleSec + 's')
            }
        }

        if (-not $cancelHandled) {
            $mode = $CookState.cancel_mode
            if ($mode -eq 'kill') {
                $cancelHandled   = $true
                $interruptReason = 'kill'
                try { $proc.Kill($true) } catch {}
            }
            elseif ($mode -eq 'stop') {
                $cancelHandled   = $true
                $interruptReason = 'stop'
                # Cooperative shutdown: close stdin (EOF), wait up to 5s,
                # then force-terminate the process tree. This is the
                # entire /stop contract; no SIGINT, no Win32 console
                # P/Invoke.
                try { $proc.StandardInput.Close() } catch {}
                if (-not $proc.WaitForExit(5000)) {
                    # Phase AG -- record that the 5s grace expired so
                    # the terminal write can distinguish cancel_stop
                    # from cancel_stop_escalated_kill.
                    $escalatedToKill = $true
                    try { $proc.Kill($true) } catch {}
                }
            }
        }

        Start-Sleep -Milliseconds 50
    }

    # Final drain after exit so any buffered lines reach cook.log + WS.
    try { $proc.WaitForExit() } catch {}
    & $drainOnce
    Start-Sleep -Milliseconds 100
    & $drainOnce

    try { Unregister-Event -SourceIdentifier $stdoutSub.Name -ErrorAction SilentlyContinue } catch {}
    try { Unregister-Event -SourceIdentifier $stderrSub.Name -ErrorAction SilentlyContinue } catch {}

    $finishedAt = (Get-Date).ToUniversalTime().ToString('o')
    $duration   = ([datetime]$finishedAt - [datetime]$startedAt).TotalSeconds
    $exitCode   = $null
    try { $exitCode = [int]$proc.ExitCode } catch {}

    try { $logWr.Dispose() } catch {}
    try { $proc.Dispose() } catch {}

    # ---------------- Sentinel + DB finalization ----------------

    if ($cancelHandled) {
        & $writeSentinel 'interrupted.json' ([ordered]@{
            interruptedAt  = $finishedAt
            lastKnownPhase = $null
            pid            = $childPid
            host           = $HostName
            reason         = $interruptReason
        })
        $finalStatus = 'interrupted'
    } else {
        # Schema must match Set-CookFromFinishedJson in Start-Broker.ps1
        # (durationSec, not durationSeconds; errorSummary optional).
        & $writeSentinel 'finished.json' ([ordered]@{
            exitCode    = $exitCode
            finishedAt  = $finishedAt
            durationSec = $duration
        })
        if ($exitCode -eq 0) { $finalStatus = 'completed' } else { $finalStatus = 'errored' }
    }

    try {
        $errClass = $null
        if ($cancelHandled) { $errClass = $interruptReason }
        elseif ($finalStatus -eq 'errored') { $errClass = 'nonzero_exit' }

        # Phase AG -- compute the granular closure_reason. The supervisor
        # is the FIRST writer (closer to the event than C5 reconciliation),
        # so its value wins via COALESCE on subsequent reconciliation.
        # The frozen vocabulary is documented in Start-Broker.ps1's
        # $Script:CookClosureReasons block. Reconciliation contributes
        # broker_restart_* values; the supervisor never uses those.
        $closureReason = if ($cancelHandled -and $interruptReason -eq 'stop' -and $escalatedToKill) { 'cancel_stop_escalated_kill' }
                         elseif ($cancelHandled -and $interruptReason -eq 'stop')                  { 'cancel_stop' }
                         elseif ($cancelHandled -and $interruptReason -eq 'kill')                  { 'cancel_kill' }
                         elseif ($finalStatus -eq 'completed')                                     { 'clean_exit' }
                         else                                                                      { 'nonzero_exit' }
        # abnormal_close_recorded_utc is set for everything OTHER than
        # the two natural-completion reasons. nonzero_exit is a normal
        # app-level failure (the child ran to completion of its own
        # accord) and is NOT an abnormal closure; cancel/kill/escalation
        # paths ARE abnormal closures.
        $abnormalCloseAt = if ($closureReason -in @('clean_exit', 'nonzero_exit')) { $null } else { $finishedAt }

        # Phase AG -- closure_evidence_json. A compact forensic record of
        # in-supervisor signals that don't have their own first-class
        # column. Fields are STABLE within Phase AG; downstream code
        # may read them positionally. Future phases can extend the
        # object, never rename existing keys.
        $lastOutputUtc = $stdoutCtx.lastTs[0]
        if ($stderrCtx.lastTs[0] -gt $lastOutputUtc) { $lastOutputUtc = $stderrCtx.lastTs[0] }

        # V1.S03 -- best-effort PAX checkpoint discovery. PAX creates
        # `.pax_checkpoint_<RunTimestamp>.json` in -OutputPath at init
        # and deletes it on successful completion. On interruption the
        # file remains on disk and lets the operator resume via
        # POST /api/v1/cooks/<id>/resume. Discovery is BEST-EFFORT
        # filesystem IO only -- the result is stored as a forensic
        # key inside closure_evidence_json, never touched the cook
        # lifecycle, and never causes the supervisor to alter PAX-owned
        # files.
        #
        # Discovery is gated to interruption paths that are RESUMABLE
        # in principle:
        #   - cancel_stop / cancel_stop_escalated_kill (chef pressed
        #     Stop; PAX had a chance to flush its checkpoint).
        # cancel_kill is excluded by design: a force-kill gives PAX no
        # opportunity to flush the partition that was in flight, so
        # the on-disk checkpoint may reflect a partially-completed
        # state that is unsafe to resume. clean_exit / nonzero_exit
        # are also excluded (PAX deletes the checkpoint on clean_exit
        # and the resumability rule excludes nonzero_exit anyway).
        $checkpointPath = $null
        if ($cancelHandled -and $interruptReason -ne 'kill') {
            try {
                $probeDirs = New-Object System.Collections.Generic.List[string]
                if ($FactPath) {
                    try {
                        $probeFactDir = [System.IO.Path]::GetDirectoryName($FactPath)
                        if ($probeFactDir -and (Test-Path -LiteralPath $probeFactDir -PathType Container)) {
                            $probeDirs.Add($probeFactDir)
                        }
                    } catch {}
                }
                if ($CookFolder -and (Test-Path -LiteralPath $CookFolder -PathType Container)) {
                    $probeDirs.Add($CookFolder)
                }
                $newestCheckpoint = $null
                foreach ($dir in $probeDirs) {
                    try {
                        $checkpointMatches = Get-ChildItem -LiteralPath $dir -File -Filter '.pax_checkpoint_*.json' -Force -ErrorAction SilentlyContinue
                        foreach ($m in $checkpointMatches) {
                            if ($null -eq $newestCheckpoint -or $m.LastWriteTimeUtc -gt $newestCheckpoint.LastWriteTimeUtc) {
                                $newestCheckpoint = $m
                            }
                        }
                    } catch {}
                }
                if ($newestCheckpoint) { $checkpointPath = $newestCheckpoint.FullName }
            } catch {}
        }

        $closureEvidence = ([ordered]@{
            stdoutDropped   = [int]$stdoutCtx.dropped[0]
            stderrDropped   = [int]$stderrCtx.dropped[0]
            stallDetected   = [bool]$stallDetected
            stallDetectedAt = $stallDetectedAt
            lastOutputAt    = $lastOutputUtc.ToString('o')
            escalatedToKill = [bool]$escalatedToKill
            interruptReason = $interruptReason
            childPid        = [int]$childPid
            checkpointPath  = $checkpointPath
        } | ConvertTo-Json -Compress -Depth 4)

        if ([int]$stdoutCtx.dropped[0] -gt 0) {
            try { & $logAppend $logWr ('[BROKER] stdout queue overflow: ' + [int]$stdoutCtx.dropped[0] + ' lines dropped (oldest-first)') } catch {}
        }
        if ([int]$stderrCtx.dropped[0] -gt 0) {
            try { & $logAppend $logWr ('[BROKER] stderr queue overflow: ' + [int]$stderrCtx.dropped[0] + ' lines dropped (oldest-first)') } catch {}
        }

        # Phase AB: terminal write is ONE transaction containing
        # UPDATE cooks (status + exit + duration + finished_at) AND
        # INSERT cook_artifacts (one row pointing at the fact dest).
        # Pre-AB this was two autocommits, which left an observable
        # window where a terminal cook had no artifact row. The atomic
        # write closes that window. On failure the cook row stays
        # 'running' and C5 sentinel reconciliation on next broker
        # startup classifies it from the sentinel files written above
        # (finished.json / interrupted.json).
        $artId = [guid]::NewGuid().ToString()
        $size  = $null
        if ($FactPath -and (Test-Path -LiteralPath $FactPath -PathType Leaf)) {
            try { $size = [long](Get-Item -LiteralPath $FactPath).Length } catch {}
        }

        # V1.S02 best-effort summary / metrics discovery. Runs purely
        # via filesystem IO + JSON.Parse; any exception is swallowed.
        # Two outputs feed the existing atomic write below:
        #   $summaryPath  -> cooks.summary_path
        #   $factRowCount -> cook_artifacts.row_count for the fact row
        # Neither value affects cook lifecycle. Discovery never
        # invokes PAX, never writes files, never modifies the cook
        # folder. Candidate files (first existing wins):
        #   1. <CookFolder>/pax-summary.json
        #   2. <CookFolder>/metrics.json
        #   3. <FactDestDir>/metrics.json
        #   4. <FactDestDir>/<factBaseName>_metrics_*.json  (current
        #      PAX -EmitMetricsJson shape; most recent by mtime).
        $summaryPath  = $null
        $factRowCount = $null
        try {
            $candidates = New-Object System.Collections.Generic.List[string]
            foreach ($name in @('pax-summary.json', 'metrics.json')) {
                $p = Join-Path $CookFolder $name
                if (Test-Path -LiteralPath $p -PathType Leaf) { $candidates.Add($p) }
            }
            $factDir  = $null
            $factBase = $null
            if ($FactPath) {
                try {
                    $factDir  = [System.IO.Path]::GetDirectoryName($FactPath)
                    $factBase = [System.IO.Path]::GetFileNameWithoutExtension($FactPath)
                } catch {}
            }
            if ($factDir -and (Test-Path -LiteralPath $factDir -PathType Container)) {
                $factSibling = Join-Path $factDir 'metrics.json'
                if (Test-Path -LiteralPath $factSibling -PathType Leaf) { $candidates.Add($factSibling) }
                if ($factBase) {
                    try {
                        $pattern   = $factBase + '_metrics_*.json'
                        $patternMatches = Get-ChildItem -LiteralPath $factDir -File -Filter $pattern -ErrorAction SilentlyContinue
                        foreach ($m in ($patternMatches | Sort-Object LastWriteTimeUtc -Descending)) {
                            $candidates.Add($m.FullName)
                        }
                    } catch {}
                }
            }
            if ($candidates.Count -gt 0) {
                $summaryPath = $candidates[0]
                try {
                    $raw = [System.IO.File]::ReadAllText($summaryPath, [System.Text.UTF8Encoding]::new($false))
                    if (-not [string]::IsNullOrWhiteSpace($raw)) {
                        $obj = $raw | ConvertFrom-Json -AsHashtable -Depth 12
                        if ($obj -is [hashtable]) {
                            # Defensive numeric pluck. Probes a small
                            # set of universally-named row-count keys.
                            # First finite numeric match wins.
                            $rowPaths = @(
                                'rowsWritten', 'rows', 'totalRows',
                                'records', 'recordCount',
                                'TotalStructuredRows', 'TotalRecordsFetched',
                                'metrics.rowsWritten', 'metrics.TotalStructuredRows',
                                'metrics.TotalRecordsFetched', 'metrics.totalRows'
                            )
                            foreach ($rp in $rowPaths) {
                                $cursor = $obj
                                $okPath = $true
                                foreach ($seg in $rp.Split('.')) {
                                    if (-not ($cursor -is [hashtable])) { $okPath = $false; break }
                                    if (-not $cursor.ContainsKey($seg)) { $okPath = $false; break }
                                    $cursor = $cursor[$seg]
                                }
                                if (-not $okPath -or $null -eq $cursor) { continue }
                                try {
                                    $n = [double]$cursor
                                    if ([double]::IsNaN($n) -or [double]::IsInfinity($n)) { continue }
                                    $factRowCount = [long]$cursor
                                    break
                                } catch {}
                            }
                        }
                    }
                } catch {}
            }
        } catch {}

        $terminalUpdate = @{
            sql = @"
UPDATE cooks
SET status=@s,
    exit_code=@e,
    finished_at=@f,
    duration_seconds=@d,
    error_class=@ec,
    closure_reason=COALESCE(closure_reason, @cr),
    closure_evidence_json=COALESCE(closure_evidence_json, @ev),
    abnormal_close_recorded_utc=COALESCE(abnormal_close_recorded_utc, @ac),
    summary_path=@sp,
    updated_at=@f
WHERE cook_id=@c
"@
            params = @{
                '@s'  = $finalStatus
                '@e'  = if ($null -eq $exitCode) { $null } else { [int]$exitCode }
                '@f'  = $finishedAt
                '@d'  = [double]$duration
                '@ec' = $errClass
                '@cr' = $closureReason
                '@ev' = $closureEvidence
                '@ac' = $abnormalCloseAt
                '@sp' = if ($null -eq $summaryPath) { $null } else { [string]$summaryPath }
                '@c'  = $CookId
            }
        }

        # Insert exactly one cook_artifacts row pointing at the fact dest.
        # No glob, no scan, no inference. If the file isn't there (cook
        # failed before writing), size_bytes=NULL. V1.S02 also records
        # row_count when the discovered metrics JSON yielded a finite
        # numeric value via the defensive probe above; NULL otherwise.
        $artifactInsert = @{
            sql = @"
INSERT INTO cook_artifacts
    (cook_artifact_id, cook_id, stream, artifact_kind, tier, location, size_bytes, row_count, is_append, pantry_dataset_id, created_at)
VALUES
    (@aid, @cid, 'fact', 'file', 'Local', @loc, @sz, @rc, 0, NULL, @ts)
"@
            params = @{
                '@aid' = $artId
                '@cid' = $CookId
                '@loc' = $FactPath
                '@sz'  = if ($null -eq $size) { $null } else { [long]$size }
                '@rc'  = if ($null -eq $factRowCount) { $null } else { [long]$factRowCount }
                '@ts'  = $finishedAt
            }
        }

        # V1.S06c best-effort PAX detailed-log + pax_metrics discovery.
        # The supervisor does NOT instrument PAX; both helpers walk the
        # fact dest directory by filesystem IO only. Both are absence-
        # tolerant — a missing file never fails the cook. Limitations:
        # PAX may write its detailed .log to OutputPathLog (e.g. a
        # OneLake/SharePoint dest) rather than alongside the fact CSV;
        # in that case discovery returns no rows and history shows
        # only the fact + metrics artifact. The recipe's filePrefix
        # is also not introspected; we glob the broad 'Purview_Audit
        # *.log' pattern that covers the engine's default basename.
        $paxLogPath  = $null
        $paxLogSize  = $null
        $paxLogMtime = $null
        try {
            if ($factDir -and (Test-Path -LiteralPath $factDir -PathType Container)) {
                $logCandidates = Get-ChildItem -LiteralPath $factDir -File -Filter 'Purview_Audit*.log' -Force -ErrorAction SilentlyContinue |
                    Where-Object { -not ($_.Name.EndsWith('_PARTIAL.log')) }
                if ($logCandidates) {
                    $bestLog = $logCandidates | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
                    if ($bestLog) {
                        $paxLogPath  = $bestLog.FullName
                        try { $paxLogSize = [long]$bestLog.Length } catch {}
                        try { $paxLogMtime = $bestLog.LastWriteTimeUtc.ToString('o') } catch {}
                    }
                }
            }
        } catch {}

        $paxLogInsert = $null
        if ($paxLogPath) {
            $paxLogInsert = @{
                sql = @"
INSERT INTO cook_artifacts
    (cook_artifact_id, cook_id, stream, artifact_kind, tier, location, size_bytes, row_count, is_append, pantry_dataset_id, created_at)
VALUES
    (@aid, @cid, 'pax_log', 'file', 'Local', @loc, @sz, NULL, 0, NULL, @ts)
"@
                params = @{
                    '@aid' = [guid]::NewGuid().ToString()
                    '@cid' = $CookId
                    '@loc' = $paxLogPath
                    '@sz'  = if ($null -eq $paxLogSize) { $null } else { [long]$paxLogSize }
                    '@ts'  = if ($paxLogMtime) { $paxLogMtime } else { $finishedAt }
                }
            }
        }

        # pax_metrics row mirrors the V1.S02 metrics-JSON discovery
        # outcome (the same $summaryPath that flows into cooks.summary
        # _path). cook_artifacts is the canonical artifact registry,
        # so we record the metrics file there too. The row_count we
        # plucked is the FACT row count; metrics row is a metadata
        # artifact, so row_count=NULL on this row.
        $paxMetricsInsert = $null
        if ($summaryPath -and (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
            $metricsSize = $null
            try { $metricsSize = [long](Get-Item -LiteralPath $summaryPath).Length } catch {}
            $paxMetricsInsert = @{
                sql = @"
INSERT INTO cook_artifacts
    (cook_artifact_id, cook_id, stream, artifact_kind, tier, location, size_bytes, row_count, is_append, pantry_dataset_id, created_at)
VALUES
    (@aid, @cid, 'pax_metrics', 'file', 'Local', @loc, @sz, NULL, 0, NULL, @ts)
"@
                params = @{
                    '@aid' = [guid]::NewGuid().ToString()
                    '@cid' = $CookId
                    '@loc' = [string]$summaryPath
                    '@sz'  = if ($null -eq $metricsSize) { $null } else { [long]$metricsSize }
                    '@ts'  = $finishedAt
                }
            }
        }

        # Terminal atomic write. Phase AB doctrine: ONE transaction.
        # The fixed prefix `@($terminalUpdate, $artifactInsert)` is
        # preserved so AB.C04 can grep-assert the atomic shape; any
        # discovered PAX-log / pax_metrics rows ride along inside
        # the same transaction.
        $atomicStmts = @($terminalUpdate, $artifactInsert)
        if ($paxLogInsert)     { $atomicStmts += $paxLogInsert }
        if ($paxMetricsInsert) { $atomicStmts += $paxMetricsInsert }
        & $sqlExecAtomic $atomicStmts
    } catch {
        # DB finalize failure leaves the row in 'running' state; C5
        # reconciliation on next broker startup will resolve it via the
        # on-disk sentinel files written above.
    }

    if ($cancelHandled) { & $publish 'interrupted' $interruptReason }
    else                { & $publish 'finished'    ('exit=' + $exitCode) }

    # N2 -- best-effort terminal notification. The terminal sentinel
    # (finished.json / interrupted.json) was written above and the
    # atomic DB write persisted $finalStatus, so the terminal state is
    # durable regardless of the DB write outcome. Emit exactly one
    # notification mapped from $finalStatus, mirroring the terminal
    # frame timing. Wrapped best-effort: a notification fault cannot
    # touch cook status, exit code, artifacts, or the frames above.
    & $emitNotification $finalStatus $exitCode $duration $factRowCount

    try { $CookRegistry.Remove($CookId) } catch {}
}

# ---------------------------------------------------------------------
# Dispatch-side entry point
# ---------------------------------------------------------------------

function Start-CookSupervisor {
    param(
        [Parameter(Mandatory)] [string]$CookId,
        [Parameter(Mandatory)] [string]$RecipeId,
        [Parameter(Mandatory)] [string]$CommandExpr,
        [Parameter(Mandatory)] [string]$CookFolder,
        [Parameter(Mandatory)] [string]$PwshPath,
        [Parameter(Mandatory)] [string]$PaxScriptPath,
        [Parameter()]          [string]$FactPath,
        [Parameter(Mandatory)] [string]$ConnectionString,
        [Parameter(Mandatory)] [string]$HostName,
        [Parameter(Mandatory)] [hashtable]$CookState,
        # Phase AF: empty / unset -> supervisor defaults to
        # 'local-manual'. Caller (Cooks.ps1) decides per-trigger;
        # manual cook entries pass 'local-manual', scheduler entries
        # will pass 'local-scheduled' when AF.C9 lands the schedule
        # config gate.
        [Parameter()]          [string]$ExecutionMode = '',
        # Phase AF: SecureString reference for AppRegistrationSecret
        # cooks; $null for every other auth mode. The supervisor zero-
        # frees the BSTR copy after $proc.Start(); the original
        # SecureString remains owned by the caller.
        [Parameter()]          [System.Security.SecureString]$ClientSecretSecure = $null
    )

    # $CommandExpr is the literal pwsh `-Command` value. It is built
    # ONCE by the broker dispatch path via Get-PaxInvocationPlan and is
    # the single source of authority for what the child process
    # actually executes. The supervisor does not rebuild it.
    #
    # The supervisor runspace cannot see the main-runspace dot-source of
    # the notification core nor $Script:WorkspacePath, so both are
    # resolved here and passed positionally into the ThreadJob.
    $notifyModulePath = Join-Path $Script:BrokerRoot 'Notify\Notification.ps1'
    [void](Start-ThreadJob -ScriptBlock $Script:CookSupervisorScript -ArgumentList @(
        $CookId, $RecipeId, $CommandExpr, $CookFolder, $PwshPath, $PaxScriptPath,
        $FactPath, $ConnectionString, $HostName,
        $ExecutionMode, $ClientSecretSecure,
        $CookState, $Script:WsRegistry, $Script:CookRegistry,
        $notifyModulePath, $Script:WorkspacePath
    ))
}

function Request-CookCancel {
    # Sets the cancel mode on the per-cook state hashtable; the
    # supervisor's drain loop observes it within ~50 ms.
    param(
        [Parameter(Mandatory)] [string]$CookId,
        [Parameter(Mandatory)] [ValidateSet('stop','kill')] [string]$Mode
    )
    if (-not $Script:CookRegistry.ContainsKey($CookId)) { return $false }
    $state = $Script:CookRegistry[$CookId]
    # /kill always wins over /stop (even an already-set /stop).
    if ($Mode -eq 'kill' -or $state.cancel_mode -eq 'none') {
        $state.cancel_mode = $Mode
    }
    return $true
}
