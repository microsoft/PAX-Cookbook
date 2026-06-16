#requires -Version 7.4

# PAX Cookbook launcher (native runtime).
#
# ARCHITECTURE: PAX Cookbook's production runtime is the native C#
# host "PAX Cookbook.exe" (Kestrel/minimal-API + WebView shell). This
# launcher is a THIN workspace-bootstrap front end ONLY. It is launcher/
# bootstrap tooling -- NOT the product runtime, and it does NOT host the
# legacy PowerShell broker.
#
# The PowerShell broker (app\broker\Start-Broker.ps1) is a FROZEN parity
# oracle / dev reference. It is intentionally NOT started here. See the
# launch step at the bottom of this file.
#
# Responsibilities:
#   1. Refuse to run on Windows PowerShell 5.x or on pwsh < 7.4.
#   2. Locate or first-run-create the workspace bootstrap pointer at
#      %APPDATA%/PAXCookbook/cookbook.bootstrap.json.
#   3. Validate the chosen workspace folder against the workspace
#      ownership requirements and surface any soft warnings.
#   4. Start the native host:
#        "<AppRoot>\bin\PAX Cookbook.exe" --workspace <validated> --approot <AppRoot>
#      and exit. The native host owns port allocation, token generation,
#      its own WebView window, single-instance reuse, SQLite, engine/
#      script integrity verification, and the single gated startCook
#      execution route. This launcher adds NO execution channel.

[CmdletBinding()]
param()

# =====================================================================
# 0. Hard runtime preconditions
# =====================================================================

# Belt-and-suspenders against the rare path where #requires is bypassed
# (e.g. invocation via Invoke-Expression of file contents).
if ($PSVersionTable.PSVersion.Major -lt 7 -or
    ($PSVersionTable.PSVersion.Major -eq 7 -and $PSVersionTable.PSVersion.Minor -lt 4)) {
    Write-Host ''
    Write-Host 'PAX Cookbook requires PowerShell 7.4 or newer.' -ForegroundColor Red
    Write-Host ('Detected: PowerShell ' + $PSVersionTable.PSVersion.ToString()) -ForegroundColor Red
    Write-Host 'Install PowerShell 7.4 LTS from https://aka.ms/PSWindows and re-run launcher\Start-PAXCookbook.ps1.' -ForegroundColor Red
    Write-Host ''
    exit 1
}

$ErrorActionPreference = 'Stop'

# =====================================================================
# 0a. Process AppUserModelID stamp (doctrine §11.5)
# =====================================================================
#
# Stamp the canonical AUMID on THIS pwsh.exe process as early as
# possible, before any window is shown. Shell groups taskbar buttons
# and jump lists by AUMID; without the stamp the launcher pwsh.exe
# would group under the generic PowerShell tile and would NOT match
# the chef-hat shortcut tile in the operator's taskbar/Start.
#
# The same AUMID literal -- 'PAXCookbook.Local.v1' -- is also stamped
# on every PAX-owned shortcut by Install-PAXCookbook.ps1.
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

# Discover the pwsh.exe path this launcher is running under. Recorded
# only for startup diagnostics / runtime parity reporting; the native
# host is a self-contained EXE and is not spawned via pwsh.
$Script:PwshExePath = (Get-Process -Id $PID).Path
if (-not $Script:PwshExePath -or -not (Test-Path -LiteralPath $Script:PwshExePath -PathType Leaf)) {
    $cmd = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if ($cmd) { $Script:PwshExePath = $cmd.Source }
}
if (-not $Script:PwshExePath -or -not (Test-Path -LiteralPath $Script:PwshExePath -PathType Leaf)) {
    Write-Host 'Cannot locate pwsh.exe. Verify PowerShell 7.4+ installation.' -ForegroundColor Red
    exit 1
}

