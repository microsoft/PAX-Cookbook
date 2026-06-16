#requires -Version 7.4

# =====================================================================
# PAX Cookbook -- session-scoped app-window watchdog.
#
# Detects when the Edge --app= app-window for THIS PAX Cookbook
# session disappears via the OS X / taskbar Close path (i.e. NOT via
# the in-SPA Close App modal), waits a short grace period to be sure
# the close was not a refresh / reopen / shutdown, and then shows a
# native WinForms prompt offering three actions:
#
#   - Keep server running   : dismiss; broker keeps running; this
#                             watchdog exits (no nag loop).
#   - Stop server           : invoke Stop-PAXCookbook.ps1 in a
#                             separate hidden pwsh.exe; the existing
#                             Stop helper closes the Edge --app=
#                             window, terminates the broker, releases
#                             the port, and cleans sidecars.
#   - Reopen Cookbook       : invoke Start-PAXCookbook.ps1 in a
#                             separate hidden pwsh.exe; the launcher's
#                             reuse path opens a fresh Edge --app=
#                             window against the still-running broker
#                             (no second broker is spawned). Then this
#                             watchdog re-arms to monitor the new
#                             Edge process.
#
# This script is invoked from the launcher prelude
# (launcher\Start-PAXCookbook.ps1) as a sibling hidden pwsh process.
# It deliberately lives OUTSIDE the broker's pwsh.exe so that:
#
#   - the broker process owns only HTTP / cook engine concerns;
#   - the broker is never blocked by a WinForms message pump or an
#     STA-runspace startup;
#   - a crash in WinForms / dialog code cannot destabilize the broker.
#
# The watchdog is session-scoped: it self-exits when the broker
# process disappears (workspace.lock brokerProcessId stops being a
# live pwsh.exe), so it never survives the PAX Cookbook session.
# A runtime lock file (<Workspace>\Runtime\app-close-watcher.lock)
# prevents two watchdogs for the same workspace.
#
# Non-negotiables (cross-checked against the phase doctrine):
#   - No WebView2. WinForms only.
#   - No automatic taskbar pinning.
#   - No permanent Edge/PWA install.
#   - No broad Edge/browser killing. The Stop button delegates to
#     Stop-PAXCookbook.ps1, which already targets ONLY msedge.exe
#     processes whose CommandLine references our EdgeAppData dir.
#   - No Read-Host / pause / Press-Enter behaviour. The console is
#     hidden; any blocking prompt would hang invisibly.
#   - No regex-driven mutation. No .bak files. No dev comments.
# =====================================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkspacePath
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------
# Self-locate: resolve install root + sibling launcher / Stop helper
# paths the same way the Stop helper does, so dev-tree and installed
# layouts both work.
# ---------------------------------------------------------------------
$Script:LauncherDir = Split-Path -Parent $PSCommandPath
$Script:AppRoot     = $null
$probeParent = Split-Path -Parent $Script:LauncherDir
$probeBroker = Join-Path $probeParent 'broker\Start-Broker.ps1'
if (Test-Path -LiteralPath $probeBroker -PathType Leaf) {
    $Script:AppRoot = $probeParent
} else {
    $probeAppRoot = Join-Path $probeParent 'app'
    $probeBroker2 = Join-Path $probeAppRoot 'broker\Start-Broker.ps1'
    if (Test-Path -LiteralPath $probeBroker2 -PathType Leaf) {
        $Script:AppRoot = $probeAppRoot
    }
}
if (-not $Script:AppRoot) { exit 5 }

$Script:InstallRoot       = Split-Path -Parent $Script:AppRoot
$Script:StartScript       = Join-Path $Script:LauncherDir 'Start-PAXCookbook.ps1'
$Script:StopScript        = Join-Path $Script:LauncherDir 'Stop-PAXCookbook.ps1'
$Script:RuntimeDir        = Join-Path $WorkspacePath 'Runtime'
$Script:LockFile          = Join-Path $Script:RuntimeDir 'workspace.lock'
$Script:BrowserSidecar    = Join-Path $Script:RuntimeDir 'browser.window.json'
$Script:IntentFile        = Join-Path $Script:RuntimeDir 'app-close-intent.json'
$Script:WatcherLockFile   = Join-Path $Script:RuntimeDir 'app-close-watcher.lock'
$Script:WatcherLogFile    = Join-Path $Script:InstallRoot 'install.log'
$Script:EdgeAppDataDir    = Join-Path $Script:InstallRoot 'EdgeAppData'

