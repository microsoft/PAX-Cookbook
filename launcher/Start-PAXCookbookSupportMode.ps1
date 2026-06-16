#requires -Version 7.4

# =====================================================================
# PAX Cookbook Support Mode launcher (UX-1H10).
#
# Behaviour matrix (per UX-1H10 Part D):
#
#   State                                   Action
#   ---------------------------------       --------------------------------
#   No broker running                       Start broker visible; open URL;
#                                           open logs/status.
#   Broker running, visible console         Reuse; bring console to front;
#                                           open URL; open logs/status.
#   Broker running, hidden console          Reuse; reveal console via Win32
#   with revealable window handle           ShowWindow(SW_RESTORE)+SetFore-
#                                           groundWindow; open URL; open
#                                           logs/status.
#   Broker running, hidden console          Reuse; DO NOT restart; open URL;
#   with NO revealable window handle        open logs/status; show banner
#                                           explaining background mode.
#
# Hard rules:
#   - Never restart an existing broker.
#   - Never elevate.
#   - Never name a specific browser binary.
#   - Never kill arbitrary pwsh processes.
#   - Always open Cookbook URL and logs/status, regardless of state.
#   - Only reveal the verified broker process (PID from workspace.lock).
# =====================================================================

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------
# Process AppUserModelID stamp (doctrine §11.5)
# ---------------------------------------------------------------------
#
# Stamp the canonical AUMID on THIS pwsh.exe process as early as
# possible, before any window is shown. Support Mode opens a console
# window of its own; without the stamp the shell would group it under
# the generic PowerShell tile instead of the PAX Cookbook tile. Same
# AUMID literal as the main launcher and as every PAX shortcut.
try {
    if (-not ('PAXCookbook.Shell.AumidStamp' -as [type])) {
        Add-Type -Namespace 'PAXCookbook.Shell' -Name 'AumidStamp' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);
'@
    }
    $null = [PAXCookbook.Shell.AumidStamp]::SetCurrentProcessExplicitAppUserModelID('PAXCookbook.Local.v1')
} catch {
    # AUMID stamping is a UX hint; failing must not abort startup.
    Write-Host ('  WARN    Could not stamp process AppUserModelID: ' + $_.Exception.Message) -ForegroundColor Yellow
}

# ---------------------------------------------------------------------
# Self-locate (parity with Start-PAXCookbook.ps1 dual-layout discovery)
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
if (-not $Script:AppRoot) {
    Write-Host ''
    Write-Host 'PAX Cookbook Support Mode cannot locate the install tree.' -ForegroundColor Red
    Write-Host ('Launcher dir: ' + $Script:LauncherDir) -ForegroundColor Red
    Write-Host 'Re-run Install-PAXCookbook.ps1 to restore the install tree.' -ForegroundColor Red
    Write-Host ''
    Write-Host 'Press Enter to close this window.'
    [void][System.Console]::ReadLine()
    exit 5
}

$Script:NormalLauncher      = Join-Path $Script:LauncherDir 'Start-PAXCookbook.ps1'
$Script:RuntimeDiscoveryPsm = Join-Path $Script:AppRoot     'launcher\RuntimeDiscovery.psm1'
$Script:BootstrapDir        = Join-Path $env:APPDATA 'PAXCookbook'
$Script:BootstrapFile       = Join-Path $Script:BootstrapDir 'cookbook.bootstrap.json'

if (-not (Test-Path -LiteralPath $Script:NormalLauncher -PathType Leaf)) {
    Write-Host ('Normal launcher missing: ' + $Script:NormalLauncher) -ForegroundColor Red
    Write-Host 'Press Enter to close this window.'
    [void][System.Console]::ReadLine()
    exit 5
}
if (-not (Test-Path -LiteralPath $Script:RuntimeDiscoveryPsm -PathType Leaf)) {
    Write-Host ('RuntimeDiscovery module missing: ' + $Script:RuntimeDiscoveryPsm) -ForegroundColor Red
    Write-Host 'Press Enter to close this window.'
    [void][System.Console]::ReadLine()
    exit 5
}

Import-Module -Name $Script:RuntimeDiscoveryPsm -Force -DisableNameChecking