# Resolve paths relative to this script.
#
# The launcher script supports two on-disk layouts:
#
#   (a) SOURCE TREE (dev / verification harnesses)
#       <repo>\launcher\Start-PAXCookbook.ps1
#       <repo>\app\bin\PAX Cookbook.exe
#       <repo>\app\VERSION.json
#
#   (b) INSTALLED TREE (user-scope appliance after Install-PAXCookbook)
#       <InstallRoot>\App\launcher\Start-PAXCookbook.ps1
#       <InstallRoot>\App\bin\PAX Cookbook.exe
#       <InstallRoot>\App\VERSION.json
#       Phase S shortcuts target this launcher path.
#
# In both shapes the native host is reachable at
# "<AppRoot>\bin\PAX Cookbook.exe". Probe both candidates and pick the
# first match; refuse to run if neither is present.
$Script:LauncherDir = Split-Path -Parent $PSCommandPath
$Script:AppRoot     = $null
$Script:NativeExeName = 'PAX Cookbook.exe'

$probeParent  = Split-Path -Parent $Script:LauncherDir
$probeExe     = Join-Path $probeParent (Join-Path 'bin' $Script:NativeExeName)
if (Test-Path -LiteralPath $probeExe -PathType Leaf) {
    # Layout (b): launcher sits under <AppRoot>\launcher\.
    $Script:AppRoot = $probeParent
} else {
    # Layout (a): launcher is at <repo>\launcher\; AppRoot = <repo>\app.
    $probeAppRoot = Join-Path $probeParent 'app'
    $probeExe2    = Join-Path $probeAppRoot (Join-Path 'bin' $Script:NativeExeName)
    if (Test-Path -LiteralPath $probeExe2 -PathType Leaf) {
        $Script:AppRoot = $probeAppRoot
    }
}
if (-not $Script:AppRoot) {
    Write-Host ''
    Write-Host 'Cannot locate the native host (PAX Cookbook.exe) relative to this launcher.' -ForegroundColor Red
    Write-Host ('  Launcher dir: ' + $Script:LauncherDir) -ForegroundColor Red
    Write-Host  '  Probed:' -ForegroundColor Red
    Write-Host ('    ' + (Join-Path $probeParent (Join-Path 'bin' $Script:NativeExeName))) -ForegroundColor Red
    Write-Host ('    ' + (Join-Path (Join-Path $probeParent 'app') (Join-Path 'bin' $Script:NativeExeName))) -ForegroundColor Red
    Write-Host 'Re-run Install-PAXCookbook.ps1 to restore the install tree.' -ForegroundColor Red
    exit 5
}

$Script:NativeExe           = Join-Path $Script:AppRoot (Join-Path 'bin' $Script:NativeExeName)
$Script:VersionFile         = Join-Path $Script:AppRoot 'VERSION.json'
$Script:PaxScriptPath       = Join-Path $Script:AppRoot 'resources\pax\PAX_Purview_Audit_Log_Processor.ps1'
$Script:BootstrapDir        = Join-Path $env:APPDATA 'PAXCookbook'
$Script:BootstrapFile       = Join-Path $Script:BootstrapDir 'cookbook.bootstrap.json'

# =====================================================================
# 1. Workspace location predicates
# =====================================================================