function Write-WatcherLog {
    param([string]$Line)
    $ts = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $entry = '[' + $ts + '] [watcher pid=' + $PID + '] ' + $Line
    try { Add-Content -LiteralPath $Script:WatcherLogFile -Value $entry -ErrorAction SilentlyContinue } catch { }
}

# ---------------------------------------------------------------------
# Locate Stop helper. Required for the Stop button. If it is missing
# we still arm the watchdog (Keep / Reopen still work) but the Stop
# button will surface an error dialog rather than silently no-op.
# ---------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $Script:StopScript -PathType Leaf)) {
    Write-WatcherLog ('Stop helper not found at expected path: ' + $Script:StopScript)
}
if (-not (Test-Path -LiteralPath $Script:StartScript -PathType Leaf)) {
    Write-WatcherLog ('Start launcher not found at expected path: ' + $Script:StartScript)
}

if (-not (Test-Path -LiteralPath $Script:RuntimeDir -PathType Container)) {
    try { $null = New-Item -ItemType Directory -Path $Script:RuntimeDir -Force } catch { }
}

# ---------------------------------------------------------------------
# Duplicate-watcher prevention.
# Lock schema (JSON):
#   {
#     "schemaVersion": 1,
#     "watcherProcessId": <pid>,
#     "workspacePath":    "<path>",
#     "installRoot":      "<path>",
#     "createdUtc":       "<iso>"
#   }
# A peer watcher is considered alive iff its PID still resolves to a
# pwsh.exe AND its lock file references this install root + workspace.
# Stale or foreign locks are replaced.
# ---------------------------------------------------------------------
function Read-WatcherLock {
    if (-not (Test-Path -LiteralPath $Script:WatcherLockFile -PathType Leaf)) { return $null }
    try {
        $raw = Get-Content -LiteralPath $Script:WatcherLockFile -Raw -ErrorAction Stop
        return ($raw | ConvertFrom-Json -ErrorAction Stop)
    } catch { return $null }
}

function Test-WatcherLockAlive {
    param($Lock)
    if (-not $Lock) { return $false }
    $names = @($Lock.PSObject.Properties.Name)
    if ($names -notcontains 'watcherProcessId') { return $false }
    $peerPid = 0
    try { $peerPid = [int]$Lock.watcherProcessId } catch { return $false }
    if ($peerPid -le 0 -or $peerPid -eq $PID) { return $false }
    $proc = $null
    try { $proc = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $peerPid) -ErrorAction SilentlyContinue } catch { return $false }
    if (-not $proc) { return $false }
    if ($proc.Name -ne 'pwsh.exe') { return $false }
    $cl = [string]$proc.CommandLine
    if (-not $cl) { return $false }
    if ($cl -notlike '*Watch-PAXCookbookAppWindow.ps1*') { return $false }
    if ($cl -notlike ('*' + $Script:InstallRoot + '*')) { return $false }
    return $true
}

function Write-WatcherLock {
    $obj = [ordered]@{
        schemaVersion    = 1
        watcherProcessId = $PID
        workspacePath    = $WorkspacePath
        installRoot      = $Script:InstallRoot
        createdUtc       = (Get-Date).ToUniversalTime().ToString('o')
    }
    $json = $obj | ConvertTo-Json -Depth 4
    $tmp = $Script:WatcherLockFile + '.tmp'
    Set-Content -LiteralPath $tmp -Value $json -Encoding utf8 -NoNewline -Force
    Move-Item -LiteralPath $tmp -Destination $Script:WatcherLockFile -Force
}

function Remove-WatcherLock {
    if (Test-Path -LiteralPath $Script:WatcherLockFile -PathType Leaf) {
        try { Remove-Item -LiteralPath $Script:WatcherLockFile -Force -ErrorAction SilentlyContinue } catch { }
    }
}

$Script:ExistingLock = Read-WatcherLock
if (Test-WatcherLockAlive -Lock $Script:ExistingLock) {
    Write-WatcherLog ('Peer watcher already running (pid=' + [int]$Script:ExistingLock.watcherProcessId + '); exiting.')
    exit 0
}
try {
    Write-WatcherLock
    Write-WatcherLog ('Watcher lock acquired for workspace=' + $WorkspacePath)
} catch {
    Write-WatcherLog ('Failed to acquire watcher lock: ' + $_.Exception.Message)
    exit 6
}