# ---------------------------------------------------------------------
# Resolve the workspace path the normal launcher would use. We re-use
# the bootstrap pointer the normal launcher already maintains rather
# than duplicating workspace-selection logic.
# ---------------------------------------------------------------------
$Script:WorkspacePath = $null
if (Test-Path -LiteralPath $Script:BootstrapFile -PathType Leaf) {
    try {
        $bs = Get-Content -LiteralPath $Script:BootstrapFile -Raw | ConvertFrom-Json -ErrorAction Stop
        # The bootstrap pointer is authored by Start-PAXCookbook.ps1
        # (see Write-BootstrapPointer) under the field name
        # `workspaceFolderPath`. Reading `workspacePath` returns $null
        # because the field does not exist on disk -- the original
        # Support Mode launcher silently fell through to the
        # "no workspace bootstrap" branch and delegated to the normal
        # launcher, bypassing the reveal-existing-console flow.
        if ($bs -and $bs.PSObject.Properties['workspaceFolderPath']) {
            $Script:WorkspacePath = [string]$bs.workspaceFolderPath
        }
    } catch { }
}

# ---------------------------------------------------------------------
# Win32 reveal helpers (UX-1H10 Part H).
# ---------------------------------------------------------------------
function Initialize-Win32Reveal {
    if ('PaxCookbookSupport.Win32' -as [type]) { return }
    Add-Type -Namespace 'PaxCookbookSupport' -Name 'Win32' -MemberDefinition @'
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(System.IntPtr hWnd, int nCmdShow);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(System.IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsIconic(System.IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindowVisible(System.IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindow(System.IntPtr hWnd);
    public const int SW_HIDE     = 0;
    public const int SW_SHOW     = 5;
    public const int SW_RESTORE  = 9;
    public const int SW_SHOWNORMAL = 1;
'@
}

function Resolve-BrokerWindowHandle {
    # Returns the broker's main window handle (IntPtr) if discoverable
    # within the retry window, otherwise IntPtr.Zero. Tries:
    #   1. workspace.lock consoleWindowHandle field (broker-recorded).
    #   2. Process.MainWindowHandle of the recorded PID, with a brief
    #      retry loop because Process objects can lag behind the
    #      kernel by one or two refreshes after a fresh spawn.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$BrokerPid,
        [Parameter()][string]$WorkspacePath
    )
    Initialize-Win32Reveal

    # 1. workspace.lock consoleWindowHandle (most reliable when set).
    if ($WorkspacePath) {
        $lockPath = Join-Path $WorkspacePath 'Runtime\workspace.lock'
        if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
            try {
                $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -ErrorAction Stop
                $names = @($lock.PSObject.Properties.Name)
                if ($names -contains 'consoleWindowHandle') {
                    $h = [int]$lock.consoleWindowHandle
                    if ($h -ne 0) {
                        $ptr = [System.IntPtr]::new($h)
                        if ([PaxCookbookSupport.Win32]::IsWindow($ptr)) {
                            return $ptr
                        }
                    }
                }
            } catch { }
        }
    }

    # 2. MainWindowHandle retry loop.
    for ($i = 0; $i -lt 5; $i++) {
        try {
            $proc = Get-Process -Id $BrokerPid -ErrorAction SilentlyContinue
            if ($proc) {
                $proc.Refresh()
                $mh = $proc.MainWindowHandle
                if ($mh -ne [System.IntPtr]::Zero) {
                    return $mh
                }
            }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    return [System.IntPtr]::Zero
}

function Invoke-RevealBrokerWindow {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.IntPtr]$Handle)
    Initialize-Win32Reveal
    if ($Handle -eq [System.IntPtr]::Zero) { return $false }
    if (-not [PaxCookbookSupport.Win32]::IsWindow($Handle)) { return $false }
    try {
        if ([PaxCookbookSupport.Win32]::IsIconic($Handle)) {
            [void][PaxCookbookSupport.Win32]::ShowWindowAsync($Handle, [PaxCookbookSupport.Win32]::SW_RESTORE)
        } else {
            [void][PaxCookbookSupport.Win32]::ShowWindowAsync($Handle, [PaxCookbookSupport.Win32]::SW_SHOW)
        }
        [void][PaxCookbookSupport.Win32]::SetForegroundWindow($Handle)
        return $true
    } catch {
        return $false
    }
}

# ---------------------------------------------------------------------
# Support status text-file writer (UX-1H10 Part G).
# ---------------------------------------------------------------------
function Write-SupportStatusFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter()]$Snapshot,
        [Parameter()][string]$RuntimeUrlRedacted,
        [Parameter()][string]$RevealResult,
        [Parameter()][string]$StatusOverride
    )

    $statusDir = Join-Path $WorkspacePath 'Runtime'
    if (-not (Test-Path -LiteralPath $statusDir -PathType Container)) {
        New-Item -ItemType Directory -Path $statusDir -Force | Out-Null
    }
    $statusFile = Join-Path $statusDir 'pax-cookbook-support-status.txt'

    $running    = if ($Snapshot) { 'Running' } else { 'Stopped' }
    if ($StatusOverride) { $running = $StatusOverride }
    $pidStr     = if ($Snapshot) { [string]$Snapshot.BrokerProcessId } else { '<none>' }
    $portStr    = if ($Snapshot) { [string]$Snapshot.BrokerPort }      else { '<none>' }
    $urlStr     = if ($RuntimeUrlRedacted) { $RuntimeUrlRedacted }     else { '<none>' }

    # Pull supplementary fields straight from workspace.lock so the
    # text file reflects exactly what the broker recorded.
    $lockPath = Join-Path $WorkspacePath 'Runtime\workspace.lock'
    $launchMode  = '<unknown>'
    $logsPath    = '<unknown>'
    $appRoot     = '<unknown>'
    $startedUtc  = '<unknown>'
    $handleStr   = '<unknown>'
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
        try {
            $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -ErrorAction Stop
            $names = @($lock.PSObject.Properties.Name)
            if ($names -contains 'launchMode')          { $launchMode = [string]$lock.launchMode }
            if ($names -contains 'logsPath')            { $logsPath   = [string]$lock.logsPath }
            if ($names -contains 'appRoot')             { $appRoot    = [string]$lock.appRoot }
            if ($names -contains 'launchTimestampUtc')  { $startedUtc = [string]$lock.launchTimestampUtc }
            if ($names -contains 'consoleWindowHandle') {
                $h = [int]$lock.consoleWindowHandle
                if ($h -eq 0) { $handleStr = '<none>' } else { $handleStr = ('0x' + $h.ToString('X')) }
            }
        } catch { }
    }

    # Pre-compute the "Console window" cell so the array literal
    # below contains no if-expression in argument context (PowerShell
    # treats inline `if` inside `+` chains as command invocation and
    # can fail at run time).
    if ($RevealResult) {
        $consoleWindowText = $RevealResult
    } else {
        $consoleWindowText = $handleStr
    }

    $lines = @(
        '# PAX Cookbook Support Status',
        ('# Written: ' + (Get-Date -Format 'u')),
        '',
        ('PAX Cookbook status : ' + $running),
        ('Broker URL          : ' + $urlStr),
        ('Broker PID          : ' + $pidStr),
        ('Broker port         : ' + $portStr),
        ('Launch mode         : ' + $launchMode),
        ('Console window      : ' + $consoleWindowText),
        ('Logs path           : ' + $logsPath),
        ('Workspace path      : ' + $WorkspacePath),
        ('App path            : ' + $appRoot),
        ('Started UTC         : ' + $startedUtc),
        '',
        '# Notes',
        '# - If "Console window" reports no handle, the broker is running',
        '#   in the background and cannot be revealed without restarting.',
        '#   Support Mode does NOT restart an existing broker.',
        '# - Stop PAX Cookbook Server will only stop the verified broker PID',
        '#   above; it will not touch unrelated pwsh.exe processes.',
        ''
    )
    [System.IO.File]::WriteAllLines($statusFile, $lines, [System.Text.UTF8Encoding]::new($false))
    return $statusFile
}