function Test-PathHasPrefix {
    param([string]$Path, [string]$Prefix)
    if (-not $Prefix) { return $false }
    $p = $Prefix.TrimEnd('\').TrimEnd('/')
    if ([string]::IsNullOrEmpty($p)) { return $false }
    if ($Path.Length -lt $p.Length) { return $false }
    if (-not $Path.Substring(0, $p.Length).Equals($p, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
    if ($Path.Length -eq $p.Length) { return $true }
    $next = $Path[$p.Length]
    return ($next -eq '\' -or $next -eq '/')
}

function Test-IsUncOrMappedUnc {
    param([string]$Path)
    if ($Path.StartsWith('\\')) { return $true }
    try {
        $root = [System.IO.Path]::GetPathRoot($Path)
        if (-not $root) { return $false }
        $driveLetter = $root.TrimEnd('\').TrimEnd(':')
        if ($driveLetter.Length -eq 1) {
            $psd = Get-PSDrive -Name $driveLetter -PSProvider FileSystem -ErrorAction SilentlyContinue
            if ($psd -and $psd.DisplayRoot -and $psd.DisplayRoot.StartsWith('\\')) { return $true }
        }
    } catch { }
    return $false
}

function Test-IsWslPath {
    param([string]$Path)
    return ($Path -like '\\wsl$\*') -or ($Path -like '\\wsl.localhost\*')
}

function Get-PathDriveType {
    param([string]$Path)
    try {
        $root = [System.IO.Path]::GetPathRoot($Path)
        if (-not $root) { return 'Unknown' }
        $drive = [System.IO.DriveInfo]::new($root)
        return $drive.DriveType.ToString()
    } catch { return 'Unknown' }
}

function Test-IsOneDrivePath {
    param([string]$Path)

    # Layer 1: environment variables maintained by the OneDrive client.
    $envRoots = @($env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer) |
        Where-Object { $_ -and $_.Trim() }
    foreach ($root in $envRoots) {
        if (Test-PathHasPrefix -Path $Path -Prefix $root) { return $true }
    }

    # Layer 2: registry — every configured account exposes its sync-root
    # folder as the UserFolder REG_SZ value.
    try {
        $accounts = Get-ChildItem -LiteralPath 'HKCU:\Software\Microsoft\OneDrive\Accounts' -ErrorAction SilentlyContinue
        foreach ($acct in $accounts) {
            $uf = (Get-ItemProperty -LiteralPath $acct.PSPath -Name 'UserFolder' -ErrorAction SilentlyContinue).UserFolder
            if ($uf -and (Test-PathHasPrefix -Path $Path -Prefix $uf)) { return $true }
        }
    } catch { }

    # Layer 3: desktop.ini shell-integration marker. OneDrive-managed
    # folders carry a desktop.ini whose IconResource/InfoTip values
    # reference the OneDrive client.
    try {
        $desktopIni = Join-Path $Path 'desktop.ini'
        if (Test-Path -LiteralPath $desktopIni -PathType Leaf) {
            $iniText = Get-Content -LiteralPath $desktopIni -Raw -ErrorAction SilentlyContinue
            if ($iniText -and ($iniText -match '(?i)OneDrive')) { return $true }
        }
    } catch { }

    # Layer 4: sync-conflict residue. ~$*.tmp files at the workspace
    # root indicate active Office-style sync coordination of the kind
    # OneDrive produces.
    try {
        $conflict = Get-ChildItem -LiteralPath $Path -Filter '~$*.tmp' -File -Force -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($conflict) { return $true }
    } catch { }

    return $false
}

function Test-IsSharePointSyncedPath {
    param([string]$Path)

    # SharePoint sync is implemented by the OneDrive client under the
    # Business1 (and Business2, Business3, …) settings folders. Each
    # configured library sync root surfaces as a drive-letter-rooted path
    # inside the per-account *.ini files.
    $accountsDir = Join-Path $env:LOCALAPPDATA 'Microsoft\OneDrive\settings'
    if (-not (Test-Path -LiteralPath $accountsDir -PathType Container)) { return $false }
    $businessDirs = Get-ChildItem -LiteralPath $accountsDir -Directory -Filter 'Business*' -ErrorAction SilentlyContinue
    foreach ($bd in $businessDirs) {
        $iniFiles = Get-ChildItem -LiteralPath $bd.FullName -Filter '*.ini' -File -ErrorAction SilentlyContinue
        foreach ($ini in $iniFiles) {
            $lines = Get-Content -LiteralPath $ini.FullName -ErrorAction SilentlyContinue
            foreach ($line in $lines) {
                $candidates = [regex]::Matches($line, '([A-Za-z]:\\[^"|\r\n=]+)')
                foreach ($m in $candidates) {
                    $candidate = $m.Value.TrimEnd().TrimEnd('\')
                    if ($candidate.Length -gt 3 -and (Test-PathHasPrefix -Path $Path -Prefix $candidate)) {
                        return $true
                    }
                }
            }
        }
    }
    return $false
}

function Test-IsDfsPath {
    param([string]$Path)
    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if ($item.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
            $fsutilOutput = & cmd /c "fsutil reparsepoint query `"$Path`"" 2>&1
            $reparseText = ($fsutilOutput | Out-String)
            if ($reparseText -match 'Microsoft DFS' -or $reparseText -match 'IO_REPARSE_TAG_DFS') {
                return $true
            }
        }
        return $false
    } catch { return $false }
}

function Test-IsWriteable {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }
    $probe = Join-Path $Path ('.cookbook-write-probe-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $null = New-Item -ItemType File -Path $probe -Force -ErrorAction Stop
        Remove-Item -LiteralPath $probe -Force -ErrorAction Stop
        return $true
    } catch {
        if (Test-Path -LiteralPath $probe -ErrorAction SilentlyContinue) {
            try { Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue } catch { }
        }
        return $false
    }
}

function Test-WorkspaceHardRequirements {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return @{ Ok = $false; Reason = 'Workspace path is empty.'; RemovableWarning = $false }
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return @{ Ok = $false; Reason = ('Workspace path does not exist or is not a folder: ' + $Path); RemovableWarning = $false }
    }

    # §3.1.7 — WSL paths (also avoids odd GetPathRoot behavior on \\wsl$\).
    if (Test-IsWslPath -Path $Path) {
        return @{ Ok = $false; Reason = ('WSL paths are not supported as a workspace (\\wsl$\... or \\wsl.localhost\...): ' + $Path); RemovableWarning = $false }
    }

    # §3.1.2 — UNC or UNC-backed mapped drive.
    if (Test-IsUncOrMappedUnc -Path $Path) {
        return @{ Ok = $false; Reason = ('UNC and UNC-backed mapped-drive workspaces are not supported: ' + $Path); RemovableWarning = $false }
    }

    # §3.1.1 — drive type.
    $dt = Get-PathDriveType -Path $Path
    $removable = $false
    switch ($dt) {
        'Network'         { return @{ Ok = $false; Reason = ('Network drives are not supported as a workspace: ' + $Path); RemovableWarning = $false } }
        'CDRom'           { return @{ Ok = $false; Reason = ('CD-ROM drives are not writable workspaces: ' + $Path); RemovableWarning = $false } }
        'Ram'             { return @{ Ok = $false; Reason = ('RAM drives are not supported as a workspace: ' + $Path); RemovableWarning = $false } }
        'NoRootDirectory' { return @{ Ok = $false; Reason = ('Drive root is invalid: ' + $Path); RemovableWarning = $false } }
        'Unknown'         { return @{ Ok = $false; Reason = ('Drive type for the workspace path could not be determined: ' + $Path); RemovableWarning = $false } }
        'Removable'       { $removable = $true }
        'Fixed'           { }
        default           { return @{ Ok = $false; Reason = ('Unsupported drive type "' + $dt + '" for workspace path: ' + $Path); RemovableWarning = $false } }
    }

    # §3.1.3 — OneDrive scope.
    if (Test-IsOneDrivePath -Path $Path) {
        return @{ Ok = $false; Reason = ('OneDrive-synced paths are not supported as a workspace: ' + $Path); RemovableWarning = $false }
    }

    # §3.1.4 — SharePoint scope (same client; Business* accounts).
    if (Test-IsSharePointSyncedPath -Path $Path) {
        return @{ Ok = $false; Reason = ('SharePoint-synced paths are not supported as a workspace: ' + $Path); RemovableWarning = $false }
    }

    # §3.1.5 — DFS-N reparse point.
    if (Test-IsDfsPath -Path $Path) {
        return @{ Ok = $false; Reason = ('DFS-N namespace paths are not supported as a workspace: ' + $Path); RemovableWarning = $false }
    }

    # §3.1.6 — round-trip writability.
    if (-not (Test-IsWriteable -Path $Path)) {
        return @{ Ok = $false; Reason = ('Workspace path is not writable by the current user: ' + $Path); RemovableWarning = $false }
    }

    return @{ Ok = $true; Reason = ''; RemovableWarning = $removable }
}

function Get-WorkspaceSoftWarnings {
    param([string]$Path)
    $warnings = @()

    # §3.2.1 — temp-ish location.
    $tempRoots = @($env:TEMP, $env:TMP) | Where-Object { $_ -and $_.Trim() }
    $matchedTemp = $false
    foreach ($tp in $tempRoots) {
        if (Test-PathHasPrefix -Path $Path -Prefix $tp) { $matchedTemp = $true; break }
    }
    if (-not $matchedTemp -and ($Path -match '[\\/][Tt][Ee][Mm][Pp]([\\/]|$)')) {
        $matchedTemp = $true
    }
    if ($matchedTemp) {
        $warnings += 'Workspace appears to be in a temporary location. Cooks, recipes, and audit history may be lost if the OS cleans this folder.'
    }

    # §3.2.2 — long path.
    if ($Path.Length -gt 200) {
        $warnings += 'Long workspace path may cause issues with deeply nested cook output. Consider moving the workspace closer to a drive root.'
    }

    # §3.2.3 — low free space.
    try {
        $root = [System.IO.Path]::GetPathRoot($Path)
        $drv = [System.IO.DriveInfo]::new($root)
        if ($drv.IsReady -and $drv.AvailableFreeSpace -lt 1GB) {
            $warnings += 'Workspace drive is low on free space. Cook history and SQLite growth may fail.'
        }
    } catch { }

    return $warnings
}

# =====================================================================
# 2. Canonical workspace resolution
# =====================================================================
#
# The appliance owns its workspace location. The user is never asked
# where to put it. First-run creates it. Recovery from a missing or
# invalid recorded workspace silently self-heals back to the canonical
# default. There is no folder-picker dialog and no text prompt in any
# normal or recovery launch flow.

function Get-CanonicalWorkspacePath {
    $lad = [Environment]::GetFolderPath('LocalApplicationData')
    if ([string]::IsNullOrWhiteSpace($lad)) {
        throw 'LocalApplicationData folder cannot be resolved on this machine.'
    }
    return (Join-Path $lad 'PAXCookbook\Workspace')
}

function Resolve-CanonicalWorkspace {
    # Returns the validated absolute canonical workspace path or throws
    # with a concise reason. Creates the directory if missing. Never
    # prompts. Lifecycle transitions are explicit:
    #   computed -> created-if-needed -> hard-checked -> soft-warned -> returned
    $canonical = Get-CanonicalWorkspacePath

    if (-not (Test-Path -LiteralPath $canonical -PathType Container)) {
        try {
            New-Item -ItemType Directory -Path $canonical -Force -ErrorAction Stop | Out-Null
        } catch {
            throw ('Cannot create canonical workspace folder ' + $canonical + ': ' + $_.Exception.Message)
        }
    }

    $hard = Test-WorkspaceHardRequirements -Path $canonical
    if (-not $hard.Ok) {
        throw ('Canonical workspace folder is unusable (' + $hard.Reason + '): ' + $canonical)
    }

    if ($hard.RemovableWarning) {
        Write-Host ('  WARN    Removable drive: ' + $canonical) -ForegroundColor Yellow
    }
    $soft = Get-WorkspaceSoftWarnings -Path $canonical
    foreach ($w in $soft) { Write-Host ('  WARN    ' + $w) -ForegroundColor Yellow }

    return $canonical
}

# =====================================================================
# 3. Bootstrap pointer I/O
# =====================================================================

function Read-BootstrapPointer {
    if (-not (Test-Path -LiteralPath $Script:BootstrapFile -PathType Leaf)) { return $null }
    try {
        $raw = Get-Content -LiteralPath $Script:BootstrapFile -Raw -ErrorAction Stop
        return $raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        # Phase W (clean-machine hardening): preserve the corrupt file
        # under a sibling name with a UTC timestamp so the operator can
        # inspect / file / triage it. Silent overwrite on the next
        # successful workspace pick would erase the only evidence of
        # what the file contained. The rename is best-effort; failure
        # to rename does NOT block first-run recovery.
        $ts = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ', [System.Globalization.CultureInfo]::InvariantCulture)
        $brokenPath = $Script:BootstrapFile + '.broken-' + $ts
        $renamed = $false
        try {
            Move-Item -LiteralPath $Script:BootstrapFile -Destination $brokenPath -Force -ErrorAction Stop
            $renamed = $true
        } catch {
            # Intentionally swallow - we still surface the parse error
            # below; first-run recovery proceeds either way.
        }
        Write-Host ('Bootstrap pointer file is unreadable; treating as missing: ' + $_.Exception.Message) -ForegroundColor Yellow
        if ($renamed) {
            Write-Host ('  Preserved as: ' + $brokenPath) -ForegroundColor Yellow
        }
        return $null
    }
}

function Write-BootstrapPointer {
    param([string]$WorkspacePath)
    if (-not (Test-Path -LiteralPath $Script:BootstrapDir -PathType Container)) {
        New-Item -ItemType Directory -Path $Script:BootstrapDir -Force | Out-Null
    }

    # Doctrine §11.2: preserve the broker-owned stable-port fields
    # (selectedBrokerPort, preferredBrokerPort, portRangeStart,
    # portRangeEnd) when the launcher rewrites the pointer on a
    # workspace handoff. Without this merge, the launcher would
    # clobber the selected port every launch and the broker would
    # always start with a cold scan of the full range. The launcher
    # never authors selectedBrokerPort; that field is owned by the
    # broker. preferredBrokerPort, portRangeStart, portRangeEnd are
    # written by both for visibility but the canonical values live in
    # Start-Broker.ps1.
    $existing = $null
    try { $existing = Read-BootstrapPointer } catch { $existing = $null }

    $selectedPort  = $null
    if ($existing -and $existing.PSObject.Properties['selectedBrokerPort']) {
        try { $selectedPort = [int]$existing.selectedBrokerPort } catch { $selectedPort = $null }
    }

    $obj = [ordered]@{
        schemaVersion       = 2
        workspaceFolderPath = $WorkspacePath
        lastUsed            = (Get-Date).ToUniversalTime().ToString('o')
        preferredBrokerPort = 17654
        portRangeStart      = 17654
        portRangeEnd        = 17664
    }
    if ($null -ne $selectedPort) {
        $obj['selectedBrokerPort'] = $selectedPort
    }

    $json = $obj | ConvertTo-Json -Depth 5
    $tmp = $Script:BootstrapFile + '.tmp'
    Set-Content -LiteralPath $tmp -Value $json -Encoding utf8 -NoNewline -Force
    Move-Item -LiteralPath $tmp -Destination $Script:BootstrapFile -Force
}

# =====================================================================
# 4. Main launcher flow
# =====================================================================

Write-Host ''
Write-Host '================================================================'
Write-Host '  PAX Cookbook  -  launcher (native runtime)'
Write-Host '================================================================'
Write-Host ('  PowerShell: ' + $PSVersionTable.PSVersion.ToString())
Write-Host ('  pwsh.exe:   ' + $Script:PwshExePath)
Write-Host ('  Native host:' + ' ' + $Script:NativeExe)
Write-Host  '  Port:       <assigned by native host on startup>'
Write-Host  '  Token:      <generated by native host on startup>'
Write-Host ''

$bootstrap = Read-BootstrapPointer
$selected = $null
$selfHealReason = $null

if ($bootstrap -and $bootstrap.workspaceFolderPath) {
    $candidate = [string]$bootstrap.workspaceFolderPath
    if (Test-Path -LiteralPath $candidate -PathType Container) {
        $hard = Test-WorkspaceHardRequirements -Path $candidate
        if ($hard.Ok) {
            $selected = $candidate
            Write-Host ('  Using existing workspace: ' + $selected) -ForegroundColor Green
            $soft = Get-WorkspaceSoftWarnings -Path $selected
            foreach ($w in $soft) { Write-Host ('  WARN    ' + $w) -ForegroundColor Yellow }
            if ($hard.RemovableWarning) {
                Write-Host ('  WARN    Removable drive: ' + $selected) -ForegroundColor Yellow
            }
        } else {
            $selfHealReason = 'Recorded workspace no longer satisfies the workspace requirements: ' + $hard.Reason
        }
    } else {
        $selfHealReason = 'Recorded workspace path no longer exists: ' + $candidate
    }
} else {
    $selfHealReason = 'No bootstrap pointer found.'
}

if (-not $selected) {
    Write-Host ('  ' + $selfHealReason) -ForegroundColor Yellow
    Write-Host  '  Self-healing to canonical workspace.' -ForegroundColor Yellow
    try {
        $selected = Resolve-CanonicalWorkspace
    } catch {
        Write-Host ''
        Write-Host ('Cannot prepare a workspace: ' + $_.Exception.Message) -ForegroundColor Red
        exit 3
    }
    try {
        Write-BootstrapPointer -WorkspacePath $selected
    } catch {
        Write-Host ''
        Write-Host ('Cannot persist bootstrap pointer: ' + $_.Exception.Message) -ForegroundColor Red
        exit 3
    }
    Write-Host ('  Workspace ready: ' + $selected) -ForegroundColor Green
    Write-Host ('  Bootstrap pointer written: ' + $Script:BootstrapFile) -ForegroundColor Green
}

# =====================================================================
# 5. Bundled-PAX integrity pre-flight
# =====================================================================
#
# The PAX script ships bundled inside the install tree at a fixed path.
# Before launching the native host, verify that the on-disk file matches
# the SHA-256 recorded in VERSION.json. Mismatch refuses to start. This
# is a fast read-only integrity gate; the native host independently
# re-verifies engine/script integrity on every cook spawn.

if (-not (Test-Path -LiteralPath $Script:VersionFile -PathType Leaf)) {
    Write-Host ''
    Write-Host ('VERSION.json not found: ' + $Script:VersionFile) -ForegroundColor Red
    Write-Host 'The install tree is incomplete. Re-run Install-PAXCookbook.ps1.' -ForegroundColor Red
    exit 6
}

if (-not (Test-Path -LiteralPath $Script:PaxScriptPath -PathType Leaf)) {
    Write-Host ''
    Write-Host ('Bundled PAX script not found: ' + $Script:PaxScriptPath) -ForegroundColor Red
    Write-Host 'The install tree is incomplete. Re-run Install-PAXCookbook.ps1.' -ForegroundColor Red
    exit 6
}

try {
    $versionRaw = Get-Content -LiteralPath $Script:VersionFile -Raw -ErrorAction Stop
    $versionDoc = $versionRaw | ConvertFrom-Json -ErrorAction Stop
} catch {
    Write-Host ''
    Write-Host ('VERSION.json is unreadable: ' + $_.Exception.Message) -ForegroundColor Red
    exit 6
}

$expectedSha = $null
if ($versionDoc -and $versionDoc.paxScript) {
    $expectedSha = [string]$versionDoc.paxScript.sha256
}
if ([string]::IsNullOrWhiteSpace($expectedSha)) {
    Write-Host ''
    Write-Host 'VERSION.json is missing paxScript.sha256.' -ForegroundColor Red
    exit 6
}
$expectedSha = $expectedSha.ToUpperInvariant()

try {
    $actualSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $Script:PaxScriptPath -ErrorAction Stop).Hash.ToUpperInvariant()
} catch {
    Write-Host ''
    Write-Host ('Failed to compute SHA-256 of bundled PAX script: ' + $_.Exception.Message) -ForegroundColor Red
    exit 6
}

if ($actualSha -ne $expectedSha) {
    Write-Host ''
    Write-Host 'Bundled PAX script SHA-256 mismatch. Refusing to start.' -ForegroundColor Red
    Write-Host ('  Script:   ' + $Script:PaxScriptPath) -ForegroundColor Red
    Write-Host ('  Expected: ' + $expectedSha) -ForegroundColor Red
    Write-Host ('  Actual:   ' + $actualSha) -ForegroundColor Red
    Write-Host 'Re-run Install-PAXCookbook.ps1 to restore the install tree.' -ForegroundColor Red
    exit 6
}

# =====================================================================
# 6. Launch the native host
# =====================================================================
#
# Production runtime is the native C# host. This launcher does NOT host
# the PowerShell broker. It starts "<AppRoot>\bin\PAX Cookbook.exe",
# passing the validated workspace and the resolved app root, then exits.
#
# The native host owns:
#   - its own WebView application window (no external browser to open),
#   - single-instance reservation + reuse for a given workspace
#     (a second launch wakes the existing window),
#   - port allocation, session token generation, SQLite, and the single
#     gated startCook execution route.
#
# Why --workspace is still passed here: with no --workspace the native host
# now defaults to the canonical %LOCALAPPDATA%\PAXCookbook\Workspace (the
# production workspace), so a flagless launch is already safe. The launcher
# nonetheless passes the validated $selected path explicitly for determinism.
#
# Why --approot is passed explicitly: the native host can auto-discover
# its app root by walking upward for app\web\index.html, but passing the
# already-resolved root is deterministic and avoids any dependency on
# directory-name casing or the working directory.

if (-not (Test-Path -LiteralPath $Script:NativeExe -PathType Leaf)) {
    Write-Host ''
    Write-Host ('Native host not found: ' + $Script:NativeExe) -ForegroundColor Red
    Write-Host 'The install tree is incomplete. Re-run Install-PAXCookbook.ps1.' -ForegroundColor Red
    exit 5
}

# UX-1H10 parity -- detect this launcher's console-window visibility so
# the native host can record/observe launchMode the same way the legacy
# broker did. The normal Start Menu shortcut launches pwsh.exe with
# -WindowStyle Hidden (console hidden); the Support Mode path launches
# visible. Surface the result to the child via PAX_COOKBOOK_LAUNCH_MODE.
$launchMode = 'unknown'
try {
    if (-not ('PaxCookbookLauncher.Win32' -as [type])) {
        Add-Type -Namespace 'PaxCookbookLauncher' -Name 'Win32' -MemberDefinition @'
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern System.IntPtr GetConsoleWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsWindowVisible(System.IntPtr hWnd);
'@
    }
    $hwnd = [PaxCookbookLauncher.Win32]::GetConsoleWindow()
    if ($hwnd -eq [System.IntPtr]::Zero) {
        $launchMode = 'unknown'
    } elseif ([PaxCookbookLauncher.Win32]::IsWindowVisible($hwnd)) {
        $launchMode = 'visible'
    } else {
        $launchMode = 'hidden'
    }
} catch {
    $launchMode = 'unknown'
}
$env:PAX_COOKBOOK_LAUNCH_MODE = $launchMode

Write-Host ''
Write-Host ('  Starting native host: ' + $Script:NativeExe) -ForegroundColor Cyan

# Decide whether to pass --approot. The native host treats an explicit
# --approot as authoritative: if the supplied path does NOT contain
# web\index.html it FAILS HARD (it does not fall back to auto-discovery).
# So only pass --approot when we can confirm <AppRoot>\web\index.html
# exists; otherwise omit it and let the native host resolve its own app
# root by walking upward from the exe. --workspace is ALWAYS passed.
$Script:AppRootIndex = Join-Path $Script:AppRoot (Join-Path 'web' 'index.html')
$passApproot = Test-Path -LiteralPath $Script:AppRootIndex -PathType Leaf

Write-Host ('    --workspace ' + $selected) -ForegroundColor DarkGray
if ($passApproot) {
    Write-Host ('    --approot   ' + $Script:AppRoot) -ForegroundColor DarkGray
} else {
    Write-Host ('    (--approot omitted: web\index.html not found under ' + $Script:AppRoot + '; native host will self-resolve)') -ForegroundColor Yellow
}
Write-Host ''

# Invoke the native host directly instead of detaching with Start-Process.
#
# Why this blocks: the production runtime is the native C# host, not this
# PowerShell bootstrapper. Keeping the hidden launcher process alive while
# the native host runs mirrors the old broker-supervision shape but without
# hosting the PowerShell broker. It also preserves stdout/stderr diagnostics
# and avoids a detached-launch failure mode where the EXE process existed
# but Kestrel never became reachable.
#
# Quoting: use PowerShell's call operator with an argument array so paths
# containing spaces arrive as exact argv elements. Do NOT build a single
# string or shell out through cmd.exe.
try {
    $nativeArgs = @('--workspace', $selected)
    if ($passApproot) {
        $nativeArgs += @('--approot', $Script:AppRoot)
    }

    & $Script:NativeExe @nativeArgs
    $nativeExit = $LASTEXITCODE
    if ($null -eq $nativeExit) { $nativeExit = 0 }
    exit $nativeExit
} catch {
    Write-Host ''
    Write-Host ('Failed to run the native host: ' + $_.Exception.Message) -ForegroundColor Red
    exit 7
}
