#requires -Version 7.4

# =====================================================================
# Stop PAX Cookbook.
#
# Stops the live broker process for the user's current workspace and
# nothing else. Hard rules:
#
#   - Verify PID before terminating. Only kill pwsh.exe processes
#     whose CommandLine references the install tree, AND whose PID
#     matches the workspace.lock brokerProcessId.
#   - Auto-exit on completion. No 'Press Enter to close' prompts;
#     the Stop shortcut launches with -WindowStyle Hidden so the
#     console is invisible -- a Read-Host would block forever.
#   - After the targeted kill, run a defensive orphan sweep over
#     pwsh.exe processes whose CommandLine references this install
#     tree AND names a PAX Cookbook launcher entry-point
#     (Start-PAXCookbook.ps1, Start-PAXCookbookSupportMode.ps1, or
#     broker\Start-Broker.ps1). This catches launchers that aren't
#     recorded in workspace.lock (e.g. after a crash truncated the
#     lock) so a fresh start doesn't fight a half-dead predecessor.
#   - Fatal errors (install tree missing, RuntimeDiscovery module
#     missing) surface via a WinForms MessageBox; otherwise the
#     hidden console would leave the operator with no signal.
#   - Never run elevated. Never escalate.
#   - Never touch unrelated pwsh.exe processes. Always exclude this
#     Stop helper's own PID, any Install-PAXCookbook.ps1 process,
#     and any other Stop-PAXCookbook.ps1 instance.
#   - Best-effort cleanup of workspace.lock + broker.token +
#     browser.window.json sidecars
#     so the next start does not run into an "active lock" refusal.
# =====================================================================

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------
# Self-locate.
# ---------------------------------------------------------------------
$Script:LauncherDir = Split-Path -Parent $PSCommandPath
$Script:AppRoot     = $null
$Script:NativeExeName = 'PAX Cookbook.exe'
$probeParent = Split-Path -Parent $Script:LauncherDir
# Native-first self-locate: the production runtime is the native host, so
# locate the app root by <AppRoot>\bin\PAX Cookbook.exe. Fall back to the
# frozen broker file only if the native exe is absent (older trees).
$probeExe = Join-Path $probeParent (Join-Path 'bin' $Script:NativeExeName)
if (Test-Path -LiteralPath $probeExe -PathType Leaf) {
    $Script:AppRoot = $probeParent
} else {
    $probeAppRoot = Join-Path $probeParent 'app'
    $probeExe2 = Join-Path $probeAppRoot (Join-Path 'bin' $Script:NativeExeName)
    if (Test-Path -LiteralPath $probeExe2 -PathType Leaf) {
        $Script:AppRoot = $probeAppRoot
    } else {
        # Legacy fallback: locate by the frozen parity-oracle broker file.
        $probeBroker = Join-Path $probeParent 'broker\Start-Broker.ps1'
        if (Test-Path -LiteralPath $probeBroker -PathType Leaf) {
            $Script:AppRoot = $probeParent
        } else {
            $probeAppRoot2 = Join-Path $probeParent 'app'
            $probeBroker2 = Join-Path $probeAppRoot2 'broker\Start-Broker.ps1'
            if (Test-Path -LiteralPath $probeBroker2 -PathType Leaf) {
                $Script:AppRoot = $probeAppRoot2
            }
        }
    }
}
function Show-FatalDialog {
    param([string]$Message)
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        [void][System.Windows.Forms.MessageBox]::Show(
            $Message,
            'Stop PAX Cookbook Server',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error)
    } catch {
        # MessageBox unavailable (no WinForms); fall back to stderr.
        Write-Host $Message -ForegroundColor Red
    }
}

if (-not $Script:AppRoot) {
    $msg = 'Stop PAX Cookbook Server cannot locate the install tree. The Stop helper is running from an unexpected directory layout.'
    Write-Host $msg -ForegroundColor Red
    Show-FatalDialog -Message $msg
    exit 5
}

$Script:RuntimeDiscoveryPsm = Join-Path $Script:AppRoot 'launcher\RuntimeDiscovery.psm1'
$Script:NativeExe           = Join-Path $Script:AppRoot (Join-Path 'bin' $Script:NativeExeName)
$Script:BootstrapDir        = Join-Path $env:APPDATA 'PAXCookbook'
$Script:BootstrapFile       = Join-Path $Script:BootstrapDir 'cookbook.bootstrap.json'
$Script:InstallRoot         = Split-Path -Parent $Script:AppRoot
$Script:StopLogFile         = Join-Path $Script:InstallRoot 'install.log'