# ---------------------------------------------------------------------
# Broker liveness: workspace.lock holds the broker PID. If the file
# is missing OR the PID no longer resolves to a pwsh.exe under our
# install tree, the broker is gone and the watcher must exit silently.
# ---------------------------------------------------------------------
function Get-BrokerProcessId {
    if (-not (Test-Path -LiteralPath $Script:LockFile -PathType Leaf)) { return 0 }
    try {
        $raw = Get-Content -LiteralPath $Script:LockFile -Raw -ErrorAction Stop
        $obj = $raw | ConvertFrom-Json -ErrorAction Stop
        if ($obj.PSObject.Properties['brokerProcessId']) { return [int]$obj.brokerProcessId }
    } catch { }
    return 0
}

function Test-BrokerAlive {
    $brokerPid = Get-BrokerProcessId
    if ($brokerPid -le 0) { return $false }
    $proc = $null
    try { $proc = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $brokerPid) -ErrorAction SilentlyContinue } catch { return $false }
    if (-not $proc) { return $false }
    if ($proc.Name -ne 'pwsh.exe') { return $false }
    $cl = [string]$proc.CommandLine
    if (-not $cl) { return $false }
    if ($cl -notlike ('*' + $Script:InstallRoot + '*')) { return $false }
    return $true
}

# ---------------------------------------------------------------------
# App-window discovery. browser.window.json is written by the launcher
# after Start-Process -PassThru on msedge.exe with our --user-data-dir.
# Cross-check: the recorded PID must be msedge.exe AND its
# CommandLine must reference our EdgeAppData dir, otherwise we are
# looking at an unrelated browser session.
# ---------------------------------------------------------------------
function Read-BrowserSidecar {
    if (-not (Test-Path -LiteralPath $Script:BrowserSidecar -PathType Leaf)) { return $null }
    try {
        $raw = Get-Content -LiteralPath $Script:BrowserSidecar -Raw -ErrorAction Stop
        return ($raw | ConvertFrom-Json -ErrorAction Stop)
    } catch { return $null }
}

function Get-EdgeAppProcessId {
    $side = Read-BrowserSidecar
    if (-not $side) { return 0 }
    if (-not $side.PSObject.Properties['processId']) { return 0 }
    $edgePid = 0
    try { $edgePid = [int]$side.processId } catch { return 0 }
    if ($edgePid -le 0) { return 0 }
    $proc = $null
    try { $proc = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $edgePid) -ErrorAction SilentlyContinue } catch { return 0 }
    if (-not $proc) { return 0 }
    if ($proc.Name -ne 'msedge.exe') { return 0 }
    $cl = [string]$proc.CommandLine
    if (-not $cl) { return 0 }
    if ($cl -notlike ('*' + $Script:EdgeAppDataDir + '*')) { return 0 }
    return $edgePid
}

function Wait-ForEdgeAppProcess {
    param([int]$TimeoutSec = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-BrokerAlive)) { return 0 }
        $epid = Get-EdgeAppProcessId
        if ($epid -gt 0) { return $epid }
        Start-Sleep -Milliseconds 500
    }
    return 0
}