function Open-LogsAndStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter(Mandatory)][string]$StatusFile
    )
    $logsDir = Join-Path $WorkspacePath 'Logs'
    if (-not (Test-Path -LiteralPath $logsDir -PathType Container)) {
        New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
    }
    try { Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + $logsDir + '"') | Out-Null } catch { }
    try { Start-Process -FilePath $StatusFile | Out-Null } catch { }
}

# =====================================================================
# Main support-mode flow
# =====================================================================

Write-Host ''
Write-Host '=================================================================='
Write-Host '  PAX Cookbook Support Mode'
Write-Host '=================================================================='
Write-Host ''

if (-not $Script:WorkspacePath -or -not (Test-Path -LiteralPath $Script:WorkspacePath -PathType Container)) {
    Write-Host '  No workspace bootstrap pointer found yet.' -ForegroundColor Yellow
    Write-Host '  Launching normal Start-PAXCookbook to perform first-run setup.' -ForegroundColor Yellow
    Write-Host ''
    & $Script:NormalLauncher
    exit $LASTEXITCODE
}

Write-Host ('  Workspace : ' + $Script:WorkspacePath) -ForegroundColor Cyan

$snap = $null
try { $snap = Test-LiveBrokerAvailable -WorkspacePath $Script:WorkspacePath } catch { $snap = $null }