if (-not (Test-Path -LiteralPath $Script:RuntimeDiscoveryPsm -PathType Leaf)) {
    $msg = 'Stop helper cannot find the RuntimeDiscovery module. Expected file: ' + $Script:RuntimeDiscoveryPsm
    Write-Host $msg -ForegroundColor Red
    Show-FatalDialog -Message $msg
    exit 5
}

Import-Module -Name $Script:RuntimeDiscoveryPsm -Force -DisableNameChecking

function Write-StopLog {
    param([string]$Line)
    $ts = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $entry = '[' + $ts + '] [stop] ' + $Line
    try { Add-Content -LiteralPath $Script:StopLogFile -Value $entry -ErrorAction SilentlyContinue } catch { }
}

# ---------------------------------------------------------------------
# Native-host stop (production runtime).
#
# The production runtime is the native C# host "PAX Cookbook.exe". It
# owns its own WebView window and single-instance lifecycle; it does NOT
# write a workspace.lock brokerProcessId and is NOT a pwsh.exe, so the
# legacy broker-PID / pwsh-sweep paths below never see it. This function
# finds and stops the native host for THIS install tree, using the same
# verify-before-kill discipline as the broker sweep: only PAX Cookbook.exe
# processes whose CommandLine references this install root are touched.
#
# Targeting precedence (any match qualifies, ALL must be under InstallRoot):
#   - CommandLine references this install tree (InstallRoot substring), OR
#   - the process MainModule path is our <AppRoot>\bin\PAX Cookbook.exe.
# When a workspace path is supplied, an additional --workspace <ws>
# substring is used to scope to the workspace the operator asked to stop;
# if the host was launched without a recorded command line we still match
# on the install-tree exe path so a restart never fights a live host.
# ---------------------------------------------------------------------
function Stop-PAXNativeHost {
    param(
        [Parameter(Mandatory)][string]$InstallRoot,
        [string]$NativeExePath,
        [string]$WorkspacePath
    )

    $procs = $null
    try {
        $procs = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'PAX Cookbook.exe'" -ErrorAction SilentlyContinue
    } catch {
        Write-StopLog ('Stop-PAXNativeHost: Win32_Process query failed: ' + $_.Exception.Message)
        return 0
    }
    if (-not $procs) {
        Write-Host '  No native PAX Cookbook host was running.' -ForegroundColor Gray
        return 0
    }

    $exeAbs = $null
    if ($NativeExePath) {
        try { $exeAbs = [System.IO.Path]::GetFullPath($NativeExePath).TrimEnd([char]'\') } catch { $exeAbs = $null }
    }
    $installAbs = $null
    try { $installAbs = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd([char]'\') } catch { $installAbs = $InstallRoot }

    $killed = 0
    foreach ($p in $procs) {
        $targetPid = [int]$p.ProcessId
        $cl = [string]$p.CommandLine
        $exePath = [string]$p.ExecutablePath

        # Ownership: command line OR executable path must sit under our
        # install tree (or be exactly our native exe). This is the same
        # install-root containment check the broker sweep uses; it ensures
        # an unrelated PAX Cookbook.exe from a different install is never
        # touched.
        $ownedByUs = $false
        if ($cl -and ($cl -like ('*' + $installAbs + '*'))) { $ownedByUs = $true }
        if (-not $ownedByUs -and $exePath) {
            try {
                $exePathAbs = [System.IO.Path]::GetFullPath($exePath).TrimEnd([char]'\')
                if ($exeAbs -and $exePathAbs.Equals($exeAbs, [System.StringComparison]::OrdinalIgnoreCase)) { $ownedByUs = $true }
                elseif ($exePathAbs.StartsWith($installAbs + '\', [System.StringComparison]::OrdinalIgnoreCase)) { $ownedByUs = $true }
            } catch { }
        }
        if (-not $ownedByUs) {
            Write-StopLog ('Stop-PAXNativeHost: PID ' + $targetPid + ' not under install tree; skipped.')
            continue
        }

        # Optional workspace scoping: if the operator asked to stop a
        # specific workspace AND the host recorded its --workspace on the
        # command line, only stop the host bound to that workspace. If the
        # command line is unavailable we fall through and stop it (it is
        # still verified to be OUR install's native host).
        if ($WorkspacePath -and $cl -and ($cl -like '*--workspace*')) {
            if ($cl -notlike ('*' + $WorkspacePath + '*')) {
                Write-StopLog ('Stop-PAXNativeHost: PID ' + $targetPid + ' bound to a different workspace; skipped.')
                continue
            }
        }

        Write-Host ('  Stopping native PAX Cookbook host (PID ' + $targetPid + ')...') -ForegroundColor Cyan
        Write-StopLog ('Stop-PAXNativeHost: stopping PID ' + $targetPid + ' CommandLine=' + $cl)
        try {
            Stop-Process -Id $targetPid -Force -ErrorAction Stop
            $killed++
        } catch {
            Write-Host ('    Stop-Process failed for PID ' + $targetPid + ': ' + $_.Exception.Message) -ForegroundColor Red
            Write-StopLog ('Stop-PAXNativeHost: Stop-Process failed for PID ' + $targetPid + ': ' + $_.Exception.Message)
        }
    }

    # Confirm exit of the processes we attempted to stop.
    if ($killed -gt 0) {
        for ($i = 0; $i -lt 20; $i++) {
            Start-Sleep -Milliseconds 100
            $still = $null
            try { $still = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'PAX Cookbook.exe'" -ErrorAction SilentlyContinue } catch { $still = $null }
            $stillOurs = 0
            if ($still) {
                foreach ($s in $still) {
                    $scl = [string]$s.CommandLine
                    $sexe = [string]$s.ExecutablePath
                    if (($scl -and ($scl -like ('*' + $installAbs + '*'))) -or
                        ($sexe -and ($sexe -like ('*' + $installAbs + '*')))) { $stillOurs++ }
                }
            }
            if ($stillOurs -eq 0) { break }
        }
        Write-Host ('  Native host stopped (' + $killed + ' process(es)).') -ForegroundColor Green
        Write-StopLog ('Stop-PAXNativeHost: stopped ' + $killed + ' native host process(es).')
    }
    return $killed
}

# ---------------------------------------------------------------------
# Defensive orphan sweep. Terminates any pwsh.exe processes whose
# CommandLine references this install tree AND a PAX Cookbook
# launcher entry-point. Excludes this Stop helper itself, the
# installer, and any other Stop helper instance.
# ---------------------------------------------------------------------
function Find-OrphanBrokerProcesses {
    param([string]$InstallRoot, [int]$ExcludePid)

    $launcherStart   = '*\launcher\Start-PAXCookbook.ps1*'
    $launcherSupport = '*\launcher\Start-PAXCookbookSupportMode.ps1*'
    $brokerEntry     = '*\broker\Start-Broker.ps1*'
    $installerSelf   = '*Install-PAXCookbook.ps1*'
    $stopSelf        = '*Stop-PAXCookbook.ps1*'

    $list = @()
    $all  = $null
    try { $all = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'pwsh.exe'" -ErrorAction SilentlyContinue } catch { return @() }
    if (-not $all) { return @() }

    foreach ($p in $all) {
        if ([int]$p.ProcessId -eq $ExcludePid) { continue }
        $cl = [string]$p.CommandLine
        if (-not $cl) { continue }
        if ($cl -notlike ('*' + $InstallRoot + '*')) { continue }
        if ($cl -like $installerSelf) { continue }
        if ($cl -like $stopSelf) { continue }
        $isLauncher = ($cl -like $launcherStart) -or ($cl -like $launcherSupport) -or ($cl -like $brokerEntry)
        if (-not $isLauncher) { continue }
        $list += $p
    }
    return $list
}

function Invoke-OrphanSweep {
    param([string]$InstallRoot, [int]$ExcludePid)

    $orphans = Find-OrphanBrokerProcesses -InstallRoot $InstallRoot -ExcludePid $ExcludePid
    if (-not $orphans -or $orphans.Count -eq 0) {
        Write-StopLog 'Defensive sweep: no orphan launcher/broker processes found.'
        return 0
    }
    $killed = 0
    foreach ($o in $orphans) {
        $targetPid = [int]$o.ProcessId
        Write-Host ('  Sweeping orphan launcher PID ' + $targetPid + '.') -ForegroundColor Yellow
        Write-StopLog ('Defensive sweep: killing orphan PID ' + $targetPid + ' CommandLine=' + ([string]$o.CommandLine))
        try {
            Stop-Process -Id $targetPid -Force -ErrorAction Stop
            $killed++
        } catch {
            Write-Host ('    Stop-Process failed for PID ' + $targetPid + ': ' + $_.Exception.Message) -ForegroundColor Red
            Write-StopLog ('Defensive sweep: Stop-Process failed for PID ' + $targetPid + ': ' + $_.Exception.Message)
        }
    }
    if ($killed -gt 0) {
        Write-Host ('  Defensive sweep terminated ' + $killed + ' orphan process(es).') -ForegroundColor Green
    }
    return $killed
}

function Remove-RuntimeSidecars {
    param([string]$WorkspacePath)
    if (-not $WorkspacePath) { return }
    foreach ($f in @('workspace.lock', 'broker.token', 'workspace.lock.acquire', 'browser.window.json', 'app-close-intent.json', 'app-close-watcher.lock')) {
        $p = Join-Path $WorkspacePath ('Runtime\' + $f)
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            try { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
}

# ---------------------------------------------------------------------
# Terminate the sibling app-window watchdog (a pwsh.exe whose
# CommandLine references launcher\Watch-PAXCookbookAppWindow.ps1 +
# this install root). The watchdog already self-exits when the
# broker disappears, but the Stop helper kills the broker via
# Stop-Process -Force; there is a brief window where the watchdog's
# next poll would notice the missing broker and exit on its own,
# but explicitly terminating it here makes "Stop" deterministic and
# guarantees no orphan helper survives the Stop shortcut.
# ---------------------------------------------------------------------
function Stop-PAXAppWindowWatcher {
    param([Parameter(Mandatory)][string]$InstallRoot, [int]$ExcludePid)
    $needleScript = '*Watch-PAXCookbookAppWindow.ps1*'
    $needleRoot   = '*' + $InstallRoot + '*'
    $killed = 0
    $procs  = $null
    try { $procs = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'pwsh.exe'" -ErrorAction SilentlyContinue } catch { return 0 }
    if (-not $procs) { return 0 }
    foreach ($p in $procs) {
        if ([int]$p.ProcessId -eq $ExcludePid) { continue }
        $cl = [string]$p.CommandLine
        if (-not $cl) { continue }
        if ($cl -notlike $needleScript) { continue }
        if ($cl -notlike $needleRoot) { continue }
        try {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
            Write-StopLog ('Stop-PAXAppWindowWatcher: terminated watcher PID ' + $p.ProcessId)
            $killed++
        } catch {
            Write-StopLog ('Stop-PAXAppWindowWatcher: failed to terminate PID ' + $p.ProcessId + ' -- ' + $_.Exception.Message)
        }
    }
    return $killed
}

# ---------------------------------------------------------------------
# Stop the Edge --app= window the appliance launched against this
# install's user-data-dir. The Stop shortcut historically only killed
# the broker process; that left the chromeless Edge window on the
# operator's screen showing a "can't reach the page" error after the
# server died. Closing it explicitly is the natural meaning of "Stop
# PAX Cookbook Server" from the operator's perspective.
#
# Targeting: match ONLY msedge.exe processes whose CommandLine
# references this install's EdgeAppData user-data-dir. The match is
# case-insensitive and uses -like with the InstallRoot\EdgeAppData
# prefix so renderer / GPU / utility subprocesses (which inherit the
# full --user-data-dir argument from the parent) are also caught.
# The operator's general-purpose Edge browser is untouched because
# its user-data-dir lives under %LOCALAPPDATA%\Microsoft\Edge\
# User Data, never under our install root.
# ---------------------------------------------------------------------
function Stop-PAXEdgeAppWindow {
    param([Parameter(Mandatory)][string]$InstallRoot)
    $edgeData = Join-Path $InstallRoot 'EdgeAppData'
    $needle = '*' + $edgeData + '*'
    $killed = 0
    $procs = $null
    try {
        $procs = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'msedge.exe'" -ErrorAction SilentlyContinue
    } catch {
        Write-StopLog ('Stop-PAXEdgeAppWindow: Win32_Process query failed: ' + $_.Exception.Message)
        return 0
    }
    if (-not $procs) {
        Write-StopLog 'Stop-PAXEdgeAppWindow: no msedge.exe processes found.'
        return 0
    }
    foreach ($p in $procs) {
        if (-not $p.CommandLine) { continue }
        if ($p.CommandLine -notlike $needle) { continue }
        try {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
            Write-StopLog ('Stop-PAXEdgeAppWindow: terminated PID ' + $p.ProcessId + ' (Edge --app= bound to ' + $edgeData + ')')
            $killed++
        } catch {
            Write-StopLog ('Stop-PAXEdgeAppWindow: failed to terminate PID ' + $p.ProcessId + ' -- ' + $_.Exception.Message)
        }
    }
    if ($killed -gt 0) {
        Write-StopLog ('Stop-PAXEdgeAppWindow: ' + $killed + ' Edge --app= process(es) terminated.')
    } else {
        Write-StopLog 'Stop-PAXEdgeAppWindow: no matching Edge processes (CommandLine did not reference our EdgeAppData path).'
    }
    return $killed
}

# ---------------------------------------------------------------------
# Resolve workspace via the bootstrap pointer.
# ---------------------------------------------------------------------
$Script:WorkspacePath = $null
if (Test-Path -LiteralPath $Script:BootstrapFile -PathType Leaf) {
    try {
        $bs = Get-Content -LiteralPath $Script:BootstrapFile -Raw | ConvertFrom-Json -ErrorAction Stop
        if ($bs) {
            # Canonical bootstrap key is workspaceFolderPath (written
            # by Install-PAXCookbook.ps1, Start-PAXCookbook.ps1,
            # Start-PAXCookbookSupportMode.ps1, and Start-Broker.ps1).
            # Fall back to a legacy workspacePath property if present
            # so an older bootstrap file written before the rename
            # still drives the targeted broker-PID + sidecar-cleanup
            # paths. PSObject.Properties indexer is strict-mode safe.
            $candidate = $null
            $propFolder = $bs.PSObject.Properties['workspaceFolderPath']
            if ($propFolder -and $propFolder.Value) {
                $candidate = [string]$propFolder.Value
            } else {
                $propLegacy = $bs.PSObject.Properties['workspacePath']
                if ($propLegacy -and $propLegacy.Value) {
                    $candidate = [string]$propLegacy.Value
                }
            }
            if ($candidate) { $Script:WorkspacePath = $candidate }
        }
    } catch { }
}

Write-Host ''
Write-Host '=================================================================='
Write-Host '  Stop PAX Cookbook Server'
Write-Host '=================================================================='
Write-Host ''
Write-StopLog 'Stop helper invoked.'

# Close the Edge --app= window first so the operator gets immediate
# visual confirmation that "Stop" took effect; the broker tear-down
# and orphan sweep then run in the background of this hidden console.
$edgeClosed = Stop-PAXEdgeAppWindow -InstallRoot $Script:InstallRoot
if ($edgeClosed -gt 0) {
    Write-Host ('  Closed ' + $edgeClosed + ' PAX Cookbook Edge window process(es).') -ForegroundColor Green
} else {
    Write-Host '  No PAX Cookbook Edge window was running.' -ForegroundColor Gray
}

if (-not $Script:WorkspacePath -or -not (Test-Path -LiteralPath $Script:WorkspacePath -PathType Container)) {
    Write-Host '  No workspace bootstrap pointer found.' -ForegroundColor Yellow
    Write-Host '  Stopping any native host for this install and running defensive sweep.' -ForegroundColor Yellow
    Write-StopLog 'No workspace bootstrap pointer; native-host stop + defensive sweep only.'
    [void](Stop-PAXNativeHost -InstallRoot $Script:InstallRoot -NativeExePath $Script:NativeExe)
    [void](Stop-PAXAppWindowWatcher -InstallRoot $Script:InstallRoot -ExcludePid $PID)
    [void](Invoke-OrphanSweep -InstallRoot $Script:InstallRoot -ExcludePid $PID)
    Write-StopLog 'Stop helper exiting (no workspace).'
    exit 0
}

Write-Host ('  Workspace : ' + $Script:WorkspacePath) -ForegroundColor Cyan
Write-StopLog ('Workspace : ' + $Script:WorkspacePath)

# ---------------------------------------------------------------------
# Native host (production runtime) FIRST. The native C# host is the
# process that actually serves this workspace; stop it before the
# legacy broker-PID handling (which is a no-op for native runs because
# the native host writes no workspace.lock brokerProcessId).
# ---------------------------------------------------------------------
[void](Stop-PAXNativeHost -InstallRoot $Script:InstallRoot -NativeExePath $Script:NativeExe -WorkspacePath $Script:WorkspacePath)

# ---------------------------------------------------------------------
# Legacy broker handling. Read workspace.lock snapshot. The recorded
# PID is only acted on if it points at a live pwsh.exe under our install
# tree; otherwise we fall through to the defensive sweep, which covers
# orphan launchers that aren't recorded in workspace.lock. For native
# runs this whole block is a no-op (no workspace.lock is written).
# ---------------------------------------------------------------------
$snap = $null
try { $snap = Read-WorkspaceLockSnapshot -WorkspacePath $Script:WorkspacePath } catch { $snap = $null }

if (-not $snap) {
    Write-Host '  workspace.lock not present -- no recorded broker PID.' -ForegroundColor Green
    Write-StopLog 'workspace.lock not present.'
} else {
    $brokerPid = [int]$snap.BrokerProcessId
    if ($brokerPid -le 0) {
        Write-Host '  workspace.lock contains no usable brokerProcessId.' -ForegroundColor Yellow
        Write-StopLog 'workspace.lock has no usable brokerProcessId.'
    } else {
        # Verify PID belongs to our broker process. The proven pattern
        # is: pwsh.exe + CommandLine LIKE install tree.
        $ownedByUs = $false
        $cmdLine   = ''
        try {
            $proc = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $brokerPid) -ErrorAction SilentlyContinue
            if ($proc) {
                $cmdLine = [string]$proc.CommandLine
                if ($proc.Name -eq 'pwsh.exe' -and $cmdLine -and $cmdLine -like ('*' + $Script:InstallRoot + '*')) {
                    $ownedByUs = $true
                }
            }
        } catch { }

        if (-not $ownedByUs) {
            Write-Host ('  workspace.lock PID ' + $brokerPid + ' does not match a PAX Cookbook broker process.') -ForegroundColor Yellow
            Write-Host '  Refusing targeted kill; defensive sweep will follow.' -ForegroundColor Yellow
            Write-StopLog ('workspace.lock PID ' + $brokerPid + ' not owned by us; targeted kill skipped.')
        } else {
            Write-Host ('  Verified broker PID ' + $brokerPid + ' (pwsh.exe under ' + $Script:InstallRoot + ').') -ForegroundColor Green
            Write-Host '  Stopping PAX Cookbook...' -ForegroundColor Cyan
            Write-StopLog ('Stopping verified broker PID ' + $brokerPid + '.')

            $stopOk = $true
            try {
                Stop-Process -Id $brokerPid -Force -ErrorAction Stop
            } catch {
                Write-Host ('  Stop-Process failed: ' + $_.Exception.Message) -ForegroundColor Red
                Write-StopLog ('Stop-Process failed for PID ' + $brokerPid + ': ' + $_.Exception.Message)
                $stopOk = $false
            }

            if ($stopOk) {
                # Wait briefly to confirm exit.
                $stopped = $false
                for ($i = 0; $i -lt 20; $i++) {
                    Start-Sleep -Milliseconds 100
                    $still = $null
                    try { $still = Get-Process -Id $brokerPid -ErrorAction SilentlyContinue } catch { $still = $null }
                    if (-not $still) { $stopped = $true; break }
                }

                if ($stopped) {
                    Write-Host '  Process terminated cleanly.' -ForegroundColor Green
                    Write-StopLog ('Broker PID ' + $brokerPid + ' terminated.')
                } else {
                    Write-Host ('  Process ' + $brokerPid + ' still present after 2s; sweep will catch stragglers.') -ForegroundColor Yellow
                    Write-StopLog ('Broker PID ' + $brokerPid + ' still present after 2s; relying on sweep.')
                }
            }
        }
    }
}

# Always run the defensive sweep + sidecar cleanup, regardless of
# which branch above ran. The sweep is the catch-all for orphan
# launchers that workspace.lock no longer points at.
Remove-RuntimeSidecars -WorkspacePath $Script:WorkspacePath
[void](Stop-PAXAppWindowWatcher -InstallRoot $Script:InstallRoot -ExcludePid $PID)
[void](Invoke-OrphanSweep -InstallRoot $Script:InstallRoot -ExcludePid $PID)

Write-Host ''
Write-Host '  PAX Cookbook has been stopped.' -ForegroundColor Green
Write-StopLog 'Stop helper exiting (success).'
exit 0