# ---------------------------------------------------------------------
# Intent marker. Written by close-app.js via a new broker route
# (POST /api/v1/broker/close-intent). Schema:
#   {
#     "schemaVersion": 1,
#     "intent":        "app-only-close" | "stop-server",
#     "writtenUtc":    "<iso>"
#   }
# Stop-PAXCookbook removes this marker on every Stop. A fresh intent
# within the last <ExpirySec> seconds suppresses the prompt.
# ---------------------------------------------------------------------
function Read-CloseIntent {
    param(
        [int]$ExpirySec = 10,
        $NotBefore = $null
    )
    if (-not (Test-Path -LiteralPath $Script:IntentFile -PathType Leaf)) { return $null }
    try {
        $raw = Get-Content -LiteralPath $Script:IntentFile -Raw -ErrorAction Stop
        $obj = $raw | ConvertFrom-Json -ErrorAction Stop
        $intent = [string]$obj.intent
        # Prefer createdUtc if present (schema v1), fall back to
        # writtenUtc for the initial draft writer. The watcher always
        # enforces its own ExpirySec ceiling regardless of any
        # expiresUtc the writer may have advertised, so a malicious
        # or buggy writer cannot poison native-close detection by
        # advertising a long expiry.
        $names = @($obj.PSObject.Properties.Name)
        $whenStr = $null
        if ($names -contains 'createdUtc') { $whenStr = [string]$obj.createdUtc }
        if (-not $whenStr -and $names -contains 'writtenUtc') { $whenStr = [string]$obj.writtenUtc }
        $when = [datetime]::MinValue
        if ($whenStr) { try { $when = [datetime]::Parse($whenStr, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind) } catch { } }
        $ageSec = ([datetime]::UtcNow - $when).TotalSeconds
        Write-WatcherLog ('Intent marker on disk: intent=' + $intent + ' createdUtc=' + $whenStr + ' ageSec=' + [int]$ageSec)
        if ($ageSec -lt 0 -or $ageSec -gt $ExpirySec) {
            Write-WatcherLog ('Intent marker rejected: outside expiry window (ExpirySec=' + $ExpirySec + ').')
            return $null
        }
        if ($null -ne $NotBefore -and $when -lt $NotBefore) {
            Write-WatcherLog ('Intent marker rejected: createdUtc precedes monitor-start (' + ([datetime]$NotBefore).ToString('o') + ').')
            return $null
        }
        return $intent
    } catch { return $null }
}

function Remove-CloseIntent {
    if (Test-Path -LiteralPath $Script:IntentFile -PathType Leaf) {
        try { Remove-Item -LiteralPath $Script:IntentFile -Force -ErrorAction SilentlyContinue } catch { }
    }
}

# ---------------------------------------------------------------------
# Native WinForms 3-button prompt. Returned values:
#   'Keep'   -- broker stays running; watcher exits.
#   'Stop'   -- invoke Stop-PAXCookbook; watcher exits.
#   'Reopen' -- invoke Start-PAXCookbook (reuse path); watcher re-arms.
#   'Cancel' -- treat as Keep (operator closed the dialog).
# ---------------------------------------------------------------------
function Show-AppWindowClosedPrompt {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    Add-Type -AssemblyName System.Drawing -ErrorAction Stop

    $form              = New-Object System.Windows.Forms.Form
    $form.Text         = 'PAX Cookbook Server is still running'
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $form.MaximizeBox  = $false
    $form.MinimizeBox  = $false
    $form.ShowInTaskbar = $true
    $form.TopMost      = $true
    $form.ClientSize   = New-Object System.Drawing.Size(520, 200)

    $iconPath = Join-Path $Script:AppRoot 'web\images\pax-cookbook-app-icon.ico'
    if (Test-Path -LiteralPath $iconPath -PathType Leaf) {
        try { $form.Icon = New-Object System.Drawing.Icon($iconPath) } catch { }
    }

    $label             = New-Object System.Windows.Forms.Label
    $label.Text        = 'The app window was closed, but the PAX Cookbook Server is still running in the background. Any bakes already running will continue unless you stop the server.'
    $label.AutoSize    = $false
    $label.Location    = New-Object System.Drawing.Point(20, 20)
    $label.Size        = New-Object System.Drawing.Size(480, 90)
    $label.Font        = New-Object System.Drawing.Font('Segoe UI', 10)
    $form.Controls.Add($label)

    $btnKeep           = New-Object System.Windows.Forms.Button
    $btnKeep.Text      = 'Keep server running'
    $btnKeep.Size      = New-Object System.Drawing.Size(160, 36)
    $btnKeep.Location  = New-Object System.Drawing.Point(20, 130)
    $btnKeep.Font      = New-Object System.Drawing.Font('Segoe UI', 9)
    $form.Controls.Add($btnKeep)

    $btnStop           = New-Object System.Windows.Forms.Button
    $btnStop.Text      = 'Stop server'
    $btnStop.Size      = New-Object System.Drawing.Size(150, 36)
    $btnStop.Location  = New-Object System.Drawing.Point(190, 130)
    $btnStop.Font      = New-Object System.Drawing.Font('Segoe UI', 9)
    $form.Controls.Add($btnStop)

    $btnReopen         = New-Object System.Windows.Forms.Button
    $btnReopen.Text    = 'Reopen Cookbook'
    $btnReopen.Size    = New-Object System.Drawing.Size(150, 36)
    $btnReopen.Location = New-Object System.Drawing.Point(350, 130)
    $btnReopen.Font    = New-Object System.Drawing.Font('Segoe UI', 9)
    $form.Controls.Add($btnReopen)

    $script:DialogResult = 'Cancel'
    $btnKeep.Add_Click({ $script:DialogResult = 'Keep'; $form.Close() })
    $btnStop.Add_Click({ $script:DialogResult = 'Stop'; $form.Close() })
    $btnReopen.Add_Click({ $script:DialogResult = 'Reopen'; $form.Close() })

    $form.AcceptButton = $btnKeep
    $form.CancelButton = $btnKeep

    [void]$form.ShowDialog()
    $form.Dispose()
    return $script:DialogResult
}