if (-not $snap) {
    # State 1: no broker running. Start visible (so support has a
    # live console) by invoking the normal launcher in this same
    # already-visible support-mode pwsh window.
    Write-Host '  Broker state: NOT RUNNING.' -ForegroundColor Yellow
    Write-Host '  Starting PAX Cookbook with the broker console visible (support mode)...' -ForegroundColor Yellow
    Write-Host ''

    # Record support-status BEFORE handoff so the file exists for
    # the operator even if the broker fails to come up.
    $statusFile = Write-SupportStatusFile -WorkspacePath $Script:WorkspacePath -Snapshot $null -RuntimeUrlRedacted $null -StatusOverride 'Starting'
    Open-LogsAndStatus -WorkspacePath $Script:WorkspacePath -StatusFile $statusFile

    # Force visible launch mode (Support Mode is intentionally
    # non-hidden). The broker will record launchMode='visible' on
    # the next Write-WorkspaceLock.
    $env:PAX_COOKBOOK_LAUNCH_MODE = 'visible'
    & $Script:NormalLauncher
    exit $LASTEXITCODE
}

# Broker is running. Reuse it - never restart from Support Mode.
Write-Host ('  Broker state: RUNNING (PID ' + $snap.BrokerProcessId + ', port ' + $snap.BrokerPort + ').') -ForegroundColor Green

$reuseToken = $null
try { $reuseToken = Get-BrokerSessionToken -WorkspacePath $Script:WorkspacePath } catch { $reuseToken = $null }
$reuseUrl         = Get-RuntimeUrlFromSnapshot -Snapshot $snap -Token $reuseToken
$reuseUrlRedacted = Format-RedactedRuntimeUrl  -RuntimeUrl $reuseUrl

# Attempt to reveal the broker console window.
$handle  = Resolve-BrokerWindowHandle -BrokerPid $snap.BrokerProcessId -WorkspacePath $Script:WorkspacePath
$revealed = $false
$revealResult = '<no window handle>'
if ($handle -ne [System.IntPtr]::Zero) {
    $revealed = Invoke-RevealBrokerWindow -Handle $handle
    if ($revealed) {
        $revealResult = ('revealed (handle 0x' + ([int64]$handle).ToString('X') + ')')
        Write-Host ('  Revealed broker console window (handle 0x' + ([int64]$handle).ToString('X') + ').') -ForegroundColor Green
    } else {
        $revealResult = ('handle present 0x' + ([int64]$handle).ToString('X') + ' but reveal failed')
        Write-Host '  Broker has a window handle but the reveal call failed.' -ForegroundColor Yellow
    }
} else {
    Write-Host '  Broker is running in the background with no revealable window.' -ForegroundColor Yellow
    Write-Host '  Support Mode will NOT restart the broker. Logs and status are below.' -ForegroundColor Yellow
}

# Write the support status file, open it and the Logs folder.
$statusFile = Write-SupportStatusFile -WorkspacePath $Script:WorkspacePath -Snapshot $snap -RuntimeUrlRedacted $reuseUrlRedacted -RevealResult $revealResult
Open-LogsAndStatus -WorkspacePath $Script:WorkspacePath -StatusFile $statusFile

# Open the Cookbook URL. Acceptable for this to be a second window
# alongside an already-open one. Doctrine §11.4: prefer Edge
# app-window mode; fall back to the OS-registered default browser
# when Edge is not available.
Write-Host ('  Opening Cookbook: ' + $reuseUrlRedacted) -ForegroundColor Green
try {
    Open-CookbookUi -RuntimeUrl $reuseUrl -WorkspacePath $Script:WorkspacePath | Out-Null
} catch {
    Write-Host ('  WARN  Could not open Cookbook UI: ' + $_.Exception.Message) -ForegroundColor Yellow
}

Write-Host ''
Write-Host '  Support Mode actions complete:' -ForegroundColor Cyan
Write-Host ('    * Cookbook URL opened : ' + $reuseUrlRedacted)
Write-Host ('    * Logs folder opened  : ' + (Join-Path $Script:WorkspacePath 'Logs'))
Write-Host ('    * Status file opened  : ' + $statusFile)
if (-not $revealed) {
    Write-Host ''
    Write-Host '  PAX Cookbook is already running in the background. Logs have been opened.' -ForegroundColor Cyan
}
Write-Host ''
Write-Host '  This Support Mode window can be closed.' -ForegroundColor DarkGray
Write-Host ''
exit 0