# ---------------------------------------------------------------------
# Action helpers for the prompt buttons.
# ---------------------------------------------------------------------
function Invoke-StopHelper {
    if (-not (Test-Path -LiteralPath $Script:StopScript -PathType Leaf)) {
        Write-WatcherLog 'Stop button pressed but Stop-PAXCookbook.ps1 is missing.'
        try {
            Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
            [void][System.Windows.Forms.MessageBox]::Show(
                'Stop helper not found. Use the Stop PAX Cookbook Server shortcut.',
                'PAX Cookbook',
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Warning)
        } catch { }
        return
    }
    $pwshExe = (Get-Process -Id $PID).Path
    if (-not $pwshExe) { $pwshExe = 'pwsh.exe' }
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $Script:StopScript)
    try {
        Start-Process -FilePath $pwshExe -ArgumentList $argList -WindowStyle Hidden | Out-Null
        Write-WatcherLog 'Stop helper invoked from watcher prompt.'
    } catch {
        Write-WatcherLog ('Failed to invoke Stop helper: ' + $_.Exception.Message)
    }
}

function Invoke-ReopenLauncher {
    if (-not (Test-Path -LiteralPath $Script:StartScript -PathType Leaf)) {
        Write-WatcherLog 'Reopen pressed but Start-PAXCookbook.ps1 is missing.'
        return $false
    }
    $pwshExe = (Get-Process -Id $PID).Path
    if (-not $pwshExe) { $pwshExe = 'pwsh.exe' }
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $Script:StartScript)
    try {
        Start-Process -FilePath $pwshExe -ArgumentList $argList -WindowStyle Hidden | Out-Null
        Write-WatcherLog 'Reopen launcher invoked from watcher prompt.'
        return $true
    } catch {
        Write-WatcherLog ('Failed to invoke Reopen launcher: ' + $_.Exception.Message)
        return $false
    }
}

# ---------------------------------------------------------------------
# Main monitor loop.
#
# 1. Wait for browser.window.json to appear with a live, validated
#    Edge --app= PID. Bail out if the broker disappears while waiting.
# 2. While Edge is alive and broker is alive, idle.
# 3. When Edge disappears, sleep 3s grace, then:
#       - if broker is no longer alive   -> exit silently
#       - if Stop-in-progress is implied -> exit silently
#       - if Edge is back (race / reopen) -> resume monitoring
#       - if intent marker is fresh      -> consume marker; resume / exit
#       - otherwise                      -> show WinForms prompt
# 4. Prompt -> dispatch:
#       Keep   -> exit watcher (no nag loop)
#       Stop   -> invoke Stop helper; exit watcher
#       Reopen -> invoke Start launcher; wait for new browser.window.json;
#                 resume monitoring
# ---------------------------------------------------------------------
try {
    # The launcher spawns this watcher BEFORE invoking the broker
    # entrypoint in-process, so workspace.lock + the broker pid won't
    # exist for the first few seconds. Poll for the broker to come
    # alive instead of bailing immediately.
    $brokerWaitDeadline = [datetime]::UtcNow.AddSeconds(60)
    $brokerReady = $false
    while ([datetime]::UtcNow -lt $brokerWaitDeadline) {
        if (Test-BrokerAlive) { $brokerReady = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $brokerReady) {
        Write-WatcherLog 'Broker did not come alive within 60s; exiting.'
        return
    }
    Write-WatcherLog 'Broker is alive; entering monitor loop.'

    # Sweep any orphan intent marker from a prior watcher session
    # before we start monitoring. Without this a stale marker (e.g.
    # operator clicked "Close window only" in a previous launch and
    # the watcher was killed before consuming it) would suppress the
    # native-close prompt the next time the operator hits the X.
    if (Test-Path -LiteralPath $Script:IntentFile -PathType Leaf) {
        Write-WatcherLog 'Removing orphan intent marker from prior session.'
        Remove-CloseIntent
    }

    $monitoring = $true
    while ($monitoring) {
        $edgePid = Wait-ForEdgeAppProcess -TimeoutSec 60
        if ($edgePid -le 0) {
            Write-WatcherLog 'Timed out waiting for Edge app-window (or broker gone). Exiting.'
            $monitoring = $false
            break
        }
        Write-WatcherLog ('Monitoring Edge app-window pid=' + $edgePid)
        # Record when monitoring of THIS Edge window began so any
        # intent marker carrying an earlier createdUtc (i.e. left
        # behind by a previous close event in the same launcher
        # session) is ignored. Also sweep any marker present at this
        # instant: by definition it predates the new window.
        $edgeMonitorStart = [datetime]::UtcNow
        if (Test-Path -LiteralPath $Script:IntentFile -PathType Leaf) {
            Write-WatcherLog 'Removing pre-existing intent marker at start of new Edge monitor cycle.'
            Remove-CloseIntent
        }

        # Idle loop: poll both processes.
        $edgeStillAlive = $true
        while ($edgeStillAlive) {
            Start-Sleep -Seconds 2
            if (-not (Test-BrokerAlive)) {
                Write-WatcherLog 'Broker no longer alive; exiting.'
                $monitoring = $false
                break
            }
            $stillEdge = Get-EdgeAppProcessId
            if ($stillEdge -le 0) { $edgeStillAlive = $false }
        }
        if (-not $monitoring) { break }

        Write-WatcherLog 'Edge app-window disappeared; entering grace period.'
        Start-Sleep -Seconds 3

        if (-not (Test-BrokerAlive)) {
            Write-WatcherLog 'Broker exited during grace period; exiting silently.'
            break
        }

        $edgeBack = Get-EdgeAppProcessId
        if ($edgeBack -gt 0) {
            Write-WatcherLog ('Edge reappeared during grace (pid=' + $edgeBack + '); resuming monitor.')
            continue
        }

        $intent = Read-CloseIntent -NotBefore $edgeMonitorStart
        if ($intent) {
            Write-WatcherLog ('Fresh close-intent marker present: ' + $intent + '. Suppressing prompt.')
            Remove-CloseIntent
            if ($intent -eq 'stop-server') {
                # Broker is in the middle of shutting down via /shutdown.
                # No further action required; the next Test-BrokerAlive
                # will fail and the watcher will exit on the next loop.
                continue
            }
            # 'app-only-close' -- chef intentionally closed the window
            # only. Exit the watcher for this close event so there is
            # no nag loop. A future relaunch will spawn a new watcher.
            break
        }

        # Unmanaged native close. Prompt.
        $choice = 'Cancel'
        try {
            $choice = Show-AppWindowClosedPrompt
        } catch {
            Write-WatcherLog ('Prompt failed to render: ' + $_.Exception.Message + '. Exiting.')
            break
        }
        Write-WatcherLog ('Prompt result: ' + $choice)

        # Bare `break` / `continue` inside a `switch` does not target
        # the enclosing `while` loop in PowerShell, so dispatch with
        # explicit conditionals + loop control instead.
        if ($choice -eq 'Reopen') {
            $ok = Invoke-ReopenLauncher
            if ($ok) {
                Start-Sleep -Seconds 2
                continue
            }
            $monitoring = $false
            break
        }
        if ($choice -eq 'Stop') {
            Invoke-StopHelper
            $monitoring = $false
            break
        }
        # Keep server running, Cancel, or any unrecognized result:
        # exit the watcher so there is no nag loop.
        $monitoring = $false
        break
    }
} catch {
    Write-WatcherLog ('Watcher loop unhandled error: ' + $_.Exception.Message)
} finally {
    Remove-WatcherLock
    Write-WatcherLog 'Watcher exiting.'
}

exit 0
